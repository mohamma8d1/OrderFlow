using Microsoft.EntityFrameworkCore;
using OrderFlow.Business.Contracts;
using OrderFlow.Business.Exceptions;
using OrderFlow.Common.DTOs.Product;
using OrderFlow.Common.Entities;
using OrderFlow.DataAccess.Caching;
using OrderFlow.DataAccess.Core;

namespace OrderFlow.Business.Businesses;

public class ProductBusiness(ApplicationDbContext dbContext, IProductCache productCache) : IProductBusiness
{
    public async Task<ProductResponse> CreateAsync(CreateProductDto dto, CancellationToken cancellationToken = default)
    {
        var categoryExists = await dbContext.Categories.AnyAsync(c => c.Id == dto.CategoryId, cancellationToken);
        if (!categoryExists)
            throw BusinessException.NotFound("Category Not Found!!");

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Description = dto.Description,
            Price = dto.Price,
            Quantity = dto.Quantity,
            CategoryId = dto.CategoryId
        };

        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync(cancellationToken);

        // In case someone asked for this id before it existed and a "not found" is cached.
        await productCache.RemoveAsync([product.Id], cancellationToken);

        return ToResponse(product);
    }

    public Task<List<ProductResponse>> GetAllAsync(CancellationToken cancellationToken = default) =>
        dbContext.Products
            .AsNoTracking()
            .Select(p => new ProductResponse(p.Id, p.Name, p.Description, p.Price, p.Quantity, p.CategoryId))
            .ToListAsync(cancellationToken);

    public async Task<ProductLookupResult> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Cache-aside step 1: ask Redis. A hit can be a product OR a cached "not found".
        var cached = await productCache.GetAsync(id, cancellationToken);

        if (cached.IsHit)
            return new ProductLookupResult(cached.Product, FromCache: true);

        // Step 2: miss, so read the database...
        var product = await dbContext.Products
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new ProductResponse(p.Id, p.Name, p.Description, p.Price, p.Quantity, p.CategoryId))
            .FirstOrDefaultAsync(cancellationToken);

        // Step 3: ...and remember the answer. "Not found" is remembered too (negative caching),
        // for a short time, so repeated requests for a bad id do not all hit SQL Server.
        if (product is null)
            await productCache.SetNotFoundAsync(id, cancellationToken);
        else
            await productCache.SetAsync(product, cancellationToken);

        return new ProductLookupResult(product, FromCache: false);
    }

    public async Task<ProductResponse> UpdateAsync(Guid id, UpdateProductDto dto, CancellationToken cancellationToken = default)
    {
        var categoryExists = await dbContext.Categories.AnyAsync(c => c.Id == dto.CategoryId, cancellationToken);
        if (!categoryExists)
            throw BusinessException.BadRequest("Category Not Found!");

        var product = await dbContext.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw BusinessException.NotFound("Product Not Found!");

        product.Name = dto.Name;
        product.Description = dto.Description;
        product.Price = dto.Price;
        product.Quantity = dto.Quantity;
        product.CategoryId = dto.CategoryId;

        await dbContext.SaveChangesAsync(cancellationToken);

        // The product changed, so the cached copy is now wrong: delete it.
        await productCache.RemoveAsync([id], cancellationToken);

        return ToResponse(product);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await dbContext.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw BusinessException.NotFound();

        dbContext.Products.Remove(product);
        await dbContext.SaveChangesAsync(cancellationToken);

        await productCache.RemoveAsync([id], cancellationToken);
    }

    private static ProductResponse ToResponse(Product p) =>
        new(p.Id, p.Name, p.Description, p.Price, p.Quantity, p.CategoryId);
}
