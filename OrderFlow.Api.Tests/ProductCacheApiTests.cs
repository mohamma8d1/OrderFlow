using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using OrderFlow.Application.DTOs.Product;
using OrderFlow.Application.Interfaces;
using OrderFlow.Domain.Entities;
using OrderFlow.Infrastructure.Caching;
using OrderFlow.Infrastructure.Persistence;
using StackExchange.Redis;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace OrderFlow.Api.Tests;

/// <summary>
/// Runs the real API against a real Redis (Testcontainers) to check the
/// cache-aside flow end to end: MISS -> HIT -> invalidated on change.
/// </summary>
[Collection("Redis")]
public sealed class ProductCacheApiTests : IClassFixture<OrderFlowApiFactory>, IAsyncLifetime
{
    private readonly RedisContainerFixture _redis;
    private readonly WebApplicationFactory<Program> _factory;

    private readonly Guid _categoryId = Guid.NewGuid();
    private readonly Guid _productId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();

    public ProductCacheApiTests(OrderFlowApiFactory baseFactory, RedisContainerFixture redis)
    {
        _redis = redis;
        _factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(redis.Multiplexer);
                services.Replace(ServiceDescriptor.Singleton<IProductCache, RedisProductCache>());
            }));
    }

    private IDatabase Redis => _redis.Multiplexer.GetDatabase();

    public async Task InitializeAsync()
    {
        await Redis.ExecuteAsync("FLUSHDB");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.OrderItems.ExecuteDeleteAsync();
        await db.Orders.ExecuteDeleteAsync();
        await db.OutboxMessages.ExecuteDeleteAsync();
        await db.Products.ExecuteDeleteAsync();
        await db.Categories.ExecuteDeleteAsync();
        await db.Users.ExecuteDeleteAsync();

        db.Categories.Add(new Category { Id = _categoryId, Name = "Cat", CreatedAt = DateTime.UtcNow });
        db.Products.Add(new Product
        {
            Id = _productId,
            CategoryId = _categoryId,
            Name = "Mouse",
            Description = "Wireless",
            Price = 20m,
            Quantity = 10,
            CreatedAt = DateTime.UtcNow
        });
        db.Users.Add(new User
        {
            Id = _customerId,
            Email = "cache-customer@example.com",
            PhoneNumber = "+989120000000",
            FullName = "Cache Customer",
            PasswordHash = "test-only-password-hash",
            Role = "Customer",
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetById_FirstCallIsMiss_SecondCallIsHit()
    {
        using var client = ClientFor(_customerId, "Customer");

        var first = await client.GetAsync($"Product/GetById/{_productId}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("MISS", first.Headers.GetValues("X-Cache").Single());

        Assert.True(await Redis.KeyExistsAsync(RedisProductCache.KeyFor(_productId)));

        var second = await client.GetAsync($"Product/GetById/{_productId}");
        Assert.Equal("HIT", second.Headers.GetValues("X-Cache").Single());

        var product = await second.Content.ReadFromJsonAsync<ProductResponse>();
        Assert.Equal("Mouse", product!.Name);
    }

    [Fact]
    public async Task GetById_UnknownProduct_IsNotCached()
    {
        using var client = ClientFor(_customerId, "Customer");
        var unknownId = Guid.NewGuid();

        var response = await client.GetAsync($"Product/GetById/{unknownId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await Redis.KeyExistsAsync(RedisProductCache.KeyFor(unknownId)));
    }

    [Fact]
    public async Task Update_InvalidatesCache_SoNextReadSeesNewData()
    {
        using var customer = ClientFor(_customerId, "Customer");
        using var admin = ClientFor(Guid.NewGuid(), "Admin");

        await customer.GetAsync($"Product/GetById/{_productId}"); // warm the cache

        var update = await admin.PutAsJsonAsync($"Product/Update/{_productId}",
            new UpdateProductDto("Mouse Pro", "Wireless", 25m, 10, _categoryId));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        Assert.False(await Redis.KeyExistsAsync(RedisProductCache.KeyFor(_productId)));

        var after = await customer.GetAsync($"Product/GetById/{_productId}");
        Assert.Equal("MISS", after.Headers.GetValues("X-Cache").Single());
        Assert.Equal("Mouse Pro", (await after.Content.ReadFromJsonAsync<ProductResponse>())!.Name);
    }

    [Fact]
    public async Task CreatingAndCancellingOrder_InvalidatesCachedStock()
    {
        using var client = ClientFor(_customerId, "Customer");

        await client.GetAsync($"Product/GetById/{_productId}"); // cache Quantity = 10

        var create = await client.PostAsJsonAsync("Order/Create",
            new { items = new[] { new { productId = _productId, quantity = 3 } } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var afterOrder = await client.GetFromJsonAsync<ProductResponse>($"Product/GetById/{_productId}");
        Assert.Equal(7, afterOrder!.Quantity);

        var orderId = (await create.Content.ReadFromJsonAsync<CreatedOrder>())!.Id;
        var cancel = await client.PostAsync($"Order/Cancel/{orderId}", null);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        var afterCancel = await client.GetFromJsonAsync<ProductResponse>($"Product/GetById/{_productId}");
        Assert.Equal(10, afterCancel!.Quantity);
    }

    private sealed record CreatedOrder(Guid Id);

    private HttpClient ClientFor(Guid userId, string role)
    {
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(ClaimTypes.Role, role)
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }
}
