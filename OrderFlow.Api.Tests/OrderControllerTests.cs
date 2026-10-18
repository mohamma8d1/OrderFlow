using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OrderFlow.Domain.Entities;
using OrderFlow.Domain.Enums;
using OrderFlow.Infrastructure.Persistence;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

namespace OrderFlow.Api.Tests;

public class OrderControllerTests : IClassFixture<OrderFlowApiFactory>
{
    private readonly OrderFlowApiFactory _factory;

    public OrderControllerTests(OrderFlowApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateOrder_ValidRequest_CreatesOrderAndDecreasesStock()
    {
        await ResetDatabaseAsync();

        var userId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var category = new Category
            {
                Id = categoryId,
                Name = "Test Category",
                CreatedAt = DateTime.UtcNow
            };

            var product = new Product
            {
                Id = productId,
                CategoryId = categoryId,
                Name = "Test Product",
                Description = "Product for integration test",
                Price = 100m,
                Quantity = 10,
                CreatedAt = DateTime.UtcNow
            };

            var user = new User
            {
                Id = userId,
                Email = "customer@example.com",
                PhoneNumber = "+989121234567",
                FullName = "Test Customer",
                PasswordHash = "test-only-password-hash",
                Role = "Customer",
                CreatedAt = DateTime.UtcNow
            };

            db.Categories.Add(category);
            db.Products.Add(product);
            db.Users.Add(user);

            await db.SaveChangesAsync();
        }
        using var client = _factory.CreateClient();

        var token = CreateToken(userId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = new
        {
            items = new[]
            {
                new
                {
                    productId,
                    quantity = 3
                }
            }
        };

        var response = await client.PostAsJsonAsync("/api/Order", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();

        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var savedProduct = await verifyDb.Products
            .AsNoTracking()
            .SingleAsync(p => p.Id == productId);

        Assert.Equal(7, savedProduct.Quantity);

        var savedOrder = await verifyDb.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .SingleAsync(o => o.UserId == userId);

        Assert.Equal(300m, savedOrder.TotalAmount);
        Assert.Equal(OrderStatus.Pending, savedOrder.Status);

        var savedOrderItem = Assert.Single(savedOrder.Items);

        Assert.Equal(productId, savedOrderItem.ProductId);
        Assert.Equal(3, savedOrderItem.Quantity);
        Assert.Equal(100m, savedOrderItem.UnitPrice);
    }

    [Fact]
    public async Task CreateOrder_InsufficientStock_RollsBackAllChanges()
    {
        await ResetDatabaseAsync();

        var userId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var productAId = Guid.NewGuid();
        var productBId = Guid.NewGuid();


        using( var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var user = new User
            {
                Id = userId,
                Email = "rollback-test@example.com",
                PhoneNumber = "+989121234567",
                FullName = "Rollback Test",
                PasswordHash = "test-only-hash",
                Role = "Customer",
                CreatedAt = DateTime.UtcNow
            };

            var category = new Category
            {
                Id = categoryId,
                Name = "Test Category",
                CreatedAt = DateTime.UtcNow
            };

            var productA = new Product
            {
                Id = productAId,
                CategoryId = categoryId,
                Name = "Product A",
                Description = "Enough stock",
                Price = 100m,
                Quantity = 10,
                CreatedAt = DateTime.UtcNow
            };

            var productB = new Product
            {
                Id = productBId,
                CategoryId = categoryId,
                Name = "Product B",
                Description = "Insufficient stock",
                Price = 50m,
                Quantity = 1,
                CreatedAt = DateTime.UtcNow
            };

            db.Users.Add(user);
            db.Categories.Add(category);
            db.Products.AddRange(productA, productB);

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();

        var token = CreateToken(userId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = new
        {
            items = new[]
            {
                new { productId = productAId, quantity = 3 },
                new { productId = productBId, quantity = 2 }
            }
        };

        var response = await client.PostAsJsonAsync("api/Order", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();

        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var savedProductA = await verifyDb.Products
            .AsNoTracking()
            .SingleAsync(p => p.Id == productAId);

        var savedProductB = await verifyDb.Products
            .AsNoTracking()
            .SingleAsync(p => p.Id == productBId);

        Assert.Equal(10, savedProductA.Quantity);
        Assert.Equal(1, savedProductB.Quantity);

        Assert.Equal(0, await verifyDb.Orders.CountAsync());
        Assert.Equal(0, await verifyDb.OrderItems.CountAsync());
    }


    [Fact]
    public async Task CancelOrder_RepeatedRequest_RestoresStockOnlyOnce()
    {
        await ResetDatabaseAsync();

        var userId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var user = new User
            {
                Id = userId,
                Email = "cancel-test@example.com",
                PhoneNumber = "+989121234567",
                FullName = "Cancel Test",
                PasswordHash = "test-only-hash",
                Role = "Customer",
                CreatedAt = DateTime.UtcNow
            };

            var category = new Category
            {
                Id = categoryId,
                Name = "Test Category",
                CreatedAt = DateTime.UtcNow
            };

            var product = new Product
            {
                Id = productId,
                CategoryId = categoryId,
                Name = "Test Product",
                Description = "Product for cancellation test",
                Price = 100m,
                Quantity = 7,
                CreatedAt = DateTime.UtcNow
            };

            var order = new Order
            {
                Id = orderId,
                UserId = userId,
                TotalAmount = 300m,
                Status = OrderStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                ProductId = productId,
                Quantity = 3,
                UnitPrice = 100m,
                CreatedAt = DateTime.UtcNow
            };

            db.Users.Add(user);
            db.Categories.Add(category);
            db.Products.Add(product);
            db.Orders.Add(order);
            db.OrderItems.Add(orderItem);

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();

        var token = CreateToken(userId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var firstResponse = await client.PostAsync($"/api/Order/{orderId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

        var secondResponse = await client.PostAsync($"/api/Order/{orderId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();

        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var savedProduct = await verifyDb.Products
            .AsNoTracking()
            .SingleAsync(p => p.Id == productId);

        var savedOrder = await verifyDb.Orders
            .AsNoTracking()
            .SingleAsync(o => o.Id == orderId);

        Assert.Equal(10, savedProduct.Quantity);
        Assert.Equal(OrderStatus.Cancelled, savedOrder.Status);
    }


    [Fact]
    public async Task CancelOrder_OtherUsersOrder_Returns404AndDoesNotChangeData()
    {
        await ResetDatabaseAsync();

        var userAId = Guid.NewGuid();
        var userBId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var userA = new User
            {
                Id = userAId,
                Email = "user-a@example.com",
                PhoneNumber = "+989121111111",
                FullName = "User A",
                PasswordHash = "test-only-hash",
                Role = "Customer",
                CreatedAt = DateTime.UtcNow
            };

            var userB = new User
            {
                Id = userBId,
                Email = "user-b@example.com",
                PhoneNumber = "+989122222222",
                FullName = "User B",
                PasswordHash = "test-only-hash",
                Role = "Customer",
                CreatedAt = DateTime.UtcNow
            };

            var category = new Category
            {
                Id = categoryId,
                Name = "Test Category",
                CreatedAt = DateTime.UtcNow
            };

            var product = new Product
            {
                Id = productId,
                CategoryId = categoryId,
                Name = "Test Product",
                Description = "Ownership test",
                Price = 100m,
                Quantity = 7,
                CreatedAt = DateTime.UtcNow
            };

            var order = new Order
            {
                Id = orderId,
                UserId = userBId,
                TotalAmount = 300m,
                Status = OrderStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                ProductId = productId,
                Quantity = 3,
                UnitPrice = 100m,
                CreatedAt = DateTime.UtcNow
            };

            db.Users.AddRange(userA, userB);
            db.Categories.Add(category);
            db.Products.Add(product);
            db.Orders.Add(order);
            db.OrderItems.Add(orderItem);

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();

        var token = CreateToken(userAId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync($"/api/Order/{orderId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();

        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var savedOrder = await verifyDb.Orders
            .AsNoTracking()
            .SingleAsync(o => o.Id == orderId);

        var savedProduct = await verifyDb.Products
            .AsNoTracking()
            .SingleAsync(p => p.Id == productId);

        Assert.Equal(OrderStatus.Pending, savedOrder.Status);
        Assert.Equal(7, savedProduct.Quantity);
    }




    private async Task ResetDatabaseAsync()
    {
        using var scope = _factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    private string CreateToken(Guid userId)
    {
        var config = _factory.Services.GetRequiredService<IConfiguration>();

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email,"customer@example.com"),
            new Claim(ClaimTypes.Role, "Customer")
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
