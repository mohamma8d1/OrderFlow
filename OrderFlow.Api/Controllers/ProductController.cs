using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Application.DTOs.Product;
using OrderFlow.Domain.Entities;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class ProductController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ProductResponse>> Create(CreateProductDto dto)
    {
        var product = new Product()
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Description = dto.Description,
            Price = dto.Price,
            Quantity = dto.Quantity,
            CategoryId = dto.CategoryId,
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync();

        var response = new ProductResponse(product.Id, product.Name, product.Description, product.Price, product.Quantity, product.CategoryId);

        return Created($"/api/Product/{product.Id}", response);
    }

    [HttpGet]
    public async Task<ActionResult<List<ProductResponse>>> Get()
    {

        var products = await dbContext.Products
                                .AsNoTracking()
                                .Select( p => new ProductResponse(p.Id, p.Name, p.Description, p.Price, p.Quantity, p.CategoryId))
                                .ToListAsync();

        return Ok(products);
    }

    [HttpGet("{guid:id}")]
    public async Task<ActionResult<ProductResponse>> GetById(Guid Id)
    {

        var product = await dbContext.Products
                                .AsNoTracking()
                                .Where( p => p.Id == Id)
                                .Select(p => new ProductResponse(p.Id, p.Name, p.Description, p.Price, p.Quantity, p.CategoryId))
                                .FirstOrDefaultAsync();
        if (product == null)
            return NotFound();

        return Ok(product);
    }
}
