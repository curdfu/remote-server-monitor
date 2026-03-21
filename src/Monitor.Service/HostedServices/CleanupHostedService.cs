using Microsoft.Extensions.Options;
using Monitor.Contracts.Options;
using Monitor.Storage.Services;

namespace Monitor.Service.HostedServices;

public sealed class CleanupHostedService(
    ILogger<CleanupHostedService> logger,
    RetentionService retentionService,
    IOptionsMonitor<MonitorSettings> settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunCleanupAsync(stoppingToken);

        var interval = TimeSpan.FromHours(Math.Max(1, settings.CurrentValue.HistoryRetentionDays > 0 ? 24 : 1));
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCleanupAsync(stoppingToken);
        }
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Running cleanup cycle.");
        await retentionService.CleanupAsync(cancellationToken);
    }
}
