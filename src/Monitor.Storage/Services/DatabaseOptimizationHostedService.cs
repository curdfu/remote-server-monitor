using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Monitor.Storage.Services;

public sealed class DatabaseOptimizationHostedService(
    DatabaseInitializer databaseInitializer,
    ILogger<DatabaseOptimizationHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            logger.LogInformation("Ensuring deferred SQLite optimization indexes...");
            await databaseInitializer.OptimizeAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to ensure deferred SQLite optimization indexes.");
        }
    }
}
