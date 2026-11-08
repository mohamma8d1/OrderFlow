using Microsoft.EntityFrameworkCore;
using OrderFlow.Common.Events;
using OrderFlow.DataAccess.Messaging;
using StackExchange.Redis;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OrderFlow.IntegrationTests;

[Collection("Redis")]
public sealed class OrderStreamConsumerTests : RedisTestBase
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    public OrderStreamConsumerTests(RedisContainerFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Initialize_CreatesConsumerGroupAndStream()
    {
        await CreateConsumer().InitializeAsync(Ct);

        var groups = await Redis.StreamGroupInfoAsync(OrderStreamConsumer.StreamKey);
        Assert.Contains(groups, g => g.Name == OrderStreamConsumer.GroupName);
    }

    [Fact]
    public async Task Initialize_GroupAlreadyExists_HandlesBusyGroup()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);

        // Second initialization hits BUSYGROUP and must not throw.
        await consumer.InitializeAsync(Ct);

        var groups = await Redis.StreamGroupInfoAsync(OrderStreamConsumer.StreamKey);
        Assert.Single(groups, g => g.Name == OrderStreamConsumer.GroupName);
    }

    [Fact]
    public async Task ProcessOnce_ValidOrderCreated_RecordsProcessedEventAndAcknowledges()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);
        var eventId = Guid.NewGuid();
        var entryId = await AddValidEventAsync(eventId);

        await consumer.ProcessOnceAsync(Ct);

        var processed = await WithDbAsync(db => db.ProcessedEvents.AsNoTracking().SingleAsync());
        Assert.Equal(eventId, processed.EventId);
        Assert.Equal("OrderCreated", processed.EventType);
        Assert.Equal(entryId.ToString(), processed.StreamEntryId);
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_MissingRequiredFields_RejectsAndLeavesPending()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);
        await AddRawAsync(new NameValueEntry("eventType", "OrderCreated"));

        await consumer.ProcessOnceAsync(Ct);

        Assert.Equal(0, await ProcessedCountAsync());
        Assert.Equal(1, await PendingCountAsync());
        Assert.Empty(await DeadLetterAsync());
    }

    [Fact]
    public async Task ProcessOnce_InvalidEventIdGuid_RejectsAndLeavesPending()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);
        await AddRawAsync(
            new NameValueEntry("eventId", "not-a-guid"),
            new NameValueEntry("eventType", "OrderCreated"),
            new NameValueEntry("payload", ValidPayload(Guid.NewGuid())));

        await consumer.ProcessOnceAsync(Ct);

        Assert.Equal(0, await ProcessedCountAsync());
        Assert.Equal(1, await PendingCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_InvalidJson_RejectsAndLeavesPending()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);
        await AddRawAsync(
            new NameValueEntry("eventId", Guid.NewGuid().ToString("D")),
            new NameValueEntry("eventType", "OrderCreated"),
            new NameValueEntry("payload", "{ this is not json"));

        await consumer.ProcessOnceAsync(Ct);

        Assert.Equal(0, await ProcessedCountAsync());
        Assert.Equal(1, await PendingCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_NullPayload_RejectsAndLeavesPending()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);
        await AddRawAsync(
            new NameValueEntry("eventId", Guid.NewGuid().ToString("D")),
            new NameValueEntry("eventType", "OrderCreated"),
            new NameValueEntry("payload", "null"));

        await consumer.ProcessOnceAsync(Ct);

        Assert.Equal(0, await ProcessedCountAsync());
        Assert.Equal(1, await PendingCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_MismatchedEventId_RejectsAndLeavesPending()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);
        await AddRawAsync(
            new NameValueEntry("eventId", Guid.NewGuid().ToString("D")),
            new NameValueEntry("eventType", "OrderCreated"),
            new NameValueEntry("payload", ValidPayload(Guid.NewGuid())));

        await consumer.ProcessOnceAsync(Ct);

        Assert.Equal(0, await ProcessedCountAsync());
        Assert.Equal(1, await PendingCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_UnsupportedEventType_RejectsAndLeavesPending()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);
        await AddRawAsync(
            new NameValueEntry("eventId", Guid.NewGuid().ToString("D")),
            new NameValueEntry("eventType", "SomethingElse"),
            new NameValueEntry("payload", "{}"));

        await consumer.ProcessOnceAsync(Ct);

        Assert.Equal(0, await ProcessedCountAsync());
        Assert.Equal(1, await PendingCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_RepeatedDelivery_CreatesOnlyOneProcessedEvent()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);
        var eventId = Guid.NewGuid();
        await AddValidEventAsync(eventId);
        await AddValidEventAsync(eventId);

        await consumer.ProcessOnceAsync(Ct);

        Assert.Equal(1, await ProcessedCountAsync());
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_DuplicateAfterCommitBeforeAck_IsIdempotent()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);
        var eventId = Guid.NewGuid();
        await AddValidEventAsync(eventId);
        await consumer.ProcessOnceAsync(Ct);

        // Simulate redelivery of the same logical event after the DB commit.
        await AddValidEventAsync(eventId);
        await consumer.ProcessOnceAsync(Ct);

        Assert.Equal(1, await ProcessedCountAsync());
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_ConcurrentDuplicateConsumers_ExactlyOneProcessedEventAndNoDeadLetter()
    {
        var eventId = Guid.NewGuid();
        await AddValidEventAsync(eventId);
        await AddValidEventAsync(eventId);

        var first = CreateConsumer("consumer-1");
        var second = CreateConsumer("consumer-2");
        await first.InitializeAsync(Ct);

        await Task.WhenAll(first.ProcessOnceAsync(Ct), second.ProcessOnceAsync(Ct));

        Assert.Equal(1, await ProcessedCountAsync());
        Assert.Empty(await DeadLetterAsync());
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task Recover_AbandonedPendingMessage_ProcessesAndAcknowledges()
    {
        var recovering = CreateConsumer("recovering");
        await recovering.InitializeAsync(Ct);
        await AddValidEventAsync();

        // Another consumer reads the message but never acknowledges it.
        await Redis.StreamReadGroupAsync(
            OrderStreamConsumer.StreamKey, OrderStreamConsumer.GroupName, "abandoned", ">", 10);
        Assert.Equal(1, await PendingCountAsync());

        await recovering.ProcessOnceAsync(Ct);

        Assert.Equal(1, await ProcessedCountAsync());
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task Recover_RespectsConfiguredMinimumIdleTime()
    {
        var highIdle = CreateConsumer("high-idle", minIdleMs: 60000);
        await highIdle.InitializeAsync(Ct);
        await AddValidEventAsync();
        await Redis.StreamReadGroupAsync(
            OrderStreamConsumer.StreamKey, OrderStreamConsumer.GroupName, "abandoned", ">", 10);

        // Idle threshold not met: the just-delivered pending message is not claimed.
        await highIdle.ProcessOnceAsync(Ct);
        Assert.Equal(1, await PendingCountAsync());
        Assert.Equal(0, await ProcessedCountAsync());

        var zeroIdle = CreateConsumer("zero-idle", minIdleMs: 0);
        await zeroIdle.ProcessOnceAsync(Ct);
        Assert.Equal(1, await ProcessedCountAsync());
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task Recover_DeletedPendingEntry_IsHandledWithoutError()
    {
        var recovering = CreateConsumer("recovering");
        await recovering.InitializeAsync(Ct);
        var entryId = await AddValidEventAsync();
        await Redis.StreamReadGroupAsync(
            OrderStreamConsumer.StreamKey, OrderStreamConsumer.GroupName, "abandoned", ">", 10);

        await Redis.StreamDeleteAsync(OrderStreamConsumer.StreamKey, new[] { entryId });

        await recovering.ProcessOnceAsync(Ct);

        Assert.Equal(0, await ProcessedCountAsync());
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_BelowMaxAttempts_KeepsMessagePendingWithoutDeadLetter()
    {
        var consumer = CreateConsumer(maxAttempts: 5);
        await consumer.InitializeAsync(Ct);
        await AddInvalidEventAsync();

        await consumer.ProcessOnceAsync(Ct);

        Assert.Equal(1, await PendingCountAsync());
        Assert.Empty(await DeadLetterAsync());
    }

    [Fact]
    public async Task ProcessOnce_AtMaxAttempts_MovesToDeadLetterWithOriginalAndDiagnosticFields()
    {
        var consumer = CreateConsumer(maxAttempts: 1);
        await consumer.InitializeAsync(Ct);
        var entryId = await AddInvalidEventAsync();

        await consumer.ProcessOnceAsync(Ct);

        var deadLetter = Assert.Single(await DeadLetterAsync());
        Assert.Equal(entryId.ToString(), Field(deadLetter, "originalStreamEntryId"));
        Assert.Equal("1", Field(deadLetter, "deliveryCount"));
        Assert.False(string.IsNullOrWhiteSpace(Field(deadLetter, "failureReason")));
        Assert.Contains(deadLetter.Values, v => v.Name == "failedAtUtc");
        Assert.Equal("{ this is not json", Field(deadLetter, "payload"));
        Assert.Equal(0, await PendingCountAsync());
        Assert.Equal(0, await ProcessedCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_RepeatedFailures_IncrementDeliveryCountUntilDeadLetter()
    {
        var consumer = CreateConsumer(maxAttempts: 3);
        await consumer.InitializeAsync(Ct);
        await AddInvalidEventAsync();

        await consumer.ProcessOnceAsync(Ct);
        Assert.Equal(1, await PendingCountAsync());
        Assert.Empty(await DeadLetterAsync());

        await consumer.ProcessOnceAsync(Ct);
        await consumer.ProcessOnceAsync(Ct);

        var deadLetter = Assert.Single(await DeadLetterAsync());
        Assert.True(long.Parse(Field(deadLetter, "deliveryCount")) >= 3);
        Assert.Equal(0, await PendingCountAsync());
    }

    [Fact]
    public async Task ProcessOnce_CancelledToken_ThrowsAndDoesNotProcess()
    {
        var consumer = CreateConsumer();
        await consumer.InitializeAsync(Ct);
        await AddValidEventAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ProcessOnceAsync(cts.Token));

        Assert.Equal(0, await ProcessedCountAsync());
    }

    private OrderStreamConsumer CreateConsumer(
        string name = "consumer",
        int maxAttempts = 5,
        long minIdleMs = 0,
        int batchSize = 10) =>
        new(ScopeFactory, Fixture.Multiplexer, Logger<OrderStreamConsumer>(), name, maxAttempts, minIdleMs, batchSize);

    private static string ValidPayload(Guid eventId) =>
        JsonSerializer.Serialize(new OrderCreatedEvent(
            eventId, Guid.NewGuid(), Guid.NewGuid(), 100m, DateTime.UtcNow));

    private Task<RedisValue> AddValidEventAsync(Guid? eventId = null)
    {
        var id = eventId ?? Guid.NewGuid();
        return AddRawAsync(
            new NameValueEntry("eventId", id.ToString("D")),
            new NameValueEntry("eventType", "OrderCreated"),
            new NameValueEntry("payload", ValidPayload(id)),
            new NameValueEntry("occurredAtUtc", DateTime.UtcNow.ToString("O")));
    }

    private Task<RedisValue> AddInvalidEventAsync() =>
        AddRawAsync(
            new NameValueEntry("eventId", Guid.NewGuid().ToString("D")),
            new NameValueEntry("eventType", "OrderCreated"),
            new NameValueEntry("payload", "{ this is not json"));

    private Task<RedisValue> AddRawAsync(params NameValueEntry[] fields) =>
        Redis.StreamAddAsync(OrderStreamConsumer.StreamKey, fields);

    private async Task<long> PendingCountAsync()
    {
        var info = await Redis.StreamPendingAsync(OrderStreamConsumer.StreamKey, OrderStreamConsumer.GroupName);
        return info.PendingMessageCount;
    }

    private Task<int> ProcessedCountAsync() =>
        WithDbAsync(db => db.ProcessedEvents.CountAsync());

    private Task<StreamEntry[]> DeadLetterAsync() =>
        Redis.StreamRangeAsync(OrderStreamConsumer.DeadLetterStreamKey, "-", "+");

    private static string Field(StreamEntry entry, string name) =>
        entry.Values.First(v => v.Name == name).Value.ToString();
}
