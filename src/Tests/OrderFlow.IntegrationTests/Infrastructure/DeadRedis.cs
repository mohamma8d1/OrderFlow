using StackExchange.Redis;

namespace OrderFlow.IntegrationTests;

/// <summary>A multiplexer pointed at a port where nothing listens, to test "Redis is down".</summary>
internal static class DeadRedis
{
    public static async Task<ConnectionMultiplexer> ConnectAsync()
    {
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 200;
        options.AsyncTimeout = 500;
        options.SyncTimeout = 500;

        return await ConnectionMultiplexer.ConnectAsync(options);
    }
}
