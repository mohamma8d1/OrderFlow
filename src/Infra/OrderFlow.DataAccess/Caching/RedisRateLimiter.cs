using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace OrderFlow.DataAccess.Caching;

/// <summary>
/// Fixed-window rate limiter. One counter key per (policy, partition):
///
///   orderflow:ratelimit:{policy}:{partition}
///
/// Each call sends three commands in ONE round trip (a pipeline/batch):
///   SET key 0 EX window NX   -> create the counter with its expiry, only if it does not exist yet
///   INCR key                 -> count this call (INCR keeps the existing TTL)
///   PTTL key                 -> milliseconds until the window resets (for Retry-After)
///
/// When the key expires, the window is over and counting starts again from zero.
/// </summary>
public sealed class RedisRateLimiter(
    IConnectionMultiplexer redis,
    ILogger<RedisRateLimiter> logger) : IRateLimiter
{
    public const string KeyPrefix = "orderflow:ratelimit:";

    public static RedisKey KeyFor(string policy, string partition) => $"{KeyPrefix}{policy}:{partition}";

    public async Task<RateLimitDecision> HitAsync(
        string policy,
        string partition,
        int limit,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var key = KeyFor(policy, partition);

        try
        {
            var database = redis.GetDatabase();

            // A batch sends the commands together and in order, instead of waiting for each reply.
            var batch = database.CreateBatch();
            var createTask = batch.StringSetAsync(key, 0, window, When.NotExists);
            var incrementTask = batch.StringIncrementAsync(key);
            var ttlTask = batch.KeyTimeToLiveAsync(key);
            batch.Execute();

            await createTask;
            var count = await incrementTask;
            var ttl = await ttlTask;

            // Safety net: if the key expired between SET NX and INCR, INCR created a fresh key
            // with NO expiry, which would block this user forever. Give it its expiry back.
            if (ttl is null)
            {
                await database.KeyExpireAsync(key, window);
                ttl = window;
            }

            return count <= limit
                ? new RateLimitDecision(true, count, limit, TimeSpan.Zero)
                : new RateLimitDecision(false, count, limit, ttl.Value);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Fail open: if Redis is down we would rather let orders through than block everyone.
            logger.LogWarning(ex, "Rate limiter unavailable for {Policy}; allowing the request.", policy);
            return RateLimitDecision.AllowAll(limit);
        }
    }
}
