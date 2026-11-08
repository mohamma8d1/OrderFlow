using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.DataAccess.Caching;
using StackExchange.Redis;

namespace OrderFlow.IntegrationTests;

[Collection("Redis")]
public sealed class RedisRateLimiterTests : IAsyncLifetime
{
    private readonly RedisContainerFixture _fixture;
    private readonly RedisRateLimiter _limiter;

    public RedisRateLimiterTests(RedisContainerFixture fixture)
    {
        _fixture = fixture;
        _limiter = new RedisRateLimiter(fixture.Multiplexer, NullLogger<RedisRateLimiter>.Instance);
    }

    private IDatabase Redis => _fixture.Multiplexer.GetDatabase();

    public async Task InitializeAsync() => await Redis.ExecuteAsync("FLUSHDB");

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AllowsUpToLimit_ThenBlocks_WithRetryAfter()
    {
        var window = TimeSpan.FromSeconds(60);

        for (var i = 1; i <= 3; i++)
        {
            var allowed = await _limiter.HitAsync("test", "user:a", limit: 3, window);
            Assert.True(allowed.IsAllowed);
            Assert.Equal(i, allowed.Count);
            Assert.Equal(3 - i, allowed.Remaining);
        }

        var blocked = await _limiter.HitAsync("test", "user:a", limit: 3, window);

        Assert.False(blocked.IsAllowed);
        Assert.Equal(4, blocked.Count);
        Assert.Equal(0, blocked.Remaining);
        Assert.InRange(blocked.RetryAfter, TimeSpan.FromSeconds(1), window);
    }

    [Fact]
    public async Task CounterKey_HasWindowTtl()
    {
        await _limiter.HitAsync("test", "user:a", limit: 3, TimeSpan.FromSeconds(60));

        var key = RedisRateLimiter.KeyFor("test", "user:a");
        Assert.Equal("orderflow:ratelimit:test:user:a", key.ToString());

        var ttl = await Redis.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task PartitionsAndPolicies_AreCountedSeparately()
    {
        var window = TimeSpan.FromSeconds(60);

        await _limiter.HitAsync("test", "user:a", 1, window);

        Assert.False((await _limiter.HitAsync("test", "user:a", 1, window)).IsAllowed);
        Assert.True((await _limiter.HitAsync("test", "user:b", 1, window)).IsAllowed);
        Assert.True((await _limiter.HitAsync("other", "user:a", 1, window)).IsAllowed);
    }

    [Fact]
    public async Task AfterWindowExpires_CountingStartsAgain()
    {
        var window = TimeSpan.FromSeconds(1);

        await _limiter.HitAsync("test", "user:a", 1, window);
        Assert.False((await _limiter.HitAsync("test", "user:a", 1, window)).IsAllowed);

        await Task.Delay(TimeSpan.FromMilliseconds(1300));

        var afterReset = await _limiter.HitAsync("test", "user:a", 1, window);
        Assert.True(afterReset.IsAllowed);
        Assert.Equal(1, afterReset.Count);
    }

    [Fact]
    public async Task CounterWithoutTtl_GetsItsTtlBack()
    {
        // Simulates the rare race where the key expired between SET NX and INCR.
        var key = RedisRateLimiter.KeyFor("test", "user:a");
        await Redis.StringSetAsync(key, 5);

        await _limiter.HitAsync("test", "user:a", 10, TimeSpan.FromSeconds(60));

        Assert.NotNull(await Redis.KeyTimeToLiveAsync(key));
    }

    [Fact]
    public async Task WhenRedisIsUnreachable_AllowsTheRequest()
    {
        using var deadRedis = await DeadRedis.ConnectAsync();
        var limiter = new RedisRateLimiter(deadRedis, NullLogger<RedisRateLimiter>.Instance);

        var decision = await limiter.HitAsync("test", "user:a", 1, TimeSpan.FromSeconds(60));

        Assert.True(decision.IsAllowed);
    }
}
