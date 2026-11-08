using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFlow.Common.Events;
using OrderFlow.DataAccess.Messaging;
using StackExchange.Redis;
using System.Text.Json;

namespace OrderFlow.Web.BackgroundServices;

/// <summary>
/// Subscribes to "orderflow:products:changed" and logs every change.
/// A real system would use these messages to refresh caches on other servers, update live dashboards, etc.
/// Pub/Sub only delivers to clients that are connected right now, so this service must be running to see changes.
/// </summary>
public sealed class ProductChangeSubscriber(
    IConnectionMultiplexer redis,
    ILogger<ProductChangeSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();

        // The handler runs on the Redis client's thread: it must never throw.
        await subscriber.SubscribeAsync(RedisProductChangePublisher.Channel, (_, message) => Handle(message));

        logger.LogInformation("Subscribed to {Channel}.", RedisProductChangePublisher.Channel);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected when the application shuts down.
        }
        finally
        {
            await subscriber.UnsubscribeAsync(RedisProductChangePublisher.Channel);
        }
    }

    private void Handle(RedisValue message)
    {
        try
        {
            var change = JsonSerializer.Deserialize<ProductChangedEvent>(message.ToString());

            if (change is null)
                return;

            logger.LogInformation(
                "Product {ProductId} {Change} (at {OccurredAtUtc:O}).",
                change.ProductId,
                change.Change,
                change.OccurredAtUtc);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Ignoring a malformed product change message.");
        }
    }
}
