using OrderFlow.Common.DTOs.Product;

namespace OrderFlow.Business.Contracts;

public interface IProductBusiness
{
    Task<ProductResponse> CreateAsync(CreateProductDto dto, CancellationToken cancellationToken = default);
    Task<List<ProductResponse>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Cache-aside read. <see cref="ProductLookupResult.Product"/> is null when the product does not exist.</summary>
    Task<ProductLookupResult> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ProductResponse> UpdateAsync(Guid id, UpdateProductDto dto, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
