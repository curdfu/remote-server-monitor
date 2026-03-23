using Monitor.Contracts.Options;
using Monitor.Storage.Services;

namespace Monitor.Service.HostedServices;

public sealed class CleanupHostedService(
    ILogger<CleanupHostedService> logger,
    RetentionService retentionService,
    IMonitorSettingsProvider settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunCleanupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromHours(Math.Max(1, settings.Current.HistoryRetentionDays > 0 ? 24 : 1));
            var settingsChanged = await WaitForIntervalOrSettingsChangeAsync(interval, stoppingToken);
            if (settingsChanged)
            {
                await RunCleanupAsync(stoppingToken);
                continue;
            }

            await RunCleanupAsync(stoppingToken);
        }
    }

    private async Task<bool> WaitForIntervalOrSettingsChangeAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var settingsChangedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = settings.RegisterChangeCallback(_ => settingsChangedSource.TrySetResult());
        var delayTask = Task.Delay(interval, cancellationToken);
        var completedTask = await Task.WhenAny(delayTask, settingsChangedSource.Task);

        if (completedTask == delayTask)
        {
            await delayTask;
            return false;
        }

        return true;
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Running cleanup cycle.");
        await retentionService.CleanupAsync(cancellationToken);
    }
}
