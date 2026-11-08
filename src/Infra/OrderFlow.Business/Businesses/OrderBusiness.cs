using Microsoft.EntityFrameworkCore;
using OrderFlow.Business.Contracts;
using OrderFlow.Business.Exceptions;
using OrderFlow.Common.DTOs.Order;
using OrderFlow.Common.DTOs.OrderItem;
using OrderFlow.Common.DTOs.Pagination;
using OrderFlow.Common.Entities;
using OrderFlow.Common.Enums;
using OrderFlow.Common.Events;
using OrderFlow.DataAccess.Caching;
using OrderFlow.DataAccess.Core;
using System.Text.Json;

namespace OrderFlow.Business.Businesses;

public class OrderBusiness(ApplicationDbContext dbContext, IProductCache productCache) : IOrderBusiness
{
    public async Task<OrderCreatedResponse> CreateAsync(Guid userId, CreateOrderDto dto, CancellationToken cancellationToken = default)
    {
        if (dto.Items is null || dto.Items.Count == 0)
            throw BusinessException.BadRequest("Order must contain at least one item.");

        var hasDuplicatedProduct = dto.Items
            .GroupBy(x => x.ProductId)
            .Any(x => x.Count() > 1);

        if (hasDuplicatedProduct)
            throw BusinessException.Conflict("duplicate item requested!");

        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = OrderStatus.Pending,
        };

        var productIds = dto.Items.Select(x => x.ProductId).ToList();

        var products = await dbContext.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        if (products.Count != productIds.Count)
            throw BusinessException.NotFound("One or more products were not found.");

        var productsById = products.ToDictionary(p => p.Id);

        if (dto.Items.Any(item => item.Quantity <= 0))
            throw BusinessException.BadRequest("Quantity must be greater than zero.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        dbContext.Orders.Add(order);

        decimal totalAmount = 0;

        foreach (var item in dto.Items)
        {
            var product = productsById[item.ProductId];

            // Atomic "decrease only if enough stock" in one UPDATE.
            var affectedRows = await dbContext.Products
                .Where(p => p.Id == item.ProductId && p.Quantity >= item.Quantity)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.Quantity, p => p.Quantity - item.Quantity)
                    .SetProperty(p => p.UpdatedAt, (DateTime?)DateTime.UtcNow),
                    cancellationToken);

            if (affectedRows == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw BusinessException.Conflict("Insufficient Stock");
            }

            dbContext.OrderItems.Add(new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                ProductId = product.Id,
                Quantity = item.Quantity,
                UnitPrice = product.Price
            });

            totalAmount += product.Price * item.Quantity;
        }

        order.TotalAmount = totalAmount;

        // Outbox: the event is saved in the same transaction as the order;
        // OutboxPublisher later copies it to the Redis stream.
        var eventId = Guid.NewGuid();
        var occurredAtUtc = DateTime.UtcNow;

        var orderCreatedEvent = new OrderCreatedEvent(
            EventId: eventId,
            OrderId: order.Id,
            UserId: userId,
            TotalAmount: order.TotalAmount,
            OccurredAtUtc: occurredAtUtc);

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = eventId,
            EventType = "OrderCreated",
            Payload = JsonSerializer.Serialize(orderCreatedEvent),
            OccurredAtUtc = occurredAtUtc
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Stock (Quantity) of these products just went down. Invalidate AFTER the
        // commit, otherwise another request could re-cache the old value in between.
        await productCache.RemoveAsync(productIds, cancellationToken);

        return new OrderCreatedResponse(order.Id, order.TotalAmount, order.Status);
    }

    public async Task CancelAsync(Guid userId, Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, cancellationToken)
            ?? throw BusinessException.NotFound("Order Not Found!");

        if (order.Status != OrderStatus.Pending)
            throw BusinessException.Conflict("Order Processed and cant cancel it now!");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var affectedRows = await dbContext.Orders
            .Where(o => o.Id == orderId && o.UserId == userId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, OrderStatus.Cancelled)
                .SetProperty(o => o.UpdatedAt, (DateTime?)DateTime.UtcNow),
                cancellationToken);

        if (affectedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw BusinessException.Conflict();
        }

        var orderItems = await dbContext.OrderItems
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToListAsync(cancellationToken);

        foreach (var item in orderItems)
        {
            var quantityAffectedRows = await dbContext.Products
                .Where(p => p.Id == item.ProductId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.Quantity, p => p.Quantity + item.Quantity)
                    .SetProperty(p => p.UpdatedAt, (DateTime?)DateTime.UtcNow),
                    cancellationToken);

            if (quantityAffectedRows == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw BusinessException.Conflict();
            }
        }

        await transaction.CommitAsync(cancellationToken);

        // Stock was returned to these products, so their cached copies are stale.
        await productCache.RemoveAsync(orderItems.Select(item => item.ProductId), cancellationToken);
    }

    public async Task<PagedResponse<OrderSummaryResponse>> GetMyOrdersAsync(Guid userId, int page, int size, CancellationToken cancellationToken = default)
    {
        if (page < 1)
            throw BusinessException.BadRequest("Page must be at least 1.");

        if (size < 1 || size > 50)
            throw BusinessException.BadRequest("Page size must be between 1 and 50.");

        var query = dbContext.Orders
            .AsNoTracking()
            .Where(o => o.UserId == userId);

        var totalCount = await query.CountAsync(cancellationToken);

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(o => new OrderSummaryResponse(o.Id, o.CreatedAt, o.TotalAmount, o.Status))
            .ToListAsync(cancellationToken);

        return new PagedResponse<OrderSummaryResponse>(orders, page, size, totalCount);
    }

    public async Task<OrderDetailsResponse> GetByIdAsync(Guid userId, Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(o => o.Id == orderId && o.UserId == userId)
            .Select(o => new OrderDetailsResponse(
                o.Id,
                o.CreatedAt,
                o.TotalAmount,
                o.Status,
                o.Items.Select(item => new OrderItemDetailsResponse(
                    item.ProductId,
                    item.Quantity,
                    item.UnitPrice,
                    item.UnitPrice * item.Quantity)).ToList()))
            .FirstOrDefaultAsync(cancellationToken);

        return order ?? throw BusinessException.NotFound();
    }
}
