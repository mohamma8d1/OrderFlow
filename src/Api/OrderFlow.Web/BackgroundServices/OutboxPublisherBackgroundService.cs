using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFlow.DataAccess.Messaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OrderFlow.Web.BackgroundServices;

public sealed class OutboxPublisherBackgroundService(
        OutboxPublisher publisher,
        ILogger<OutboxPublisherBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox publisher started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await publisher.PublishPendingBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while publishing outbox messages.");
            }

            try
            {
                await Task.Delay(PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Outbox publisher stopped.");
    }
}
