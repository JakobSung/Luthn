using Luthn.Core.Memory;
using Luthn.Core.Persistence;

namespace Luthn.Host.Worker;

public sealed class Worker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Luthn safe-projection outbox worker started.");
        var disabledLogged = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<SafeProjectionOutboxProcessor>();
                var result = await processor.ProcessBatchAsync(
                    timeProvider.GetUtcNow(),
                    cancellationToken: stoppingToken);

                if (result.TransportState == SafeProjectionSyncTransportState.Disabled)
                {
                    if (!disabledLogged)
                    {
                        logger.LogInformation(
                            "External safe-projection transport is disabled; local outbox remains durable and untouched.");
                        disabledLogged = true;
                    }
                }
                else
                {
                    disabledLogged = false;
                    logger.LogInformation(
                        "Safe-projection batch completed: claimed={Claimed}, acknowledged={Acknowledged}, failed={Failed}, recovered={Recovered}, superseded={Superseded}.",
                        result.ClaimedCount,
                        result.AcknowledgedCount,
                        result.FailedCount,
                        result.RecoveredCount,
                        result.SupersededCount);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Safe-projection outbox processing failed.");
                await Task.Delay(TimeSpan.FromSeconds(15), timeProvider, stoppingToken);
            }
        }
    }
}
