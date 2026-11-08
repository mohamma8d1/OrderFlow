using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderFlow.Business.Contracts;
using OrderFlow.Common.DTOs.Category;

namespace OrderFlow.Api.Controllers;

public class CategoryController(ICategoryBusiness categoryBusiness) : OrderFlowControllerBase
{
    [Authorize(Roles = "Admin")]
    [HttpPost("[action]")]
    public async Task<ActionResult<CategoryResponse>> Create(CreateCategoryDto dto, CancellationToken cancellationToken)
    {
        var category = await categoryBusiness.CreateAsync(dto, cancellationToken);
        return Created($"/api/Category/{category.Id}", category);
    }

    [Authorize]
    [HttpGet("[action]")]
    public async Task<ActionResult<List<CategoryResponse>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await categoryBusiness.GetAllAsync(cancellationToken));

    [Authorize]
    [HttpGet("[action]/{id:guid}")]
    public async Task<ActionResult<CategoryResponse>> GetById(Guid id, CancellationToken cancellationToken) =>
        Ok(await categoryBusiness.GetByIdAsync(id, cancellationToken));

    [Authorize(Roles = "Admin")]
    [HttpPut("[action]/{id:guid}")]
    public async Task<ActionResult<CategoryResponse>> Update(Guid id, CreateCategoryDto dto, CancellationToken cancellationToken) =>
        Ok(await categoryBusiness.UpdateAsync(id, dto, cancellationToken));

    [Authorize(Roles = "Admin")]
    [HttpDelete("[action]/{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await categoryBusiness.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
