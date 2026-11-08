using OrderFlow.Application.DTOs.Product;

namespace OrderFlow.Application.Interfaces;

/// <summary>
/// Cache for single products (cache-aside pattern).
/// Implementations must never throw: if the cache is down, reads return null
/// and writes are skipped, so the caller simply falls back to the database.
/// </summary>
public interface IProductCache
{
    Task<ProductResponse?> GetAsync(Guid productId, CancellationToken cancellationToken = default);

    Task SetAsync(ProductResponse product, CancellationToken cancellationToken = default);

    Task RemoveAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default);
}
