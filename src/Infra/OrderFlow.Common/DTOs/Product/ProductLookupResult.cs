namespace OrderFlow.Common.DTOs.Product;

/// <param name="Product">The product, or null when it does not exist.</param>
/// <param name="FromCache">True when Redis answered (including a cached "not found").</param>
public sealed record ProductLookupResult(ProductResponse? Product, bool FromCache);
