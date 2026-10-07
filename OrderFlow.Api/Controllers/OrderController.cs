using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Application.DTOs.Order;
using OrderFlow.Application.DTOs.OrderItem;
using OrderFlow.Domain.Entities;
using OrderFlow.Domain.Enums;
using OrderFlow.Infrastructure.Persistence;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace OrderFlow.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class OrderController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpPost]
    [Authorize]
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

        var createOrder = new Order
        {
            Id = Guid.NewGuid(),
            UserId = parsedUserId,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };


        decimal totalAmount = 0;

        var productId = dto.Items.Select(x => x.ProductId).ToList();

        var products  =  await dbContext.Products.Where(p => productId.Contains(p.Id)).ToListAsync();

        var productsById = products.ToDictionary(p => p.Id);

        foreach (var item in dto.Items) {

            if (item.Quantity <= 0)
                return BadRequest("Quantity must be greater than zero.");

            var product = productsById[item.ProductId];

            if (product == null)
                return NotFound("product not found!");

            if (product.Quantity < item.Quantity)
                return Conflict("Insufficient Stock");


            var orderItem = new OrderItem
            {
                Id= Guid.NewGuid(),
                OrderId= createOrder.Id,
                ProductId= product.Id,
                Quantity = item.Quantity,
                UnitPrice = product.Price
            };
       
            product.Quantity -= item.Quantity;

            totalAmount += product.Price * item.Quantity;

            dbContext.OrderItems.Add(orderItem);
        }

        createOrder.TotalAmount = totalAmount;
            
        dbContext.Orders.Add(createOrder);

        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch(DbUpdateConcurrencyException)
        {
            return Conflict("Product stock was changed by another request. Please try again.");
        }


        return Created($"/api/orders/{createOrder.Id}" ,new
        {
            createOrder.Id,
            createOrder.TotalAmount,
            createOrder.Status
        });
    }
}
