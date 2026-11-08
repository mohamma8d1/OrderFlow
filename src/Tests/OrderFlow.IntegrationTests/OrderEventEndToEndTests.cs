using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using OrderFlow.Common.Entities;
using OrderFlow.DataAccess.Messaging;
using OrderFlow.DataAccess.Core;
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
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OrderFlow.IntegrationTests;

[Collection("Redis")]
public sealed class OrderEventEndToEndTests : IClassFixture<OrderFlowApiFactory>, IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly OrderFlowApiFactory _factory;
    private readonly RedisContainerFixture _redis;

    public OrderEventEndToEndTests(OrderFlowApiFactory factory, RedisContainerFixture redis)
    {
        _factory = factory;
        _redis = redis;
    }

    public async Task InitializeAsync()
    {
        await Redis.ExecuteAsync("FLUSHDB");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.OrderItems.ExecuteDeleteAsync();
        await db.Orders.ExecuteDeleteAsync();
        await db.OutboxMessages.ExecuteDeleteAsync();
        await db.ProcessedEvents.ExecuteDeleteAsync();
        await db.Products.ExecuteDeleteAsync();
        await db.Categories.ExecuteDeleteAsync();
        await db.Users.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync() => await Redis.ExecuteAsync("FLUSHDB");

    [Fact]
    public async Task ScenarioA_CreateOrder_PublishesAndProcessesEventEndToEnd()
    {
        var (userId, productId, _) = await SeedCatalogAsync();
        var eventId = await CreateOrderViaApiAsync(userId, productId, 3);

        // Order and OutboxMessage were committed together by the API.
        var orderCount = await WithFactoryDbAsync(db => db.Orders.CountAsync());
        var outbox = await WithFactoryDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync());
        Assert.Equal(1, orderCount);
        Assert.Equal(eventId, outbox.Id);
        Assert.Equal("OrderCreated", outbox.EventType);
        Assert.Null(outbox.PublishedAtUtc);

        await Publisher().PublishPendingBatchAsync(Ct);

        var entry = Assert.Single(await Redis.StreamRangeAsync(OutboxPublisher.StreamKey, "-", "+"));
        Assert.Equal(eventId.ToString("D"), Field(entry, "eventId"));
        var published = await WithFactoryDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync());
        Assert.NotNull(published.PublishedAtUtc);

        var consumer = Consumer();
        await consumer.InitializeAsync(Ct);
        await consumer.ProcessOnceAsync(Ct);

        var processed = await WithFactoryDbAsync(db => db.ProcessedEvents.AsNoTracking().SingleAsync());
        Assert.Equal(eventId, processed.EventId);
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task ScenarioB_RedisUnavailable_OutboxRetainsThenPublishesAfterRecovery()
    {
        var (userId, productId, _) = await SeedCatalogAsync();
        var eventId = await CreateOrderViaApiAsync(userId, productId, 2);

        await using var broken = await BrokenRedisAsync();
        await Publisher(broken).PublishPendingBatchAsync(Ct);

        // Order and outbox remain committed; the outbox records the failure.
        Assert.Equal(1, await WithFactoryDbAsync(db => db.Orders.CountAsync()));
        var failed = await WithFactoryDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync());
        Assert.Null(failed.PublishedAtUtc);
        Assert.Equal(1, failed.RetryCount);
        Assert.False(string.IsNullOrWhiteSpace(failed.LastError));

        // Redis restored: the same outbox message is published and processed.
        await Publisher().PublishPendingBatchAsync(Ct);
        var consumer = Consumer();
        await consumer.InitializeAsync(Ct);
        await consumer.ProcessOnceAsync(Ct);

        var recovered = await WithFactoryDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync());
        Assert.NotNull(recovered.PublishedAtUtc);
        Assert.Null(recovered.LastError);
        var processed = await WithFactoryDbAsync(db => db.ProcessedEvents.AsNoTracking().SingleAsync());
        Assert.Equal(eventId, processed.EventId);
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task ScenarioC_DuplicateDelivery_OnlyOneProcessedEventAndBothAcknowledged()
    {
        var (userId, productId, _) = await SeedCatalogAsync();
        var eventId = await CreateOrderViaApiAsync(userId, productId, 1);

        await Publisher().PublishPendingBatchAsync(Ct);

        // Simulate a crash after XADD but before PublishedAtUtc was saved, then republish.
        await WithFactoryDbAsync(async db =>
        {
            var tracked = await db.OutboxMessages.SingleAsync();
            tracked.PublishedAtUtc = null;
            await db.SaveChangesAsync();
        });
        await Publisher().PublishPendingBatchAsync(Ct);
        Assert.Equal(2, (await Redis.StreamRangeAsync(OutboxPublisher.StreamKey, "-", "+")).Length);

        var consumer = Consumer();
        await consumer.InitializeAsync(Ct);
        await consumer.ProcessOnceAsync(Ct);

        Assert.Equal(1, await WithFactoryDbAsync(db => db.ProcessedEvents.CountAsync()));
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task ScenarioD_ConsumerFailureBeforeAck_RecoveredIdempotentlyByAnotherConsumer()
    {
        var (userId, productId, _) = await SeedCatalogAsync();
        var eventId = await CreateOrderViaApiAsync(userId, productId, 1);

        await Publisher().PublishPendingBatchAsync(Ct);

        var failing = Consumer("failing-consumer");
        await failing.InitializeAsync(Ct);

        // The failing consumer reads the message but crashes before acknowledging.
        await Redis.StreamReadGroupAsync(
            OutboxPublisher.StreamKey, OrderStreamConsumer.GroupName, "failing-consumer", ">", 10);
        Assert.Equal(1, await PendingCountAsync());
        Assert.Equal(0, await WithFactoryDbAsync(db => db.ProcessedEvents.CountAsync()));

        var recovering = Consumer("recovering-consumer");
        await recovering.ProcessOnceAsync(Ct);

        var processed = await WithFactoryDbAsync(db => db.ProcessedEvents.AsNoTracking().SingleAsync());
        Assert.Equal(eventId, processed.EventId);
        Assert.Equal(0, await PendingCountAsync());
    }

    private IDatabase Redis => _redis.Multiplexer.GetDatabase();

    private IServiceScopeFactory ScopeFactory =>
        _factory.Services.GetRequiredService<IServiceScopeFactory>();

    private OutboxPublisher Publisher(IConnectionMultiplexer? redis = null) =>
        new(ScopeFactory, redis ?? _redis.Multiplexer, NullLogger<OutboxPublisher>.Instance);

    private OrderStreamConsumer Consumer(string name = "e2e-consumer") =>
        new(ScopeFactory, _redis.Multiplexer, NullLogger<OrderStreamConsumer>.Instance, name, 5, 0);

    private async Task<(Guid UserId, Guid ProductId, Guid CategoryId)> SeedCatalogAsync()
    {
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await WithFactoryDbAsync(async db =>
        {
            db.Users.Add(new User
            {
                Id = userId,
                Email = "e2e@example.com",
                PhoneNumber = "+989121234567",
                FullName = "E2E Customer",
                PasswordHash = "test-only-hash",
                Role = "Customer",
                CreatedAt = DateTime.UtcNow
            });
            db.Categories.Add(new Category { Id = categoryId, Name = "E2E Category", CreatedAt = DateTime.UtcNow });
            db.Products.Add(new Product
            {
                Id = productId,
                CategoryId = categoryId,
                Name = "E2E Product",
                Description = "End-to-end test product",
                Price = 100m,
                Quantity = 10,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });

        return (userId, productId, categoryId);
    }

    private async Task<Guid> CreateOrderViaApiAsync(Guid userId, Guid productId, int quantity)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(userId));

        var response = await client.PostAsJsonAsync("Order/Create", new
        {
            items = new[] { new { productId, quantity } }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var outbox = await WithFactoryDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync());
        return outbox.Id;
    }

    private string CreateToken(Guid userId)
    {
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
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

    private async Task WithFactoryDbAsync(Func<ApplicationDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await action(db);
    }

    private async Task<T> WithFactoryDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await action(db);
    }

    private async Task<long> PendingCountAsync()
    {
        var info = await Redis.StreamPendingAsync(OutboxPublisher.StreamKey, OrderStreamConsumer.GroupName);
        return info.PendingMessageCount;
    }

    private static string Field(StreamEntry entry, string name) =>
        entry.Values.First(v => v.Name == name).Value.ToString();

    private static async Task<IConnectionMultiplexer> BrokenRedisAsync()
    {
        var options = ConfigurationOptions.Parse("localhost:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 500;
        options.SyncTimeout = 500;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }
}
