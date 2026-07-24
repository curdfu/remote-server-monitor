using Monitor.Contracts.Options;
using Monitor.Storage.Services;

namespace Monitor.Service.HostedServices;

public sealed class CleanupHostedService(
    ILogger<CleanupHostedService> logger,
    RetentionService retentionService,
    IMonitorSettingsProvider settings) : BackgroundService
{
    private static readonly TimeSpan StartupCleanupDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupCleanupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await RunCleanupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromHours(Math.Max(1, settings.Current.HistoryRetentionDays > 0 ? 24 : 1));
            var cleanupRequired = await WaitForScheduledCleanupOrRetentionReductionAsync(interval, stoppingToken);
            if (!cleanupRequired)
            {
                continue;
            }

            await RunCleanupAsync(stoppingToken);
        }
    }

    private async Task<bool> WaitForScheduledCleanupOrRetentionReductionAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var observedRetentionDays = settings.Current.HistoryRetentionDays;
        var retentionChangedSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = settings.RegisterChangeCallback(updated =>
        {
            if (updated.HistoryRetentionDays != observedRetentionDays)
            {
                retentionChangedSource.TrySetResult(updated.HistoryRetentionDays);
            }
        });

        // 覆盖读取基线和注册回调之间的极小竞态窗口。
        var currentRetentionDays = settings.Current.HistoryRetentionDays;
        if (currentRetentionDays != observedRetentionDays)
        {
            retentionChangedSource.TrySetResult(currentRetentionDays);
        }

        var delayTask = Task.Delay(interval, cancellationToken);
        var completedTask = await Task.WhenAny(delayTask, retentionChangedSource.Task);

        if (completedTask == delayTask)
        {
            await delayTask;
            return true;
        }

        var updatedRetentionDays = await retentionChangedSource.Task;
        return updatedRetentionDays > 0 && updatedRetentionDays < observedRetentionDays;
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Running cleanup cycle.");
            await retentionService.CleanupAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cleanup cycle failed. The service will retry on the next scheduled cleanup cycle.");
        }
    }
}
