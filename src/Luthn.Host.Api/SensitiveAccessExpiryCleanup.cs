namespace Luthn.Host.Api;

internal sealed class SensitiveAccessExpiryCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SensitiveAccessExpiryCleanupHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try
            {
                await Task.Delay(CleanupInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var workflow = scope.ServiceProvider.GetRequiredService<ISensitiveAccessSystemWorkflow>();
            var result = await workflow.MaterializeExpiriesAsync(
                timeProvider.GetUtcNow(),
                SensitiveAccessWorkflow.DefaultExpiryMaterializationBatchSize,
                cancellationToken);
            if (result.MaterializedCount > 0)
            {
                logger.LogInformation(
                    "Sensitive access expiry materialization completed: requests={RequestsExpired}, grants={GrantsExpired}.",
                    result.RequestsExpired,
                    result.GrantsExpired);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogError(
                error,
                "Sensitive access expiry materialization failed; synchronous authorization remains enforced.");
        }
    }
}
