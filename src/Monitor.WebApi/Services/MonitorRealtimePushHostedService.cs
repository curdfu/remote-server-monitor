using Microsoft.Extensions.Hosting;
using Monitor.Contracts.Options;

namespace Monitor.WebApi.Services;

public sealed class MonitorRealtimePushHostedService(
    MonitorRealtimeBroadcaster broadcaster,
    IMonitorSettingsProvider settings,
    ILogger<MonitorRealtimePushHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await BroadcastSafeAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = GetInterval(settings.Current);
            var settingsChanged = await WaitForIntervalOrSettingsChangeAsync(interval, stoppingToken);
            if (settingsChanged)
            {
                await BroadcastSafeAsync(stoppingToken);
                continue;
            }

            await BroadcastSafeAsync(stoppingToken);
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

    private async Task BroadcastSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await broadcaster.BroadcastOnceAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "SignalR realtime broadcast cycle failed.");
        }
    }

    private static TimeSpan GetInterval(MonitorSettings settings)
    {
        var intervalMs = Math.Max(
            500,
            Math.Min(settings.HardwareSampleIntervalMs, MonitorSettings.NetworkRealtimeIntervalMs));
        return TimeSpan.FromMilliseconds(intervalMs);
    }
}
