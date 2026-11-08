using OrderFlow.Application.DTOs.Product;
using OrderFlow.Application.Interfaces;

namespace OrderFlow.Infrastructure.Caching;

/// <summary>
/// "Do nothing" cache used when Redis is not configured (for example in tests).
/// Every read is a miss, so the app always goes to the database.
/// </summary>
public sealed class NullProductCache : IProductCache
{
    public Task<ProductResponse?> GetAsync(Guid productId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ProductResponse?>(null);

    public Task SetAsync(ProductResponse product, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RemoveAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
