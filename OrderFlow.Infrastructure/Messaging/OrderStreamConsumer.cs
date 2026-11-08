using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderFlow.Application.Events;
using OrderFlow.Infrastructure.Persistence;
using OrderFlow.Infrastructure.Persistence.Outbox;
using StackExchange.Redis;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OrderFlow.Infrastructure.Messaging;

public sealed class OrderStreamConsumer(
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    ILogger<OrderStreamConsumer> logger,
    string? consumerName = null,
    int maxDeliveryAttempts = 5,
    long minimumIdleTimeMilliseconds = 5000,
    int batchSize = 10)
{
    public const string StreamKey = "orderflow:orders:events";
    public const string GroupName = "orderflow-order-consumers";
    public const string DeadLetterStreamKey = "orderflow:orders:dead-letter";

    private readonly string _consumerName = consumerName ?? $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private string _claimCursor = "0-0";

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        EnsureConsumerGroupAsync(redis.GetDatabase(), cancellationToken);

    public async Task ProcessOnceAsync(CancellationToken cancellationToken)
    {
        var database = redis.GetDatabase();

        await RecoverPendingMessagesAsync(database, cancellationToken);

        var entries = await database.StreamReadGroupAsync(
            StreamKey,
            GroupName,
            _consumerName,
            ">",
            batchSize,
            CommandFlags.None);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await ProcessEntryAsync(database, entry, cancellationToken);
        }
    }

    private async Task EnsureConsumerGroupAsync(
        IDatabase database,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await database.StreamCreateConsumerGroupAsync(
                    StreamKey,
                    GroupName,
                    "0-0",
                    createStream: true);

                logger.LogInformation("Consumer group {GroupName} created.", GroupName);

                return;
            }
            catch (RedisServerException ex)
                when (ex.Message.Contains(
                    "BUSYGROUP",
                    StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Consumer group {GroupName} already exists.", GroupName);

                return;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not initialize Redis consumer group.");

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task RecoverPendingMessagesAsync(
        IDatabase database,
        CancellationToken cancellationToken)
    {
        var result = await database.StreamAutoClaimAsync(
            StreamKey,
            GroupName,
            _consumerName,
            minimumIdleTimeMilliseconds,
            _claimCursor,
            batchSize);

        if (result.IsNull)
            return;

        _claimCursor = result.NextStartId.ToString();

        if (string.IsNullOrWhiteSpace(_claimCursor))
            _claimCursor = "0-0";

        foreach (var entry in result.ClaimedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogWarning("Recovering pending message {MessageId}.", entry.Id);

            await ProcessEntryAsync(database, entry, cancellationToken);
        }

        if (result.DeletedIds.Length > 0)
            logger.LogWarning("Redis reported {Count} deleted pending entries.", result.DeletedIds.Length);
    }

    private async Task ProcessEntryAsync(
        IDatabase redisDatabase,
        StreamEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var fields = entry.Values.ToDictionary(
                field => field.Name.ToString(),
                field => field.Value.ToString(),
                StringComparer.Ordinal);

            if (!fields.TryGetValue("eventId", out var eventIdText) || !fields.TryGetValue("eventType", out var eventType) || !fields.TryGetValue("payload", out var payload))
                throw new InvalidOperationException($"Required fields are missing from message {entry.Id}.");

            if (!Guid.TryParse(eventIdText, out var eventId))
                throw new InvalidOperationException($"Invalid EventId in message {entry.Id}.");

            switch (eventType)
            {
                case "OrderCreated":
                    {
                        var orderCreatedEvent = JsonSerializer.Deserialize<OrderCreatedEvent>(payload);

                        if (orderCreatedEvent is null)
                            throw new InvalidOperationException($"Invalid OrderCreated payload. Message: {entry.Id}");

                        if (orderCreatedEvent.EventId != eventId)
                            throw new InvalidOperationException($"EventId mismatch. Message: {entry.Id}");

                        await HandleOrderCreatedAsync(orderCreatedEvent, entry.Id.ToString(), cancellationToken);

                        break;
                    }

                default:
                    throw new NotSupportedException($"Unsupported event type: {eventType}");
            }

            var acknowledged = await redisDatabase.StreamAcknowledgeAsync(
                StreamKey,
                GroupName,
                entry.Id);

            if (acknowledged == 0)
            {
                logger.LogWarning("Message {MessageId} was not acknowledged.", entry.Id);

                return;
            }

            logger.LogInformation("Message {MessageId} processed and acknowledged.", entry.Id);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to process Redis message {MessageId}.",
                entry.Id);

            var deliveryCount = await GetDeliveryCountAsync(
                redisDatabase,
                entry.Id);

            if (deliveryCount >= maxDeliveryAttempts)
            {
                await MoveToDeadLetterAsync(
                    redisDatabase,
                    entry,
                    deliveryCount,
                    ex.Message);
            }
            else
            {
                logger.LogWarning(
                    "Message {MessageId} failed. Delivery attempt: {DeliveryCount}/{MaxAttempts}.",
                    entry.Id,
                    deliveryCount,
                    maxDeliveryAttempts);
            }
        }
    }

    private async Task HandleOrderCreatedAsync(
        OrderCreatedEvent orderEvent,
        string streamEntryId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var dbContext = scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var alreadyProcessed = await dbContext.ProcessedEvents
            .AnyAsync(
                x => x.EventId == orderEvent.EventId,
                cancellationToken);

        if (!alreadyProcessed)
        {
            dbContext.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = orderEvent.EventId,
                EventType = "OrderCreated",
                StreamEntryId = streamEntryId,
                ProcessedAtUtc = DateTime.UtcNow
            });

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogInformation("OrderCreated event recorded. OrderId: {OrderId}", orderEvent.OrderId);
            }
            catch (DbUpdateException)
            {
                // A concurrent consumer inserted the same EventId first. The primary key
                // guarantees uniqueness, so treat this as an idempotent duplicate rather
                // than a failure that would burn delivery attempts and dead-letter the event.
                var existsNow = await dbContext.ProcessedEvents
                    .AsNoTracking()
                    .AnyAsync(x => x.EventId == orderEvent.EventId, cancellationToken);

                if (!existsNow)
                    throw;

                logger.LogInformation("Duplicate event detected concurrently. EventId: {EventId}", orderEvent.EventId);
            }
        }
        else
        {
            logger.LogInformation("Duplicate event detected. EventId: {EventId}", orderEvent.EventId);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<long> GetDeliveryCountAsync(
        IDatabase database,
        RedisValue messageId)
    {
        var pendingMessages = await database.StreamPendingMessagesAsync(
            StreamKey,
            GroupName,
            count: 100,
            consumerName: RedisValue.Null,
            minId: messageId,
            maxId: messageId);

        if (pendingMessages.Length == 0)
            return 0;

        return pendingMessages[0].DeliveryCount;
    }

    private async Task MoveToDeadLetterAsync(
        IDatabase database,
        StreamEntry entry,
        long deliveryCount,
        string failureReason)
    {
        var fields = entry.Values
            .Concat(new[]
            {
                new NameValueEntry("originalStreamEntryId", entry.Id),
                new NameValueEntry("deliveryCount", deliveryCount),
                new NameValueEntry("failureReason", failureReason),
                new NameValueEntry("failedAtUtc", DateTime.UtcNow.ToString("O"))
            })
            .ToArray();

        await database.StreamAddAsync(DeadLetterStreamKey, fields);

        var acknowledged = await database.StreamAcknowledgeAsync(
            StreamKey,
            GroupName,
            entry.Id);

        if (acknowledged == 0)
            logger.LogWarning("Dead-letter message {MessageId} was added, but original message was not acknowledged.", entry.Id);
        else
            logger.LogError("Message {MessageId} moved to Dead Letter Stream after {DeliveryCount} attempts.", entry.Id, deliveryCount);
    }
}
