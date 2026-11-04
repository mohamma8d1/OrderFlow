using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Application.DTOs.Order;
using OrderFlow.Application.DTOs.OrderItem;
using OrderFlow.Application.DTOs.Pagination;
using OrderFlow.Application.Events;
using OrderFlow.Domain.Entities;
using OrderFlow.Domain.Enums;
using OrderFlow.Infrastructure.Persistence;
using OrderFlow.Infrastructure.Persistence.Outbox;
using StackExchange.Redis;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;

namespace OrderFlow.Api.Controllers;

[Route("[controller]")]
[ApiController]
public class OrderController(ApplicationDbContext dbContext) : ControllerBase
{
    [Authorize]
    [HttpPost]
    [Route("[action]")]
    public async Task<IActionResult> Create(CreateOrderDto dto)
    {
        if (dto.Items is null || dto.Items.Count == 0)
            return BadRequest("Order must contain at least one item.");

        var hasDuplicatedProduct = dto.Items
            .GroupBy(x => x.ProductId)
            .Any(x => x.Count() > 1);

        if (hasDuplicatedProduct)
            return Conflict("duplicate item requested!");

        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);

        if (!Guid.TryParse(userId, out var parsedUserId))
            return Unauthorized();

        var createOrder = new Domain.Entities.Order
        {
            Id = Guid.NewGuid(),
            UserId = parsedUserId,
            Status = OrderStatus.Pending,
        };


        decimal totalAmount = 0;

        var productIds = dto.Items
            .Select(x => x.ProductId).ToList();

        var products  =  await dbContext.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync();


        if (products.Count != productIds.Count)
            return NotFound("One or more products were not found.");

        var productsById = products.ToDictionary(p => p.Id);
        
        if (dto.Items.Any(item => item.Quantity <= 0))
            return BadRequest("Quantity must be greater than zero.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        dbContext.Orders.Add(createOrder);

        foreach (var item in dto.Items) {

            var product = productsById[item.ProductId];

            var affectedRows = await dbContext.Products
                .Where(p  => (p.Id == item.ProductId && p.Quantity >= item.Quantity))
                .ExecuteUpdateAsync(setters =>
                            setters.SetProperty(
                                p => p.Quantity,
                                p => p.Quantity - item.Quantity)
                            .SetProperty(p => p.UpdatedAt, (DateTime?)DateTime.UtcNow));

            if (affectedRows == 0)
            {
                await transaction.RollbackAsync();
                return Conflict("Insufficient Stock");
            }

            var orderItem = new OrderItem
            {
                Id= Guid.NewGuid(),
                OrderId= createOrder.Id,
                ProductId= product.Id,
                Quantity = item.Quantity,
                UnitPrice = product.Price
            };
      
            totalAmount += product.Price * item.Quantity;

            dbContext.OrderItems.Add(orderItem);
        }

        createOrder.TotalAmount = totalAmount;

        var eventId = Guid.NewGuid();
        var occurredAtUtc = DateTime.UtcNow;

        var orderCreatedEvent = new OrderCreatedEvent(
            EventId: eventId,
            OrderId: createOrder.Id,
            UserId: parsedUserId,
            TotalAmount: createOrder.TotalAmount,
            OccurredAtUtc: occurredAtUtc
        );

        var outboxMessage = new OutboxMessage
        {
            Id = eventId,
            EventType = "OrderCreated",
            Payload = JsonSerializer.Serialize(orderCreatedEvent),
            OccurredAtUtc = occurredAtUtc
        };

        dbContext.OutboxMessages.Add(outboxMessage);

        await dbContext.SaveChangesAsync();

        await transaction.CommitAsync();


        return CreatedAtAction(
                nameof(GetById),
                new { id = createOrder.Id },
                new
                {
                    createOrder.Id,
                    createOrder.TotalAmount,
                    createOrder.Status
                });
    }


    [Authorize]
    [HttpPost]
    [Route("[action]/{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(userId, out var parsedUserId))
            return Unauthorized();

        var order = await dbContext.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && o.UserId == parsedUserId);

        if(order is null)
            return NotFound("Order Not Found!");

        if (order.Status != OrderStatus.Pending)
            return Conflict("Order Processed and cant cancel it now!");



        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var affectedRows = await dbContext.Orders
            .Where(o => o.Id == id
                        && o.UserId == parsedUserId
                        && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, OrderStatus.Cancelled)
                .SetProperty(o => o.UpdatedAt, (DateTime?)DateTime.UtcNow));

        if(affectedRows == 0)
        {
            await transaction.RollbackAsync();
            return Conflict();
        }

        var orderItems = await dbContext.OrderItems
            .AsNoTracking()
            .Where(order => order.OrderId == id)
            .ToListAsync();


        foreach(var item in orderItems)
        {

            var quantityAffectedRows = await dbContext.Products
                .Where(p => p.Id == item.ProductId)
                .ExecuteUpdateAsync(setter => setter
                    .SetProperty(p => p.Quantity, p => p.Quantity + item.Quantity )
                    .SetProperty(o => o.UpdatedAt, (DateTime?)DateTime.UtcNow));

            if(quantityAffectedRows == 0)
            {
                await transaction.RollbackAsync();
                return Conflict();
            }

        }


        await transaction.CommitAsync();

        return NoContent();
    }

    [Authorize]
    [HttpGet]
    [Route("[action]")]
    public async Task<ActionResult<PagedResponse<OrderSummaryResponse>>> GetMyOrders([FromQuery] int page = 1, [FromQuery] int size = 10)
    {

        if (page < 1)
            return BadRequest("Page must be at least 1.");

        if (size < 1 || size > 50)
            return BadRequest("Page size must be between 1 and 50.");

        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(userId, out var parsedUserId))
            return Unauthorized();

        var query = dbContext.Orders
            .AsNoTracking()
            .Where(o => o.UserId == parsedUserId);

        var totalCount = await query.CountAsync();

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(o => new OrderSummaryResponse(
                o.Id,
                o.CreatedAt,
                o.TotalAmount,
                o.Status))
            .ToListAsync();

        var response = new PagedResponse<OrderSummaryResponse>(orders, page, size, totalCount);

        return Ok(response);
    }


    [Authorize]
    [HttpGet]
    [Route("[action]/{id:guid}")]
    public async Task<ActionResult<OrderDetailsResponse>> GetById(Guid id)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);

        if (!Guid.TryParse(userId, out var parsedUserId))
            return Unauthorized();

        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(o => o.Id == id && o.UserId == parsedUserId)
            .Select(o => new OrderDetailsResponse(
                o.Id,
                o.CreatedAt,
                o.TotalAmount,
                o.Status,
                o.Items.Select(item => new OrderItemDetailsResponse(
                        item.ProductId,
                        item.Quantity,
                        item.UnitPrice,
                        item.UnitPrice * item.Quantity
                    )).ToList()
                )).FirstOrDefaultAsync();

        if (order is null)
            return NotFound();

        return Ok(order);

    }
}
    