using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderFlow.DataAccess.Core;
using OrderFlow.Common.Entities;
using StackExchange.Redis;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OrderFlow.DataAccess.Messaging;

public sealed class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    ILogger<OutboxPublisher> logger,
    int batchSize = 20)
{
    public const string StreamKey = "orderflow:orders:events";

    public async Task PublishPendingBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var dbContext = scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        IQueryable<OutboxMessage> query = dbContext.OutboxMessages
            .Where(x => x.PublishedAtUtc == null)
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.Id)
            .Take(batchSize);

        var messages = await query.ToListAsync(cancellationToken);

        if (messages.Count == 0)
            return;

        var database = redis.GetDatabase();

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RedisValue redisEntryId;

            try
            {
                redisEntryId = await database.StreamAddAsync(
                    StreamKey,
                    new NameValueEntry[]
                    {
                        new("eventId", message.Id.ToString("D")),
                        new("eventType", message.EventType),
                        new("payload", message.Payload),
                        new(
                            "occurredAtUtc",
                            message.OccurredAtUtc.ToString(
                                "O",
                                CultureInfo.InvariantCulture))
                    });
            }
            catch (Exception ex)
            {
                message.RetryCount++;

                message.LastError = ex.Message.Length > 2000
                    ? ex.Message[..2000]
                    : ex.Message;

                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogError(
                    ex,
                    "Failed to publish Outbox message {OutboxMessageId}. RetryCount: {RetryCount}",
                    message.Id,
                    message.RetryCount);

                break;
            }

            message.PublishedAtUtc = DateTime.UtcNow;
            message.LastError = null;

            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Outbox message {OutboxMessageId} published to Redis. Stream entry ID: {StreamEntryId}",
                message.Id,
                redisEntryId.ToString());
        }
    }
}
