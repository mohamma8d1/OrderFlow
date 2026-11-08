# Redis product cache — how it works and how to play with it

OrderFlow uses Redis in two ways:

| Use | Redis feature | Code |
|---|---|---|
| Order events (outbox → consumer) | **Streams** + consumer groups | `Infrastructure/Messaging/` |
| Faster product reads | **Strings with TTL** (a cache) | `Infrastructure/Caching/RedisProductCache.cs` |

This guide is about the second one.

---

## 1. The idea: cache-aside

The database is the source of truth. Redis keeps a *copy* of hot data in memory.

```
GET /Product/GetById/{id}
        │
        ▼
  Redis: GET orderflow:cache:product:{id}
        │
   found? ──yes──► return it            (X-Cache: HIT)
        │
        no
        ▼
  SQL Server: SELECT product
        │
  Redis: SET orderflow:cache:product:{id} <json> EX 600   (X-Cache: MISS)
        │
        ▼
     return it
```

When the product changes, we **delete** the key. The next read is a MISS and loads the new data.

> What you'll actually see in `MONITOR`: StackExchange.Redis sends `SETEX key 600 value`
> (same as `SET key value EX 600`) and `UNLINK key` instead of `DEL key`. `UNLINK` removes the key
> right away but frees its memory in a background thread, so it never blocks Redis.

Where the key is deleted (`productCache.RemoveAsync`):

| Endpoint | Why |
|---|---|
| `Product/Update` | name/price/quantity changed |
| `Product/Delete` | product is gone |
| `Order/Create` | stock (`Quantity`) went **down** |
| `Order/Cancel` | stock went back **up** |

The last two are easy to forget, and they're the classic caching bug:
anything that changes the data must also clear the cache.

---

## 2. Design choices (and why)

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
- **`IProductCache` lives in Application, the Redis class in Infrastructure.** Controllers don't
  know Redis exists. In tests (`Testing` env) a `NullProductCache` is used instead.

---

## 3. One-time setup: fix the Redis user

Your `my-redis` container's `orderflow-app` user is currently **disabled with no password**, so the API
can't log in. Run this once:

```bash
docker exec my-redis redis-cli ACL SETUSER orderflow-app reset on ">OrderFlowDev2026" "~orderflow:*" "&orderflow:*" +@read +@write +@stream +@pubsub +@connection +info +client
```

What that means:

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

## 4. Try it yourself

Start the API (`dotnet run --project OrderFlow.Api`). In a second terminal, open `redis-cli` and watch every command:

```bash
docker exec -it my-redis redis-cli MONITOR
```

Then in Swagger:

1. Call `GET /Product/GetById/{id}`. The response header says **`X-Cache: MISS`**.
   In MONITOR you'll see `GET` and then `SETEX ... 6xx`.
2. Call it again: **`X-Cache: HIT`**, with only a `GET` and no SQL query.
3. Create an order for that product, then call `GetById` again. It's a MISS with the new `quantity`.
   MONITOR shows the `UNLINK`.

Inspect keys in another terminal:

```bash
docker exec -it my-redis redis-cli
```

```
KEYS orderflow:cache:*          # fine for learning; use SCAN in production
GET orderflow:cache:product:<id>
TTL orderflow:cache:product:<id>     # seconds left
DEL orderflow:cache:product:<id>     # force a MISS
```

Try **fail-open**: `docker stop my-redis`, call `GetById`. It still works (always MISS, warning in the logs).
Then `docker start my-redis`.

---

## 5. Tests

- `RedisProductCacheTests`: the cache class against a real Redis (Testcontainers). Covers miss, set/get,
  TTL range, multi-key delete, corrupt JSON, and Redis unreachable.
- `ProductCacheApiTests`: the real API plus real Redis. Covers MISS→HIT, 404 not cached, update invalidates,
  and order create/cancel invalidates stock.

```bash
dotnet test OrderFlow.Api.Tests --filter "FullyQualifiedName~ProductCache"
```

---

## 6. Next things to learn with this code

1. **Cache stampede protection.** When a hot key expires, 100 requests all hit SQL at once. Fix it with a short
   lock (`SET lock:key 1 NX EX 5`) so only one request rebuilds the value.
2. **Negative caching.** Cache "not found" for 30 s so repeated lookups of a bad id don't hit SQL.
3. **Hashes.** Store the product with `HSET` and read single fields with `HGET`.
4. **Rate limiting.** `INCR orderflow:rl:{userId}:{minute}` + `EXPIRE`: limit `Order/Create` to N per minute.
5. **Sorted sets.** `ZINCRBY orderflow:top-products 1 {productId}` on every order gives you a live
   "best sellers" list with `ZREVRANGE ... 0 9 WITHSCORES`.
