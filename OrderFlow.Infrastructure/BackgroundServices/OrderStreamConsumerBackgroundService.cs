using System;
using global::OrderFlow.Application.Events;
using global::OrderFlow.Infrastructure.Persistence;
using global::OrderFlow.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFlow.Infrastructure.Persistence;
using StackExchange.Redis;
using System.Text.Json;

namespace OrderFlow.Infrastructure.BackgroundServices;



public sealed class OrderStreamConsumerBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer redis,
        ILogger<OrderStreamConsumerBackgroundService> logger) : BackgroundService
{
    private const string StreamKey = "orderflow:orders:events";
    private const string GroupName = "orderflow-order-consumers";
    private const int MaxDeliveryAttempts = 5;
    private const string DeadLetterStreamKey = "orderflow:orders:dead-letter";

    private const int BatchSize = 10;
    private const long MinimumIdleTimeMilliseconds = 5000; // 30000
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(1);
    private readonly string _consumerName = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private string _claimCursor = "0-0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var database = redis.GetDatabase();

        await EnsureConsumerGroupAsync(database, stoppingToken);

        logger.LogInformation("Order stream consumer started. Consumer: {ConsumerName}", _consumerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Recover old pending messages first.
                await RecoverPendingMessagesAsync(database, stoppingToken);

                // Read new messages.
                var entries = await database.StreamReadGroupAsync(
                    StreamKey,
                    GroupName,
                    _consumerName,
                    ">",
                    BatchSize,
                    CommandFlags.None);

                foreach (var entry in entries)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    await ProcessEntryAsync(
                        database,
                        entry,
                        stoppingToken);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred while consuming order events.");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        logger.LogInformation("Order stream consumer stopped.");
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
            MinimumIdleTimeMilliseconds,
            _claimCursor,
            BatchSize);

        if (result.IsNull)
            return;

        _claimCursor = result.NextStartId.ToString();

        if (string.IsNullOrWhiteSpace(_claimCursor))
            _claimCursor = "0-0";

        foreach (var entry in result.ClaimedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogWarning("Recovering pending message {MessageId}.", entry.Id);

            await ProcessEntryAsync(
                database,
                entry,
                cancellationToken);
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

            if (deliveryCount >= MaxDeliveryAttempts)
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
                    MaxDeliveryAttempts);
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

            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation("OrderCreated event recorded. OrderId: {OrderId}", orderEvent.OrderId);
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
