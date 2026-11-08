using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderFlow.Api.Filters;
using OrderFlow.Business.Contracts;
using OrderFlow.Common.DTOs.Order;
using OrderFlow.Common.DTOs.Pagination;

namespace OrderFlow.Api.Controllers;

public class OrderController(IOrderBusiness orderBusiness) : OrderFlowControllerBase
{
    /// <summary>Rate limited in Redis: 5 orders per user per minute (429 after that).</summary>
    [Authorize]
    [HttpPost("[action]")]
    [RateLimit("order-create", limit: 5, windowSeconds: 60)]
    public async Task<IActionResult> Create(CreateOrderDto dto, CancellationToken cancellationToken)
    {
        var order = await orderBusiness.CreateAsync(CurrentUserId, dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
    }

    [Authorize]
    [HttpPost("[action]/{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        await orderBusiness.CancelAsync(CurrentUserId, id, cancellationToken);
        return NoContent();
    }

    [Authorize]
    [HttpGet("[action]")]
    public async Task<ActionResult<PagedResponse<OrderSummaryResponse>>> GetMyOrders(
        [FromQuery] int page = 1,
        [FromQuery] int size = 10,
        CancellationToken cancellationToken = default) =>
        Ok(await orderBusiness.GetMyOrdersAsync(CurrentUserId, page, size, cancellationToken));

    [Authorize]
    [HttpGet("[action]/{id:guid}")]
    public async Task<ActionResult<OrderDetailsResponse>> GetById(Guid id, CancellationToken cancellationToken) =>
        Ok(await orderBusiness.GetByIdAsync(CurrentUserId, id, cancellationToken));
}
