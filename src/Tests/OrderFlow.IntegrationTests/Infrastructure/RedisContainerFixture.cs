using StackExchange.Redis;
using System.Threading.Tasks;
using Testcontainers.Redis;
using Xunit;

namespace OrderFlow.IntegrationTests;

public sealed class RedisContainerFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.0").Build();

    public IConnectionMultiplexer Multiplexer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = ConfigurationOptions.Parse(_container.GetConnectionString());
        options.AbortOnConnectFail = true;
        options.AllowAdmin = true;
        options.ClientName = "OrderFlow.Tests";

        Multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task DisposeAsync()
    {
        Multiplexer?.Dispose();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("Redis")]
public sealed class RedisCollection : ICollectionFixture<RedisContainerFixture>
{
}
