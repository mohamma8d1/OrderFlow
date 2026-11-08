using Microsoft.EntityFrameworkCore;
using OrderFlow.Business.Contracts;
using OrderFlow.Business.Exceptions;
using OrderFlow.Common.DTOs.Category;
using OrderFlow.Common.Entities;
using OrderFlow.DataAccess.Core;

namespace OrderFlow.Business.Businesses;

public class CategoryBusiness(ApplicationDbContext dbContext) : ICategoryBusiness
{
    public async Task<CategoryResponse> CreateAsync(CreateCategoryDto dto, CancellationToken cancellationToken = default)
    {
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = dto.Name
        };

        dbContext.Categories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CategoryResponse(category.Id, category.Name);
    }

    public Task<List<CategoryResponse>> GetAllAsync(CancellationToken cancellationToken = default) =>
        dbContext.Categories
            .AsNoTracking()
            .Select(c => new CategoryResponse(c.Id, c.Name))
            .ToListAsync(cancellationToken);

    public async Task<CategoryResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var category = await dbContext.Categories
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new CategoryResponse(c.Id, c.Name))
            .FirstOrDefaultAsync(cancellationToken);

        return category ?? throw BusinessException.NotFound();
    }

    public async Task<CategoryResponse> UpdateAsync(Guid id, CreateCategoryDto dto, CancellationToken cancellationToken = default)
    {
        var category = await dbContext.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw BusinessException.NotFound();

        category.Name = dto.Name;

        await dbContext.SaveChangesAsync(cancellationToken);

        return new CategoryResponse(category.Id, category.Name);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var category = await dbContext.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw BusinessException.NotFound();

        dbContext.Categories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
