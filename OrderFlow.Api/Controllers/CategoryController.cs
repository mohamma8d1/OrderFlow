using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Application.DTOs.Category;
using OrderFlow.Domain.Entities;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class CategoryController(ApplicationDbContext dbContext) : ControllerBase
{
    [Authorize(Roles = "Admin")]
    [HttpPost]
    [Route("[action]")]
    public async Task<ActionResult<CategoryResponse>> Create(CreateCategoryDto dto)
    {
        var category = new Category()
        {
            Id = Guid.NewGuid(),
            Name = dto.Name
        };

        dbContext.Categories.Add(category);
        await dbContext.SaveChangesAsync();

        var response = new CategoryResponse(category.Id, category.Name);

        return Created($"/api/Category/{category.Id}", response);
    }

    [Authorize]
    [HttpGet]
    [Route("[action]")]
    public async Task<ActionResult<List<CategoryResponse>>> GetAll()
    {
        var categories = await dbContext.Categories
            .AsNoTracking()
            .Select(c => new CategoryResponse(c.Id, c.Name))
            .ToListAsync();

        return Ok(categories);
    }

    [Authorize]
    [HttpGet]
    [Route("[action]/{id:guid}")]
    public async Task<ActionResult<CategoryResponse>> GetById(Guid id)
    {
        var category = await dbContext.Categories
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new CategoryResponse(c.Id, c.Name))
            .FirstOrDefaultAsync();

        if (category is null)
            return NotFound();

        return Ok(category);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut]
    [Route("[action]/{id:guid}")]

    public async Task<ActionResult<CategoryResponse>> Update(Guid id, CreateCategoryDto dto)
    {
        var category = await dbContext.Categories
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category is null)
            return NotFound();

        category.Name = dto.Name;

        await dbContext.SaveChangesAsync();


        return Ok(new CategoryResponse(category.Id, category.Name));
    }


    [Authorize(Roles = "Admin")]
    [HttpDelete]
    [Route("[action]/{id:guid}")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var category = await dbContext.Categories
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category is null)
            return NotFound();

        dbContext.Categories.Remove(category);
        await dbContext.SaveChangesAsync();


        return NoContent();
    }
}
