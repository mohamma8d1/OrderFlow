
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Application.DTOs.Product;
using OrderFlow.Application.Interfaces;
using OrderFlow.Domain.Entities;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Api.Controllers;

[Route("[controller]")]
[ApiController]
public class ProductController(ApplicationDbContext dbContext, IProductCache productCache) : ControllerBase
{
    [Authorize(Roles = "Admin")]
    [HttpPost]
    [Route("[action]")]
    public async Task<ActionResult<ProductResponse>> Create(CreateProductDto dto)
    {
        var category = await dbContext.Categories.FirstOrDefaultAsync(c => c.Id == dto.CategoryId);
        if (category == null)
            return NotFound("Category Not Found!!");

        var product = new Product()
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Description = dto.Description,
            Price = dto.Price,
            Quantity = dto.Quantity,
            CategoryId = dto.CategoryId
        };

        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync();

        var response = new ProductResponse(product.Id, product.Name, product.Description, product.Price, product.Quantity, product.CategoryId);

        return Created($"/api/Product/{product.Id}", response);
    }

    [Authorize]
    [HttpGet]
    [Route("[action]")]
    public async Task<ActionResult<List<ProductResponse>>> Get()
    {

        var products = await dbContext.Products
                                .AsNoTracking()
                                .Select( p => new ProductResponse(p.Id, p.Name, p.Description, p.Price, p.Quantity, p.CategoryId))
                                .ToListAsync();

        return Ok(products);
    }

    [Authorize]
    [HttpGet]
    [Route("[action]/{id:guid}")]
    public async Task<ActionResult<ProductResponse>> GetById(Guid id)
    {
        // Cache-aside step 1: try Redis first.
        var cached = await productCache.GetAsync(id);

        if (cached is not null)
        {
            Response.Headers["X-Cache"] = "HIT";
            return Ok(cached);
        }

        Response.Headers["X-Cache"] = "MISS";

        // Step 2: on a miss, read from the database...
        var product = await dbContext.Products
                                .AsNoTracking()
                                .Where( p => p.Id == id)
                                .Select(p => new ProductResponse(p.Id, p.Name, p.Description, p.Price, p.Quantity, p.CategoryId))
                                .FirstOrDefaultAsync();
        if (product == null)
            return NotFound();

        // Step 3: ...and store the result so the next request is a HIT.
        await productCache.SetAsync(product);

        return Ok(product);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut]
    [Route("[action]/{id:guid}")]
    public async Task<ActionResult<ProductResponse>> Update(Guid id , UpdateProductDto dto)
    {

        var categoryExist = await dbContext.Categories.AnyAsync(c => c.Id == dto.CategoryId);
        if (!categoryExist)
            return BadRequest("Category Not Found!");

        var product = await dbContext.Products.FirstOrDefaultAsync(p => p.Id == id);

        if (product is null)
            return NotFound("Product Not Found!");

        product.Name = dto.Name;
        product.Description = dto.Description;
        product.Price = dto.Price;
        product.Quantity = dto.Quantity;
        product.CategoryId = dto.CategoryId;

        await dbContext.SaveChangesAsync();

        // The product changed, so the cached copy is now wrong: delete it.
        await productCache.RemoveAsync([id]);

        var response = new ProductResponse(product.Id, product.Name, product.Description, product.Price, product.Quantity, product.CategoryId);
        return Ok(response);
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete]
    [Route("[action]/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {

        var product = await dbContext.Products.FirstOrDefaultAsync(p => p.Id == id);
        if (product == null)
            return NotFound();

        dbContext.Products.Remove(product);
        await dbContext.SaveChangesAsync();

        await productCache.RemoveAsync([id]);

        return NoContent();
    }
}
