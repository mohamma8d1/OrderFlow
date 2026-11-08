using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderFlow.Business.Contracts;
using OrderFlow.Common.DTOs.Product;

namespace OrderFlow.Api.Controllers;

public class ProductController(IProductBusiness productBusiness) : OrderFlowControllerBase
{
    [Authorize(Roles = "Admin")]
    [HttpPost("[action]")]
    public async Task<ActionResult<ProductResponse>> Create(CreateProductDto dto, CancellationToken cancellationToken)
    {
        var product = await productBusiness.CreateAsync(dto, cancellationToken);
        return Created($"/api/Product/{product.Id}", product);
    }

    [Authorize]
    [HttpGet("[action]")]
    public async Task<ActionResult<List<ProductResponse>>> Get(CancellationToken cancellationToken) =>
        Ok(await productBusiness.GetAllAsync(cancellationToken));

    [Authorize]
    [HttpGet("[action]/{id:guid}")]
    public async Task<ActionResult<ProductResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        // X-Cache (HIT/MISS) is added by CacheStatusHeaderFilter, like on every other GET.
        var result = await productBusiness.GetByIdAsync(id, cancellationToken);

        return result.Product is null ? NotFound() : Ok(result.Product);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("[action]/{id:guid}")]
    public async Task<ActionResult<ProductResponse>> Update(Guid id, UpdateProductDto dto, CancellationToken cancellationToken) =>
        Ok(await productBusiness.UpdateAsync(id, dto, cancellationToken));

    [Authorize(Roles = "Admin")]
    [HttpDelete("[action]/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await productBusiness.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
