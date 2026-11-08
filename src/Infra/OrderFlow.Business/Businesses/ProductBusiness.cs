using Microsoft.EntityFrameworkCore;
using OrderFlow.Business.Caching;
using OrderFlow.Business.Contracts;
using OrderFlow.Business.Exceptions;
using OrderFlow.Common.DTOs.Product;
using OrderFlow.Common.Entities;
using OrderFlow.Common.Events;
using OrderFlow.DataAccess.Caching;
using OrderFlow.DataAccess.Core;
using OrderFlow.DataAccess.Messaging;

namespace OrderFlow.Business.Businesses;

public class ProductBusiness(
    ApplicationDbContext dbContext,
    IProductCache productCache,
    IProductChangePublisher productChanges,
    IReadCache readCache,
    CacheOutcome cacheOutcome) : IProductBusiness
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
        await readCache.BumpVersionAsync(CacheScopes.ProductList, cancellationToken);
        await productChanges.PublishAsync(product.Id, ProductChangeKinds.Created, cancellationToken);

        return ToResponse(product);
    }

    /// <summary>
    /// Cached with a version key and a short TTL. Every product change and every order (stock) bumps the version,
    /// so on a busy shop this list is rebuilt often; that is the price of showing quantity in a list.
    /// </summary>
    public Task<List<ProductResponse>> GetAllAsync(CancellationToken cancellationToken = default) =>
        readCache.GetOrLoadAsync(
            cacheOutcome,
            CacheScopes.ProductList,
            "all",
            CacheScopes.ProductListTtl,
            () => dbContext.Products
                .AsNoTracking()
                .Select(p => new ProductResponse(p.Id, p.Name, p.Description, p.Price, p.Quantity, p.CategoryId))
                .ToListAsync(cancellationToken),
            cancellationToken);

    public async Task<ProductLookupResult> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Cache-aside step 1: ask Redis. A hit can be a product OR a cached "not found".
        var cached = await productCache.GetAsync(id, cancellationToken);
        cacheOutcome.Record(cached.IsHit);

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
        await readCache.BumpVersionAsync(CacheScopes.ProductList, cancellationToken);
        await productChanges.PublishAsync(id, ProductChangeKinds.Updated, cancellationToken);

        return ToResponse(product);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await dbContext.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw BusinessException.NotFound();

        dbContext.Products.Remove(product);
        await dbContext.SaveChangesAsync(cancellationToken);

        await productCache.RemoveAsync([id], cancellationToken);
        await readCache.BumpVersionAsync(CacheScopes.ProductList, cancellationToken);
        await productChanges.PublishAsync(id, ProductChangeKinds.Deleted, cancellationToken);
    }

    private static ProductResponse ToResponse(Product p) =>
        new(p.Id, p.Name, p.Description, p.Price, p.Quantity, p.CategoryId);
}
