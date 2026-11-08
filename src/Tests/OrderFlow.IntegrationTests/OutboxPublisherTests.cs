using Microsoft.EntityFrameworkCore;
using OrderFlow.Common.Events;
using OrderFlow.DataAccess.Messaging;
using OrderFlow.Common.Entities;
using StackExchange.Redis;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OrderFlow.IntegrationTests;

[Collection("Redis")]
public sealed class OutboxPublisherTests : RedisTestBase
{
    public OutboxPublisherTests(RedisContainerFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task PublishPendingBatch_UnpublishedMessage_AddsEntryWithExpectedFields()
    {
        var occurredAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var message = NewOutboxMessage(occurredAt: occurredAt);
        await SeedAsync(message);

        var stored = await WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == message.Id));

        await CreatePublisher().PublishPendingBatchAsync(CancellationToken.None);

        var entries = await ReadEventsAsync();
        var entry = Assert.Single(entries);
        Assert.Equal(message.Id.ToString("D"), Field(entry, "eventId"));
        Assert.Equal("OrderCreated", Field(entry, "eventType"));
        Assert.Equal(message.Payload, Field(entry, "payload"));
        Assert.Equal(stored.OccurredAtUtc.ToString("O"), Field(entry, "occurredAtUtc"));
    }

    [Fact]
    public async Task PublishPendingBatch_Success_SetsPublishedAtUtcAndClearsLastError()
    {
        var message = NewOutboxMessage();
        message.LastError = "previous failure";
        await SeedAsync(message);

        await CreatePublisher().PublishPendingBatchAsync(CancellationToken.None);

        var saved = await WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == message.Id));
        Assert.NotNull(saved.PublishedAtUtc);
        Assert.Null(saved.LastError);
        Assert.Equal(0, saved.RetryCount);
    }

    [Fact]
    public async Task PublishPendingBatch_RedisUnavailable_LeavesUnpublishedAndIncrementsRetry()
    {
        var message = NewOutboxMessage();
        await SeedAsync(message);

        await using var broken = await BrokenRedisAsync();
        await CreatePublisher(redis: broken).PublishPendingBatchAsync(CancellationToken.None);

        var saved = await WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == message.Id));
        Assert.Null(saved.PublishedAtUtc);
        Assert.Equal(1, saved.RetryCount);
        Assert.False(string.IsNullOrWhiteSpace(saved.LastError));
        Assert.True(saved.LastError!.Length <= 2000);
        Assert.Empty(await ReadEventsAsync());
    }

    [Fact]
    public async Task PublishPendingBatch_AlreadyPublished_DoesNotPublishAgain()
    {
        var published = NewOutboxMessage();
        published.PublishedAtUtc = DateTime.UtcNow;
        var pending = NewOutboxMessage();
        await SeedAsync(published, pending);

        await CreatePublisher().PublishPendingBatchAsync(CancellationToken.None);

        var entry = Assert.Single(await ReadEventsAsync());
        Assert.Equal(pending.Id.ToString("D"), Field(entry, "eventId"));
    }

    [Fact]
    public async Task PublishPendingBatch_MultipleMessages_PublishesInOccurredAtOrder()
    {
        var first = NewOutboxMessage(occurredAt: DateTime.UtcNow.AddMinutes(-3));
        var second = NewOutboxMessage(occurredAt: DateTime.UtcNow.AddMinutes(-2));
        var third = NewOutboxMessage(occurredAt: DateTime.UtcNow.AddMinutes(-1));
        await SeedAsync(third, first, second);

        await CreatePublisher().PublishPendingBatchAsync(CancellationToken.None);

        var entries = await ReadEventsAsync();
        Assert.Equal(3, entries.Length);
        Assert.Equal(first.Id.ToString("D"), Field(entries[0], "eventId"));
        Assert.Equal(second.Id.ToString("D"), Field(entries[1], "eventId"));
        Assert.Equal(third.Id.ToString("D"), Field(entries[2], "eventId"));
    }

    [Fact]
    public async Task PublishPendingBatch_RespectsConfiguredBatchSize()
    {
        var messages = Enumerable.Range(0, 5)
            .Select(i => NewOutboxMessage(occurredAt: DateTime.UtcNow.AddSeconds(i)))
            .ToArray();
        await SeedAsync(messages);

        await CreatePublisher(batchSize: 2).PublishPendingBatchAsync(CancellationToken.None);

        Assert.Equal(2, (await ReadEventsAsync()).Length);
        var publishedCount = await WithDbAsync(db => db.OutboxMessages.CountAsync(x => x.PublishedAtUtc != null));
        Assert.Equal(2, publishedCount);
    }

    [Fact]
    public async Task PublishPendingBatch_Failure_StopsBatchAfterFirstMessage()
    {
        var first = NewOutboxMessage(occurredAt: DateTime.UtcNow.AddMinutes(-2));
        var second = NewOutboxMessage(occurredAt: DateTime.UtcNow.AddMinutes(-1));
        await SeedAsync(first, second);

        await using var broken = await BrokenRedisAsync();
        await CreatePublisher(redis: broken).PublishPendingBatchAsync(CancellationToken.None);

        var savedFirst = await WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == first.Id));
        var savedSecond = await WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == second.Id));
        Assert.Equal(1, savedFirst.RetryCount);
        Assert.Equal(0, savedSecond.RetryCount);
        Assert.Null(savedSecond.PublishedAtUtc);
    }

    [Fact]
    public async Task PublishPendingBatch_Cancelled_ThrowsAndPublishesNothing()
    {
        await SeedAsync(NewOutboxMessage());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreatePublisher().PublishPendingBatchAsync(cts.Token));

        Assert.Empty(await ReadEventsAsync());
        var publishedCount = await WithDbAsync(db => db.OutboxMessages.CountAsync(x => x.PublishedAtUtc != null));
        Assert.Equal(0, publishedCount);
    }

    [Fact]
    public async Task PublishPendingBatch_MultiplePending_PublishesAll()
    {
        var messages = Enumerable.Range(0, 3)
            .Select(i => NewOutboxMessage(occurredAt: DateTime.UtcNow.AddSeconds(i)))
            .ToArray();
        await SeedAsync(messages);

        await CreatePublisher().PublishPendingBatchAsync(CancellationToken.None);

        Assert.Equal(3, (await ReadEventsAsync()).Length);
        var publishedCount = await WithDbAsync(db => db.OutboxMessages.CountAsync(x => x.PublishedAtUtc != null));
        Assert.Equal(3, publishedCount);
    }

    [Fact]
    public async Task PublishPendingBatch_CrashBeforeSave_RepublishesSameEventId()
    {
        var message = NewOutboxMessage();
        await SeedAsync(message);

        await CreatePublisher().PublishPendingBatchAsync(CancellationToken.None);

        // Simulate a crash after XADD but before PublishedAtUtc was persisted.
        await WithDbAsync(async db =>
        {
            var tracked = await db.OutboxMessages.SingleAsync(x => x.Id == message.Id);
            tracked.PublishedAtUtc = null;
            await db.SaveChangesAsync();
        });

        await CreatePublisher().PublishPendingBatchAsync(CancellationToken.None);

        var entries = await ReadEventsAsync();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, e => Assert.Equal(message.Id.ToString("D"), Field(e, "eventId")));
    }

    [Fact]
    public async Task PublishPendingBatch_EmptyOutbox_PublishesNothing()
    {
        await CreatePublisher().PublishPendingBatchAsync(CancellationToken.None);

        Assert.Empty(await ReadEventsAsync());
    }

    private OutboxPublisher CreatePublisher(int batchSize = 20, IConnectionMultiplexer? redis = null) =>
        new(ScopeFactory, redis ?? Fixture.Multiplexer, Logger<OutboxPublisher>(), batchSize);

    private Task SeedAsync(params OutboxMessage[] messages) =>
        WithDbAsync(async db =>
        {
            db.OutboxMessages.AddRange(messages);
            await db.SaveChangesAsync();
        });

    private Task<StreamEntry[]> ReadEventsAsync() =>
        Redis.StreamRangeAsync(OutboxPublisher.StreamKey, "-", "+");

    private static string Field(StreamEntry entry, string name) =>
        entry.Values.First(v => v.Name == name).Value.ToString();

    private static OutboxMessage NewOutboxMessage(DateTime? occurredAt = null, Guid? id = null)
    {
        var eventId = id ?? Guid.NewGuid();
        return new OutboxMessage
        {
            Id = eventId,
            EventType = "OrderCreated",
            Payload = JsonSerializer.Serialize(new OrderCreatedEvent(
                eventId, Guid.NewGuid(), Guid.NewGuid(), 100m, DateTime.UtcNow)),
            OccurredAtUtc = occurredAt ?? DateTime.UtcNow
        };
    }

    private static async Task<IConnectionMultiplexer> BrokenRedisAsync()
    {
        var options = ConfigurationOptions.Parse("localhost:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 500;
        options.SyncTimeout = 500;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }
}
