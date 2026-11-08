namespace OrderFlow.DataAccess.Caching;

/// <summary>Caches nothing. Used when Redis is not configured (for example in tests).</summary>
public sealed class NullReadCache : IReadCache
{
    public Task<CacheLookup<T>> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class =>
        Task.FromResult(new CacheLookup<T>(false, null));

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class =>
        Task.CompletedTask;

    public Task<long> GetVersionAsync(string scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(0L);

    public Task BumpVersionAsync(string scope, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
