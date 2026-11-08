using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.Common.DTOs.Product;
using OrderFlow.DataAccess.Caching;
using StackExchange.Redis;

namespace OrderFlow.IntegrationTests;

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
    public async Task Get_WhenKeyMissing_IsMiss()
    {
        var lookup = await _cache.GetAsync(Guid.NewGuid());

        Assert.False(lookup.IsHit);
        Assert.Null(lookup.Product);
    }

    [Fact]
    public async Task Set_ThenGet_ReturnsSameProduct_AndStoresJsonWithTtl()
    {
        var product = NewProduct();

        await _cache.SetAsync(product);

        var lookup = await _cache.GetAsync(product.Id);
        Assert.True(lookup.IsHit);
        Assert.Equal(product, lookup.Product);

        var key = RedisProductCache.KeyFor(product.Id);
        Assert.Equal($"orderflow:cache:product:{product.Id:D}", key.ToString());

        var ttl = await Redis.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value,
            RedisProductCache.BaseTtl - TimeSpan.FromSeconds(5),
            RedisProductCache.BaseTtl + RedisProductCache.MaxJitter);
    }

    [Fact]
    public async Task SetNotFound_ThenGet_IsHitWithoutProduct_AndShortTtl()
    {
        var id = Guid.NewGuid();

        await _cache.SetNotFoundAsync(id);

        var lookup = await _cache.GetAsync(id);
        Assert.True(lookup.IsHit);
        Assert.Null(lookup.Product);

        var key = RedisProductCache.KeyFor(id);
        Assert.Equal(RedisProductCache.NotFoundMarker, (await Redis.StringGetAsync(key)).ToString());

        var ttl = await Redis.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(1), RedisProductCache.NotFoundTtl);
    }

    [Fact]
    public async Task Set_OverwritesNotFoundMarker()
    {
        var product = NewProduct();

        await _cache.SetNotFoundAsync(product.Id);
        await _cache.SetAsync(product);

        Assert.Equal(product, (await _cache.GetAsync(product.Id)).Product);
    }

    [Fact]
    public async Task Remove_DeletesAllGivenKeys_AndLeavesOthers()
    {
        var a = NewProduct();
        var b = NewProduct();
        var untouched = NewProduct();

        await _cache.SetAsync(a);
        await _cache.SetNotFoundAsync(b.Id);
        await _cache.SetAsync(untouched);

        await _cache.RemoveAsync([a.Id, b.Id, a.Id]);

        Assert.False((await _cache.GetAsync(a.Id)).IsHit);
        Assert.False((await _cache.GetAsync(b.Id)).IsHit);
        Assert.True((await _cache.GetAsync(untouched.Id)).IsHit);
    }

    [Fact]
    public async Task Remove_WithNoIds_DoesNothing()
    {
        await _cache.RemoveAsync([]);
    }

    [Fact]
    public async Task Get_WhenStoredValueIsCorrupt_IsMiss()
    {
        var id = Guid.NewGuid();
        await Redis.StringSetAsync(RedisProductCache.KeyFor(id), "{not json");

        Assert.False((await _cache.GetAsync(id)).IsHit);
    }

    [Fact]
    public async Task WhenRedisIsUnreachable_CacheFailsOpen()
    {
        using var deadRedis = await DeadRedis.ConnectAsync();
        var cache = new RedisProductCache(deadRedis, NullLogger<RedisProductCache>.Instance);
        var product = NewProduct();

        // None of these may throw: the API must keep working without Redis.
        await cache.SetAsync(product);
        await cache.SetNotFoundAsync(product.Id);
        Assert.False((await cache.GetAsync(product.Id)).IsHit);
        await cache.RemoveAsync([product.Id]);
    }
}
