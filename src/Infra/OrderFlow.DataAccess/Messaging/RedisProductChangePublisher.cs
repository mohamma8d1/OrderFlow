using Microsoft.Extensions.Logging;
using OrderFlow.Common.Events;
using StackExchange.Redis;
using System.Text.Json;

namespace OrderFlow.DataAccess.Messaging;

/// <summary>
/// Publishes to the channel "orderflow:products:changed" with PUBLISH.
/// PUBLISH returns how many subscribers received the message (0 when nobody is listening).
/// </summary>
public sealed class RedisProductChangePublisher(
    IConnectionMultiplexer redis,
    ILogger<RedisProductChangePublisher> logger) : IProductChangePublisher
{
    public static readonly RedisChannel Channel = RedisChannel.Literal("orderflow:products:changed");

    public async Task PublishAsync(Guid productId, string change, CancellationToken cancellationToken = default)
    {
        var message = JsonSerializer.Serialize(new ProductChangedEvent(productId, change, DateTime.UtcNow));

        try
        {
            var receivers = await redis.GetSubscriber().PublishAsync(Channel, message);

            logger.LogDebug("Published {Change} for product {ProductId} to {Receivers} subscriber(s).", change, productId, receivers);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Fail open: the change is already saved, so only the notification is lost.
            logger.LogWarning(ex, "Could not publish {Change} for product {ProductId}.", change, productId);
        }
    }
}
