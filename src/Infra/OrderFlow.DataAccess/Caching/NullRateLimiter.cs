namespace OrderFlow.DataAccess.Caching;

/// <summary>Allows every call. Used when Redis is not configured (for example in tests).</summary>
public sealed class NullRateLimiter : IRateLimiter
{
    public Task<RateLimitDecision> HitAsync(
        string policy,
        string partition,
        int limit,
        TimeSpan window,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(RateLimitDecision.AllowAll(limit));
}
