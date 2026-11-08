using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.Common.Events;
using OrderFlow.DataAccess.Messaging;
using StackExchange.Redis;
using System.Text.Json;

namespace OrderFlow.IntegrationTests;

[Collection("Redis")]
public sealed class RedisPubSubTests
{
    private readonly RedisContainerFixture _fixture;
    private readonly RedisProductChangePublisher _publisher;

    public RedisPubSubTests(RedisContainerFixture fixture)
    {
        _fixture = fixture;
        _publisher = new RedisProductChangePublisher(fixture.Multiplexer, NullLogger<RedisProductChangePublisher>.Instance);
    }

    [Fact]
    public async Task PublishedChange_IsDeliveredToSubscriber()
    {
        var subscriber = _fixture.Multiplexer.GetSubscriber();
        var received = new TaskCompletionSource<ProductChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await subscriber.SubscribeAsync(RedisProductChangePublisher.Channel, (_, message) =>
            received.TrySetResult(JsonSerializer.Deserialize<ProductChangedEvent>(message.ToString())!));

        try
        {
            var productId = Guid.NewGuid();

            await _publisher.PublishAsync(productId, ProductChangeKinds.StockDecreased);

            var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(productId, change.ProductId);
            Assert.Equal(ProductChangeKinds.StockDecreased, change.Change);
        }
        finally
        {
            await subscriber.UnsubscribeAsync(RedisProductChangePublisher.Channel);
        }
    }

    [Fact]
    public async Task PublishWithNoSubscribers_ReturnsReceiverCountZero()
    {
        // Nobody is listening: Redis accepts the message and reports 0 receivers. Nothing is stored.
        var receivers = await _fixture.Multiplexer.GetSubscriber()
            .PublishAsync(RedisProductChangePublisher.Channel, "{}");

        Assert.Equal(0, receivers);
    }

    [Fact]
    public async Task WhenRedisIsUnreachable_PublishFailsOpen()
    {
        using var deadRedis = await DeadRedis.ConnectAsync();
        var publisher = new RedisProductChangePublisher(deadRedis, NullLogger<RedisProductChangePublisher>.Instance);

        // Must not throw: the product change is already saved; only the notification is lost.
        await publisher.PublishAsync(Guid.NewGuid(), ProductChangeKinds.Updated);
    }
}
