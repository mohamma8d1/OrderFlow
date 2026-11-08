using OrderFlow.Common.DTOs.Product;

namespace OrderFlow.DataAccess.Caching;

/// <summary>
/// "Do nothing" cache used when Redis is not configured (for example in tests).
/// Every read is a miss, so the app always goes to the database.
/// </summary>
public sealed class NullProductCache : IProductCache
{
    public Task<ProductCacheLookup> GetAsync(Guid productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ProductCacheLookup.Miss);

    public Task SetAsync(ProductResponse product, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetNotFoundAsync(Guid productId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RemoveAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
