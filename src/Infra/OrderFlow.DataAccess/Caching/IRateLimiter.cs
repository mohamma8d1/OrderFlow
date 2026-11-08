namespace OrderFlow.DataAccess.Caching;

/// <param name="IsAllowed">False when the caller went over the limit.</param>
/// <param name="Count">How many calls were counted in the current window, including this one.</param>
/// <param name="Limit">The allowed number of calls per window.</param>
/// <param name="RetryAfter">How long until the window resets (zero when allowed).</param>
public readonly record struct RateLimitDecision(bool IsAllowed, long Count, int Limit, TimeSpan RetryAfter)
{
    public long Remaining => Math.Max(0, Limit - Count);

    public static RateLimitDecision AllowAll(int limit) => new(true, 0, limit, TimeSpan.Zero);
}

/// <summary>
/// Counts calls per (policy, partition) inside a time window.
/// Implementations must never throw: if the store is down, calls are allowed (fail open).
/// </summary>
public interface IRateLimiter
{
    /// <param name="policy">What is limited, for example "order-create".</param>
    /// <param name="partition">Who is limited, for example "user:{id}" or "ip:1.2.3.4".</param>
    Task<RateLimitDecision> HitAsync(
        string policy,
        string partition,
        int limit,
        TimeSpan window,
        CancellationToken cancellationToken = default);
}
