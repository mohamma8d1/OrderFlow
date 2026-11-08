namespace OrderFlow.DataAccess.Caching;

public static class ReadCacheExtensions
{
    /// <summary>
    /// Cache-aside on a versioned key: return the cached value, or load it, store it and return it.
    /// <paramref name="load"/> must not return null.
    /// </summary>
    public static async Task<T> GetOrLoadAsync<T>(
        this IReadCache cache,
        CacheOutcome outcome,
        string scope,
        string suffix,
        TimeSpan ttl,
        Func<Task<T>> load,
        CancellationToken cancellationToken = default) where T : class
    {
        var key = await KeyAsync(cache, scope, suffix, cancellationToken);

        var cached = await cache.GetAsync<T>(key, cancellationToken);
        outcome.Record(cached.IsHit);

        if (cached.IsHit)
            return cached.Value!;

        var value = await load();
        await cache.SetAsync(key, value, ttl, cancellationToken);
        return value;
    }

    /// <summary>Same, for single items: a null result ("not found") is returned but not cached.</summary>
    public static async Task<T?> GetOrLoadItemAsync<T>(
        this IReadCache cache,
        CacheOutcome outcome,
        string scope,
        string suffix,
        TimeSpan ttl,
        Func<Task<T?>> load,
        CancellationToken cancellationToken = default) where T : class
    {
        var key = await KeyAsync(cache, scope, suffix, cancellationToken);

        var cached = await cache.GetAsync<T>(key, cancellationToken);
        outcome.Record(cached.IsHit);

        if (cached.IsHit)
            return cached.Value;

        var value = await load();

        if (value is not null)
            await cache.SetAsync(key, value, ttl, cancellationToken);

        return value;
    }

    private static async Task<string> KeyAsync(IReadCache cache, string scope, string suffix, CancellationToken cancellationToken)
    {
        // The version is read on every request: one extra GET, in exchange for invalidation that needs no DEL.
        var version = await cache.GetVersionAsync(scope, cancellationToken);
        return ReadCacheKeys.EntryKey(scope, version, suffix);
    }
}
