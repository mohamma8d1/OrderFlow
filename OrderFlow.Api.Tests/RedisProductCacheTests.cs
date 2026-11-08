using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.Application.DTOs.Product;
using OrderFlow.Infrastructure.Caching;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;
using Xunit;

namespace OrderFlow.Api.Tests;

[Collection("Redis")]
public sealed class RedisProductCacheTests : IAsyncLifetime
{
    private readonly RedisContainerFixture _fixture;
    private readonly RedisProductCache _cache;

    public RedisProductCacheTests(RedisContainerFixture fixture)
    {
        _fixture = fixture;
        _cache = new RedisProductCache(fixture.Multiplexer, NullLogger<RedisProductCache>.Instance);
    }

    private IDatabase Redis => _fixture.Multiplexer.GetDatabase();

    public async Task InitializeAsync() => await Redis.ExecuteAsync("FLUSHDB");

    public Task DisposeAsync() => Task.CompletedTask;

    private static ProductResponse NewProduct() =>
        new(Guid.NewGuid(), "Keyboard", "Mechanical", 49.90m, 7, Guid.NewGuid());

    [Fact]
    public async Task Get_WhenKeyMissing_ReturnsNull()
    {
        Assert.Null(await _cache.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Set_ThenGet_ReturnsSameProduct_AndStoresJsonWithTtl()
    {
        var product = NewProduct();

        await _cache.SetAsync(product);

        Assert.Equal(product, await _cache.GetAsync(product.Id));

        var key = RedisProductCache.KeyFor(product.Id);
        Assert.Equal($"orderflow:cache:product:{product.Id:D}", key.ToString());

        var ttl = await Redis.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value,
            RedisProductCache.BaseTtl - TimeSpan.FromSeconds(5),
            RedisProductCache.BaseTtl + RedisProductCache.MaxJitter);
    }

    [Fact]
    public async Task Remove_DeletesAllGivenKeys_AndLeavesOthers()
    {
        var a = NewProduct();
        var b = NewProduct();
        var untouched = NewProduct();

        await _cache.SetAsync(a);
        await _cache.SetAsync(b);
        await _cache.SetAsync(untouched);

        await _cache.RemoveAsync([a.Id, b.Id, a.Id]);

        Assert.Null(await _cache.GetAsync(a.Id));
        Assert.Null(await _cache.GetAsync(b.Id));
        Assert.NotNull(await _cache.GetAsync(untouched.Id));
    }

    [Fact]
    public async Task Remove_WithNoIds_DoesNothing()
    {
        await _cache.RemoveAsync([]);
    }

    [Fact]
    public async Task Get_WhenStoredValueIsCorrupt_ReturnsNull()
    {
        var id = Guid.NewGuid();
        await Redis.StringSetAsync(RedisProductCache.KeyFor(id), "{not json");

        Assert.Null(await _cache.GetAsync(id));
    }

    [Fact]
    public async Task WhenRedisIsUnreachable_CacheFailsOpen()
    {
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 200;
        options.AsyncTimeout = 500;
        options.SyncTimeout = 500;

        using var deadRedis = await ConnectionMultiplexer.ConnectAsync(options);
        var cache = new RedisProductCache(deadRedis, NullLogger<RedisProductCache>.Instance);
        var product = NewProduct();

        // None of these may throw: the API must keep working without Redis.
        await cache.SetAsync(product);
        Assert.Null(await cache.GetAsync(product.Id));
        await cache.RemoveAsync([product.Id]);
    }
}
