using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;

namespace OrderFlow.DataAccess.Caching;

/// <summary>
/// Redis commands used:
///   GET  orderflow:cache:ver:{scope}             -> current version (missing = 0)
///   GET  orderflow:cache:{scope}:v{n}:{suffix}   -> cached JSON
///   SETEX ... ttl json                           -> store an entry
///   INCR orderflow:cache:ver:{scope}             -> invalidate the scope
/// </summary>
public sealed class RedisReadCache(
    IConnectionMultiplexer redis,
    ILogger<RedisReadCache> logger) : IReadCache
{
    public async Task<CacheLookup<T>> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);

            if (value.IsNullOrEmpty)
                return new CacheLookup<T>(false, null);

            var result = JsonSerializer.Deserialize<T>(value.ToString());

            return result is null ? new CacheLookup<T>(false, null) : new CacheLookup<T>(true, result);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            logger.LogWarning(ex, "Redis read failed for {Key}; falling back to database.", key);
            return new CacheLookup<T>(false, null);
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(value), ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Redis write failed for {Key}.", key);
        }
    }

    public async Task<long> GetVersionAsync(string scope, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(ReadCacheKeys.VersionKey(scope));

            return value.TryParse(out long version) ? version : 0;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Redis version read failed for {Scope}.", scope);
            return 0;
        }
    }

    public async Task BumpVersionAsync(string scope, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().StringIncrementAsync(ReadCacheKeys.VersionKey(scope));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Old entries stay readable until their TTL runs out; keep TTLs short for that reason.
            logger.LogWarning(ex, "Redis version bump failed for {Scope}; cached entries may be stale until they expire.", scope);
        }
    }
}
