using OrderFlow.Common.DTOs.Category;

namespace OrderFlow.Business.Contracts;

public interface ICategoryBusiness
{
    Task<CategoryResponse> CreateAsync(CreateCategoryDto dto, CancellationToken cancellationToken = default);
    Task<List<CategoryResponse>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<CategoryResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CategoryResponse> UpdateAsync(Guid id, CreateCategoryDto dto, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
