using Microsoft.Extensions.Logging;
using OrderFlow.Application.DTOs.Product;
using OrderFlow.Application.Interfaces;
using StackExchange.Redis;
using System.Text.Json;

namespace OrderFlow.Infrastructure.Caching;

/// <summary>
/// Stores each product as a JSON string under "orderflow:cache:product:{id}".
///
/// Redis commands used:
///   GET key                  -> read a cached product
///   SET key value EX seconds -> store it with an expiry (TTL)
///   DEL key1 key2 ...        -> remove products whose data changed
/// </summary>
public sealed class RedisProductCache(
    IConnectionMultiplexer redis,
    ILogger<RedisProductCache> logger) : IProductCache
{
    public const string KeyPrefix = "orderflow:cache:product:";

    // Base lifetime of a cached product. A small random "jitter" is added so that
    // many keys written at the same moment don't all expire at the same moment
    // (which would send a burst of requests to the database).
    public static readonly TimeSpan BaseTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxJitter = TimeSpan.FromSeconds(60);

    public static RedisKey KeyFor(Guid productId) => $"{KeyPrefix}{productId:D}";

    public async Task<ProductResponse?> GetAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(KeyFor(productId));

            if (value.IsNullOrEmpty)
                return null; // cache miss

            return JsonSerializer.Deserialize<ProductResponse>(value.ToString());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            // Fail open: a broken cache must not break the API.
            logger.LogWarning(ex, "Redis read failed for product {ProductId}; falling back to database.", productId);
            return null;
        }
    }

    public async Task SetAsync(ProductResponse product, CancellationToken cancellationToken = default)
    {
        try
        {
            var ttl = BaseTtl + TimeSpan.FromSeconds(Random.Shared.Next(0, (int)MaxJitter.TotalSeconds + 1));

            await redis.GetDatabase().StringSetAsync(
                KeyFor(product.Id),
                JsonSerializer.Serialize(product),
                ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Redis write failed for product {ProductId}.", product.Id);
        }
    }

    public async Task RemoveAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default)
    {
        var keys = productIds.Distinct().Select(KeyFor).ToArray();

        if (keys.Length == 0)
            return;

        try
        {
            // One DEL with many keys = one round trip to Redis.
            await redis.GetDatabase().KeyDeleteAsync(keys);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The entry will still disappear when its TTL runs out.
            logger.LogWarning(ex, "Redis delete failed for {Count} product key(s).", keys.Length);
        }
    }
}
