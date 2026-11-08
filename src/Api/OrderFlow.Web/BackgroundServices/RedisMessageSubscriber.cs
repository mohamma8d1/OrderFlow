using StackExchange.Redis;

namespace OrderFlow.Web.BackgroundServices;

public class RedisMessageSubscriber(
        IConnectionMultiplexer redis,
        ILogger<RedisMessageSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();

        var channel = RedisChannel.Literal("orderflow:events");

        await subscriber.SubscribeAsync(
                channel,
                (redisChannel, message) =>
                {
                    logger.LogInformation("Received message from {Channel}: {Message}", redisChannel.ToString(), message.ToString());
                });

        logger.LogInformation("Redis subscriber started.");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Expected when the application shuts down.
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel);
        }
    }
}
