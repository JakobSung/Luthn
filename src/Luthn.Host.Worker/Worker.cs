namespace Luthn.Host.Worker;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Luthn worker started without background jobs configured.");
        return Task.CompletedTask;
    }
}
