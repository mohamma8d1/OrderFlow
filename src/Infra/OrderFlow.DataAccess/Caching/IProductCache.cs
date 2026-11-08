using OrderFlow.Common.DTOs.Product;

namespace OrderFlow.DataAccess.Caching;

/// <summary>
/// What the cache knows about one product id.
/// <list type="bullet">
/// <item><c>IsHit = false</c>: the cache knows nothing; read the database.</item>
/// <item><c>IsHit = true, Product != null</c>: here is the product.</item>
/// <item><c>IsHit = true, Product == null</c>: we recently checked and this product does NOT exist (negative cache).</item>
/// </list>
/// </summary>
public readonly record struct ProductCacheLookup(bool IsHit, ProductResponse? Product)
{
    public static ProductCacheLookup Miss => new(false, null);
    public static ProductCacheLookup NotFound => new(true, null);
    public static ProductCacheLookup Found(ProductResponse product) => new(true, product);
}

/// <summary>
/// Cache for single products (cache-aside pattern).
/// Implementations must never throw: if the cache is down, reads are misses
/// and writes are skipped, so the caller simply falls back to the database.
/// </summary>
public interface IProductCache
{
    Task<ProductCacheLookup> GetAsync(Guid productId, CancellationToken cancellationToken = default);

    Task SetAsync(ProductResponse product, CancellationToken cancellationToken = default);

    /// <summary>Remember for a short time that this product does not exist.</summary>
    Task SetNotFoundAsync(Guid productId, CancellationToken cancellationToken = default);

    Task RemoveAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default);
}
