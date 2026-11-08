# Redis in OrderFlow: cache, negative cache, rate limiter

OrderFlow uses Redis in four ways:

| Use | Redis feature | Code |
|---|---|---|
| Order events (outbox → consumer) | **Streams** + consumer groups | `src/Infra/OrderFlow.DataAccess/Messaging/` |
| Faster product reads | **Strings with TTL** (a cache) | `src/Infra/OrderFlow.DataAccess/Caching/RedisProductCache.cs` |
| Fewer lookups of ids that don't exist | **Negative caching** (a marker string, short TTL) | same class, `SetNotFoundAsync` |
| Limit `Order/Create` per user | **Counter + TTL** (fixed window) | `Caching/RedisRateLimiter.cs` + `src/Infra/OrderFlow.Api/Filters/RateLimitAttribute.cs` |

This guide covers the last three.

---

## 1. Cache-aside

The database is the source of truth. Redis keeps a *copy* of hot data in memory.
The logic lives in `ProductBusiness.GetByIdAsync`:

```
GET /Product/GetById/{id}
        │
        ▼
  Redis: GET orderflow:cache:product:{id}
        │
   product JSON? ──► return it                     (X-Cache: HIT, 200)
   "__not_found__"? ──► return 404                 (X-Cache: HIT, 404)   ← negative cache
        │
     nothing (miss)
        ▼
  SQL Server: SELECT product
        │
   found?     → SETEX key 600 <json>               (X-Cache: MISS, 200)
   not found? → SETEX key 30 __not_found__         (X-Cache: MISS, 404)
```

When the product changes, we **delete** the key. The next read is a MISS and loads the new data.

> In `MONITOR` you'll see StackExchange.Redis send `SETEX key 600 value`
> (same as `SET key value EX 600`) and `UNLINK key` instead of `DEL key`. `UNLINK` removes the key
> right away but frees its memory in a background thread, so it never blocks Redis.

Where the key is deleted (`productCache.RemoveAsync`):

| Business method | Why |
|---|---|
| `ProductBusiness.CreateAsync` | clears a cached "not found" for that id, if any |
| `ProductBusiness.UpdateAsync` | name/price/quantity changed |
| `ProductBusiness.DeleteAsync` | product is gone |
| `OrderBusiness.CreateAsync` | stock (`Quantity`) went **down** |
| `OrderBusiness.CancelAsync` | stock went back **up** |

The order ones are easy to forget, and they're the classic caching bug:
anything that changes the data must also clear the cache.

### Design choices (and why)

- **Key naming: `orderflow:cache:product:{id}`.** Use `:` to build a hierarchy. You can
  then scan by prefix, and restrict an ACL user to `~orderflow:*`.
- **Value is JSON.** Simple and readable in `redis-cli`. A Redis *Hash* (`HSET`) would also work
  and lets you update one field, but we always read the whole product.
- **TTL = 10 min + 0–60 s random jitter.** The TTL is a safety net: even if an invalidation is
  missed, stale data disappears on its own. Jitter stops thousands of keys from expiring in the
  same second (a "cache stampede").
- **Delete, don't update, on change.** Deleting is simpler and safer than writing the new value
  (with two concurrent writers, updating can leave the *older* value in the cache).
- **Invalidate after `CommitAsync()`.** If we deleted before the commit, another request could read
  the old row and re-cache it before the commit lands.
- **Fail open.** If Redis is down, `RedisProductCache` logs a warning and the app reads from SQL.
  The cache makes the app faster but must never be the reason it breaks.
- **Only `GetById` is cached, not the full list.** A list changes whenever *any* product changes,
  so invalidating it correctly is much harder. Start with single items.
- **`IProductCache` lives in DataAccess and is used by Business.** Controllers don't know Redis exists.
  In tests (`Testing` env) a `NullProductCache` is used instead.

---

## 2. Negative caching

Without it, a client asking for a product id that doesn't exist (a typo, a deleted product, a bot
trying random ids) hits SQL Server **every time**: there's nothing to cache.

With it, the first miss stores a marker:

```
SETEX orderflow:cache:product:{id} 30 __not_found__
```

For the next 30 seconds, the same id is answered by Redis with a 404 (`X-Cache: HIT`).

Things to notice in the code:

- **A cache lookup now has three answers, not two.** `ProductCacheLookup` is either a *miss*, a *hit with a product*,
  or a *hit that says "doesn't exist"*. A plain `ProductResponse?` couldn't tell "not cached" from "cached as missing".
- **Much shorter TTL (30 s vs 10 min).** If a product appears in a way that skips our code (an import script,
  a manual SQL insert), users see it after at most 30 seconds.
- **`ProductBusiness.CreateAsync` deletes the key.** If someone asked for that id before it existed, the marker is cleared.
- **The marker can't clash with real data.** A product is stored as JSON (`{...}`); `__not_found__` can never be valid JSON for one.

---

## 3. Rate limiting (fixed window)

`Order/Create` allows **5 orders per user per minute**. The 6th gets `429 Too Many Requests` with a `Retry-After` header.

```csharp
[RateLimit("order-create", limit: 5, windowSeconds: 60)]
public async Task<IActionResult> Create(...)
```

One counter key per policy and user (or per IP address when nobody is signed in):

```
orderflow:ratelimit:order-create:user:{userId}
```

Each request sends three commands in **one round trip** (a *batch*, also called pipelining):

```
SET   key 0 EX 60 NX   → create the counter with a 60 s expiry, ONLY if it doesn't exist
INCR  key              → +1 for this request (INCR keeps the existing expiry)
PTTL  key              → milliseconds left in the window (used for Retry-After)
```

- Count ≤ limit → allowed. The response has `X-RateLimit-Limit` and `X-RateLimit-Remaining` headers.
- Count > limit → 429, `Retry-After: <seconds left>`. The action (and the database) never runs.
- When the key expires, the window is over and the next request starts again at 1.

Things to notice in the code:

- **Why `SET ... NX EX` before `INCR`?** The naive version is `INCR` then `EXPIRE`. If the app crashes between
  the two, the key has no expiry and that user is blocked **forever**. Creating the key with its expiry first avoids that.
- **The safety net.** There's still a tiny race: the key can expire in the microsecond between `SET NX` and `INCR`,
  and then `INCR` creates a fresh key with no TTL. `RedisRateLimiter` checks `PTTL` and puts the expiry back if it's missing
  (`CounterWithoutTtl_GetsItsTtlBack` tests this).
- **Why Redis and not in-memory?** With two API instances behind a load balancer, an in-memory counter lets a user
  make 5 + 5 orders. A counter in Redis is shared.
- **Fail open.** If Redis is down, requests are allowed. Blocking all orders because the limiter broke would be worse.
- **Fixed-window weakness.** A user can make 5 orders at 0:59 and 5 more at 1:01, which is 10 in two seconds. A *sliding window*
  (a sorted set of timestamps: `ZADD` + `ZREMRANGEBYSCORE` + `ZCARD`) fixes that. It's a good next exercise.
- **Why not MULTI/EXEC or a Lua script?** Both would make the three commands atomic. But your `orderflow-app` ACL user
  doesn't have `@transaction` or `@scripting`, and the safety net above makes the batch good enough.

---

## 4. One-time setup: the Redis user

The API logs in as `orderflow-app`. If it's disabled or missing permissions, run:

```bash
docker exec my-redis redis-cli ACL SETUSER orderflow-app reset on ">OrderFlowDev2026" "~orderflow:*" "&orderflow:*" +@read +@write +@stream +@pubsub +@connection +info +client
```

| Part | Meaning |
|---|---|
| `reset` | start from a clean user |
| `on` | enable it |
| `>OrderFlowDev2026` | add this password |
| `~orderflow:*` | may only touch keys starting with `orderflow:` |
| `&orderflow:*` | may only use Pub/Sub channels starting with `orderflow:` |
| `+@read +@write +@stream ...` | allowed command categories |

> ⚠️ The container has no volume/config file, so ACL users are lost if you recreate it.
> To keep them, run Redis with a mounted `users.acl` (`--aclfile /data/users.acl`) and call `ACL SAVE`.

---

## 5. Try it yourself

Start the API, then watch every Redis command in a second terminal:

```bash
dotnet run --project src/Api/OrderFlow.Web
```

```bash
docker exec -it my-redis redis-cli MONITOR
```

Then in Swagger (http://localhost:5187/swagger):

1. **Cache:** `GET /Product/GetById/{id}` twice. You get `X-Cache: MISS`, then `HIT`.
2. **Invalidation:** create an order for that product, then call `GetById`. It's a MISS with the new `quantity`; MONITOR shows `UNLINK`.
3. **Negative cache:** `GetById` with a made-up guid twice. You get 404 + `MISS`, then 404 + `HIT`; MONITOR shows `SETEX ... 30 __not_found__`.
4. **Rate limit:** `POST /Order/Create` 6 times quickly. The 6th is **429** with `Retry-After`. Watch `X-RateLimit-Remaining` count down.

Inspect keys:

```bash
docker exec -it my-redis redis-cli --user orderflow-app --pass OrderFlowDev2026
```

```
SCAN 0 MATCH orderflow:* COUNT 100
GET orderflow:cache:product:<id>
TTL orderflow:cache:product:<id>                    # seconds left
GET orderflow:ratelimit:order-create:user:<userId>  # how many orders this window
TTL orderflow:ratelimit:order-create:user:<userId>  # seconds until it resets
DEL orderflow:ratelimit:order-create:user:<userId>  # reset your own limit
```

Try **fail-open**: `docker stop my-redis`. `GetById` still works (always MISS) and `Order/Create` is never limited.
Then `docker start my-redis`.

---

## 6. Tests

All in `src/Tests/OrderFlow.IntegrationTests` (real Redis via Testcontainers, needs Docker):

- `RedisProductCacheTests`: miss, set/get, TTL range, not-found marker + its short TTL, overwrite marker,
  multi-key delete, corrupt JSON, Redis unreachable.
- `RedisRateLimiterTests`: allow up to the limit then block, counter TTL, separate users/policies,
  window reset, missing-TTL safety net, Redis unreachable.
- `ProductCacheApiTests`: the real API + real Redis. MISS→HIT, negative cache MISS→HIT, delete/update/order invalidation,
  and the 6th order getting 429 while another user is unaffected.

```bash
dotnet test src/Tests/OrderFlow.IntegrationTests --filter "FullyQualifiedName~Redis|FullyQualifiedName~ProductCache"
```

---

## 7. Next things to learn with this code

1. **Cache stampede protection.** When a hot key expires, 100 requests all hit SQL at once. Fix it with a short
   lock (`SET lock:key 1 NX EX 5`) so only one request rebuilds the value.
2. **Sliding-window rate limit.** Replace the counter with a sorted set of request timestamps.
3. **Hashes.** Store the product with `HSET` and read single fields with `HGET`.
4. **Sorted sets.** `ZINCRBY orderflow:top-products 1 {productId}` on every order gives you a live
   "best sellers" list with `ZREVRANGE ... 0 9 WITHSCORES`.
