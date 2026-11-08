using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using OrderFlow.Common.DTOs.Category;
using OrderFlow.Common.DTOs.Order;
using OrderFlow.Common.DTOs.Pagination;
using OrderFlow.Common.DTOs.Product;
using OrderFlow.Common.Entities;
using OrderFlow.Common.Enums;
using OrderFlow.DataAccess.Caching;
using OrderFlow.DataAccess.Core;
using StackExchange.Redis;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

namespace OrderFlow.IntegrationTests;

/// <summary>
/// Every GET endpoint against a real Redis: first call MISS, second HIT, and the right write invalidates it.
/// </summary>
[Collection("Redis")]
public sealed class ReadCacheApiTests : IClassFixture<OrderFlowApiFactory>, IAsyncLifetime
{
    private readonly RedisContainerFixture _redis;
    private readonly WebApplicationFactory<Program> _factory;

    private readonly Guid _categoryId = Guid.NewGuid();
    private readonly Guid _productId = Guid.NewGuid();
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();

    public ReadCacheApiTests(OrderFlowApiFactory baseFactory, RedisContainerFixture redis)
    {
        _redis = redis;
        _factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(redis.Multiplexer);
                services.Replace(ServiceDescriptor.Singleton<IProductCache, RedisProductCache>());
                services.Replace(ServiceDescriptor.Singleton<IReadCache, RedisReadCache>());
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
        db.Users.Add(NewUser(_userA, "a@example.com", "+989120000010"));
        db.Users.Add(NewUser(_userB, "b@example.com", "+989120000011"));

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ProductList_MissThenHit_AndOrderInvalidatesStock()
    {
        using var client = ClientFor(_userA, "Customer");

        Assert.Equal("MISS", XCache(await client.GetAsync("Product/Get")));

        var hit = await client.GetAsync("Product/Get");
        Assert.Equal("HIT", XCache(hit));
        Assert.Equal(10, (await hit.Content.ReadFromJsonAsync<List<ProductResponse>>())!.Single().Quantity);

        await CreateOrderAsync(client, quantity: 3);

        var after = await client.GetAsync("Product/Get");
        Assert.Equal("MISS", XCache(after));
        Assert.Equal(7, (await after.Content.ReadFromJsonAsync<List<ProductResponse>>())!.Single().Quantity);
    }

    [Fact]
    public async Task ProductList_InvalidatedByProductUpdate()
    {
        using var customer = ClientFor(_userA, "Customer");
        using var admin = ClientFor(Guid.NewGuid(), "Admin");

        await customer.GetAsync("Product/Get");
        Assert.Equal("HIT", XCache(await customer.GetAsync("Product/Get")));

        var update = await admin.PutAsJsonAsync($"Product/Update/{_productId}",
            new UpdateProductDto("Mouse Pro", "Wireless", 25m, 10, _categoryId));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var after = await customer.GetAsync("Product/Get");
        Assert.Equal("MISS", XCache(after));
        Assert.Equal("Mouse Pro", (await after.Content.ReadFromJsonAsync<List<ProductResponse>>())!.Single().Name);
    }

    [Fact]
    public async Task Categories_MissThenHit_AndCreateInvalidatesBoth()
    {
        using var customer = ClientFor(_userA, "Customer");
        using var admin = ClientFor(Guid.NewGuid(), "Admin");

        Assert.Equal("MISS", XCache(await customer.GetAsync("Category/GetAll")));
        Assert.Equal("HIT", XCache(await customer.GetAsync("Category/GetAll")));

        Assert.Equal("MISS", XCache(await customer.GetAsync($"Category/GetById/{_categoryId}")));
        Assert.Equal("HIT", XCache(await customer.GetAsync($"Category/GetById/{_categoryId}")));

        var create = await admin.PostAsJsonAsync("Category/Create", new CreateCategoryDto("New"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var all = await customer.GetAsync("Category/GetAll");
        Assert.Equal("MISS", XCache(all));
        Assert.Equal(2, (await all.Content.ReadFromJsonAsync<List<CategoryResponse>>())!.Count);

        Assert.Equal("MISS", XCache(await customer.GetAsync($"Category/GetById/{_categoryId}")));
    }

    [Fact]
    public async Task MyOrders_AndOrderDetail_Cached_CancelInvalidates()
    {
        using var client = ClientFor(_userA, "Customer");
        var orderId = await CreateOrderAsync(client, quantity: 1);

        Assert.Equal("MISS", XCache(await client.GetAsync("Order/GetMyOrders")));
        Assert.Equal("HIT", XCache(await client.GetAsync("Order/GetMyOrders")));

        Assert.Equal("MISS", XCache(await client.GetAsync($"Order/GetById/{orderId}")));
        var cached = await client.GetAsync($"Order/GetById/{orderId}");
        Assert.Equal("HIT", XCache(cached));
        Assert.Equal(OrderStatus.Pending, (await cached.Content.ReadFromJsonAsync<OrderDetailsResponse>())!.Status);

        var cancel = await client.PostAsync($"Order/Cancel/{orderId}", null);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        var detail = await client.GetAsync($"Order/GetById/{orderId}");
        Assert.Equal("MISS", XCache(detail));
        Assert.Equal(OrderStatus.Cancelled, (await detail.Content.ReadFromJsonAsync<OrderDetailsResponse>())!.Status);

        var list = await client.GetAsync("Order/GetMyOrders");
        Assert.Equal("MISS", XCache(list));
        var page = await list.Content.ReadFromJsonAsync<PagedResponse<OrderSummaryResponse>>();
        Assert.Equal(OrderStatus.Cancelled, page!.Items.Single().Status);
    }

    [Fact]
    public async Task OrderCache_IsPerUser()
    {
        using var userA = ClientFor(_userA, "Customer");
        using var userB = ClientFor(_userB, "Customer");
        var orderId = await CreateOrderAsync(userA, quantity: 1);

        await userA.GetAsync($"Order/GetById/{orderId}");
        await userA.GetAsync("Order/GetMyOrders");

        // User B must not be served user A's cached entries.
        Assert.Equal(HttpStatusCode.NotFound, (await userB.GetAsync($"Order/GetById/{orderId}")).StatusCode);

        var bList = await userB.GetAsync("Order/GetMyOrders");
        Assert.Equal("MISS", XCache(bList));
        Assert.Empty((await bList.Content.ReadFromJsonAsync<PagedResponse<OrderSummaryResponse>>())!.Items);
    }

    [Fact]
    public async Task Writes_HaveNoXCacheHeader()
    {
        using var client = ClientFor(_userA, "Customer");

        var create = await client.PostAsJsonAsync("Order/Create",
            new { items = new[] { new { productId = _productId, quantity = 1 } } });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.False(create.Headers.Contains("X-Cache"));
    }

    private async Task<Guid> CreateOrderAsync(HttpClient client, int quantity)
    {
        var response = await client.PostAsJsonAsync("Order/Create",
            new { items = new[] { new { productId = _productId, quantity } } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<OrderCreatedResponse>())!.Id;
    }

    private static string? XCache(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Cache", out var values) ? values.Single() : null;

    private static User NewUser(Guid id, string email, string phone) => new()
    {
        Id = id,
        Email = email,
        PhoneNumber = phone,
        FullName = "Cache Test",
        PasswordHash = "test-only-password-hash",
        Role = "Customer",
        CreatedAt = DateTime.UtcNow
    };

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
