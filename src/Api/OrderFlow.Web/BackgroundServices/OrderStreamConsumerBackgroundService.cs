using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFlow.DataAccess.Messaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OrderFlow.Web.BackgroundServices;

public sealed class OrderStreamConsumerBackgroundService(
        OrderStreamConsumer consumer,
        ILogger<OrderStreamConsumerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await consumer.InitializeAsync(stoppingToken);

        logger.LogInformation("Order stream consumer started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await consumer.ProcessOnceAsync(stoppingToken);
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

        logger.LogInformation("Order stream consumer stopped.");
    }
}
