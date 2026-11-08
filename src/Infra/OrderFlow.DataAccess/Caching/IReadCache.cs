namespace OrderFlow.DataAccess.Caching;

public readonly record struct CacheLookup<T>(bool IsHit, T? Value);

/// <summary>
/// General read cache for GET endpoints, built on "version keys".
///
/// Every group of related entries (a "scope", e.g. "products:list" or "orders:user:{id}") has a version counter:
///   orderflow:cache:ver:{scope}               -> 7
/// and its entries include that version in their key:
///   orderflow:cache:{scope}:v7:{suffix}       -> JSON
///
/// To invalidate the whole group after a write, INCR the counter. Readers now build "v8" keys,
/// so every "v7" entry is unreachable at once and simply expires by its TTL. No key has to be found or deleted.
///
/// Implementations must never throw: if Redis is down, reads are misses and writes are skipped.
/// </summary>
public interface IReadCache
{
    Task<CacheLookup<T>> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Current version of a scope (0 when it was never bumped).</summary>
    Task<long> GetVersionAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>Invalidates every entry of a scope.</summary>
    Task BumpVersionAsync(string scope, CancellationToken cancellationToken = default);
}

public static class ReadCacheKeys
{
    public const string Prefix = "orderflow:cache:";

    public static string VersionKey(string scope) => $"{Prefix}ver:{scope}";

    public static string EntryKey(string scope, long version, string suffix) => $"{Prefix}{scope}:v{version}:{suffix}";
}

/// <summary>
/// Remembers whether this request was answered from the cache, so the X-Cache response header can report it.
/// One per request (scoped).
/// </summary>
public sealed class CacheOutcome
{
    public bool? IsHit { get; private set; }

    public void Record(bool isHit) => IsHit = isHit;
}
