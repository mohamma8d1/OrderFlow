# OrderFlow

.NET 10 order API (products, categories, orders, JWT users) used to learn Redis: caching, rate limiting and Streams.
Layout mirrors **SamanSalamat.Statistics.Rest** (`Api` + `Infra` + `Tests`).

| Path | Role |
|------|------|
| `src/Api/OrderFlow.Web` | ASP.NET host: `Program`, `DependencyInjectionExtension` (`Inject*` methods), hosted services (`BackgroundServices/`), appsettings. **http://localhost:5187** |
| `src/Infra/OrderFlow.Api` | Controllers (thin, one call to a Business class each), filters (`BusinessExceptionFilter`, `RateLimitAttribute`) |
| `src/Infra/OrderFlow.Business` | Business rules: `Businesses/` (one class per area), `Contracts/` (their interfaces), `Exceptions/BusinessException`, `Services/JwtService` |
| `src/Infra/OrderFlow.Common` | Entities, enums, DTOs, events. Depends on nothing |
| `src/Infra/OrderFlow.DataAccess` | EF Core context (`Core/`, used directly by Business, with no repositories), mappings (`Mapping/`), migrations, Redis cache and rate limiter (`Caching/`), Redis Streams outbox/consumer (`Messaging/`) |
| `src/Tests/OrderFlow.Tests` | API tests (WebApplicationFactory + in-memory SQLite). No Docker needed |
| `src/Tests/OrderFlow.IntegrationTests` | Tests against a real Redis (Testcontainers, needs Docker) |

References point one way: `Web → Api → Business → DataAccess → Common`.

## Quick start

```bash
dotnet run --project src/Api/OrderFlow.Web
```

- Swagger (Development): http://localhost:5187/swagger
- Needs SQL Server (`ConnectionStrings:DefaultConnection`) and Redis (`Redis:ConnectionString` in `appsettings.Development.json`).

## How a request flows

```
OrderController.Create            (Api: reads the user id, calls the business)
  └─ [RateLimit] filter           (Api: Redis counter, 429 when over the limit)
  └─ OrderBusiness.CreateAsync    (Business: rules, EF Core transaction, outbox row)
       └─ throws BusinessException.Conflict("Insufficient Stock")
  └─ BusinessExceptionFilter      (Api: BusinessException → 409 + message)
```

Controllers never return `BadRequest(...)`/`NotFound(...)` for rules. The business throws a `BusinessException`
(`BadRequest`, `Unauthorized`, `NotFound`, `Conflict`), and `BusinessExceptionFilter` maps it to 400/401/404/409.
`DependencyInjectionExtension.InjectBusinesses` registers every class in `OrderFlow.Business.Businesses` automatically.

## Redis

| Feature | Redis | Code |
|---------|-------|------|
| Product cache (cache-aside) | `GET` / `SETEX` / `UNLINK` | `DataAccess/Caching/RedisProductCache.cs`, used by `ProductBusiness` |
| Negative caching | `SETEX key 30 __not_found__` | same class, `SetNotFoundAsync` |
| Rate limiting (`Order/Create`: 5/min per user) | `SET NX EX` + `INCR` + `PTTL` | `DataAccess/Caching/RedisRateLimiter.cs`, `Api/Filters/RateLimitAttribute.cs` |
| Order events | Streams: `XADD`, `XREADGROUP`, `XACK`, `XAUTOCLAIM` | `DataAccess/Messaging/` |

Full walkthrough with hands-on steps: [docs/redis-caching.md](docs/redis-caching.md).

In the `Testing` environment Redis is not registered: `NullProductCache` and `NullRateLimiter` are used instead.

## Tests

```bash
dotnet test src/Tests/OrderFlow.Tests
dotnet test src/Tests/OrderFlow.IntegrationTests
```
