using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Application.DTOs.Category;
using OrderFlow.Domain.Entities;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CategoryController(ApplicationDbContext dbContext) : ControllerBase
    {
        [HttpPost]
        public async Task<ActionResult<CategoryResponse>> Create(CreateCategoryDto dto)
        {
            var category = new Category()
            {
                Id = Guid.NewGuid(),
                Name = dto.Name,
                CreatedAt = DateTime.UtcNow
            };

            dbContext.Categories.Add(category);
            await dbContext.SaveChangesAsync();

            var response = new CategoryResponse(category.Id, category.Name);

            return Created($"/api/Category/{category.Id}", response);
        }

        [HttpGet]
        public async Task<ActionResult<List<CategoryResponse>>> GetAll()
        {
            var categories = await dbContext.Categories
                .AsNoTracking()
                .Select(c => new CategoryResponse(c.Id, c.Name))
                .ToListAsync();

            return Ok(categories);
        }

        [HttpGet("{id:guid}")]
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

        [HttpPut("{id:guid}")]
        public async Task<ActionResult<CategoryResponse>> Update(Guid id, CreateCategoryDto dto)
        {
            var category = await dbContext.Categories
                .FirstOrDefaultAsync(c => c.Id == id);

            if (category is null)
                return NotFound();

            category.Name = dto.Name;
            category.UpdatedAt = DateTime.UtcNow;

            await dbContext.SaveChangesAsync();


            return Ok(new CategoryResponse(category.Id, category.Name));
        }

        [HttpDelete("{id:guid}")]
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
}
