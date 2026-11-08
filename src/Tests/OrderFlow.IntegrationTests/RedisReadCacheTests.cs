using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.DataAccess.Caching;
using StackExchange.Redis;

namespace OrderFlow.IntegrationTests;

[Collection("Redis")]
public sealed class RedisReadCacheTests : IAsyncLifetime
{
    private sealed record Item(string Name, int Count);

    private readonly RedisContainerFixture _fixture;
    private readonly RedisReadCache _cache;

    public RedisReadCacheTests(RedisContainerFixture fixture)
    {
        _fixture = fixture;
        _cache = new RedisReadCache(fixture.Multiplexer, NullLogger<RedisReadCache>.Instance);
    }

    private IDatabase Redis => _fixture.Multiplexer.GetDatabase();

    public async Task InitializeAsync() => await Redis.ExecuteAsync("FLUSHDB");

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_WhenMissing_IsMiss()
    {
        var lookup = await _cache.GetAsync<Item>("orderflow:cache:test:missing");

        Assert.False(lookup.IsHit);
        Assert.Null(lookup.Value);
    }

    [Fact]
    public async Task Set_ThenGet_RoundTripsWithTtl()
    {
        const string key = "orderflow:cache:test:item";

        await _cache.SetAsync(key, new Item("a", 1), TimeSpan.FromSeconds(60));

        var lookup = await _cache.GetAsync<Item>(key);
        Assert.True(lookup.IsHit);
        Assert.Equal(new Item("a", 1), lookup.Value);

        var ttl = await Redis.KeyTimeToLiveAsync(key);
        Assert.InRange(ttl!.Value, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Version_StartsAtZero_AndBumpIncrements()
    {
        Assert.Equal(0, await _cache.GetVersionAsync("test"));

        await _cache.BumpVersionAsync("test");
        await _cache.BumpVersionAsync("test");

        Assert.Equal(2, await _cache.GetVersionAsync("test"));
        Assert.Equal("2", (await Redis.StringGetAsync("orderflow:cache:ver:test")).ToString());
    }

    [Fact]
    public async Task GetOrLoad_LoadsOnce_ThenHits_AndReloadsAfterBump()
    {
        var loads = 0;
        Task<Item> Load() => Task.FromResult(new Item("x", ++loads));

        var first = new CacheOutcome();
        Assert.Equal(1, (await _cache.GetOrLoadAsync(first, "test", "all", TimeSpan.FromMinutes(1), Load)).Count);
        Assert.False(first.IsHit);

        var second = new CacheOutcome();
        Assert.Equal(1, (await _cache.GetOrLoadAsync(second, "test", "all", TimeSpan.FromMinutes(1), Load)).Count);
        Assert.True(second.IsHit);
        Assert.Equal(1, loads);

        // Bumping the version makes the old entry unreachable: the next read loads again.
        await _cache.BumpVersionAsync("test");

        var third = new CacheOutcome();
        Assert.Equal(2, (await _cache.GetOrLoadAsync(third, "test", "all", TimeSpan.FromMinutes(1), Load)).Count);
        Assert.False(third.IsHit);

        Assert.True(await Redis.KeyExistsAsync("orderflow:cache:test:v0:all"));
        Assert.True(await Redis.KeyExistsAsync("orderflow:cache:test:v1:all"));
    }

    [Fact]
    public async Task GetOrLoadItem_DoesNotCacheNull()
    {
        var loads = 0;
        Task<Item?> Load() { loads++; return Task.FromResult<Item?>(null); }

        Assert.Null(await _cache.GetOrLoadItemAsync(new CacheOutcome(), "test", "id:1", TimeSpan.FromMinutes(1), Load));
        Assert.Null(await _cache.GetOrLoadItemAsync(new CacheOutcome(), "test", "id:1", TimeSpan.FromMinutes(1), Load));

        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task WhenRedisIsUnreachable_ReadsFallBackToLoad()
    {
        using var deadRedis = await DeadRedis.ConnectAsync();
        var cache = new RedisReadCache(deadRedis, NullLogger<RedisReadCache>.Instance);
        var outcome = new CacheOutcome();

        var value = await cache.GetOrLoadAsync(outcome, "test", "all", TimeSpan.FromMinutes(1), () => Task.FromResult(new Item("db", 1)));

        Assert.Equal(new Item("db", 1), value);
        Assert.False(outcome.IsHit);
        await cache.BumpVersionAsync("test"); // must not throw
    }
}
