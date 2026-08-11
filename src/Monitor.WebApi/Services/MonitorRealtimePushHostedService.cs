using Microsoft.Extensions.Hosting;
using Monitor.Contracts.Options;

namespace Monitor.WebApi.Services;

// 实时推送服务只负责调度硬件广播频率，实际 DTO 组装和 SignalR 发送交给 MonitorRealtimeBroadcaster。
// 广播周期跟随硬件采样间隔，设置变化后立即触发一次广播。
public sealed class MonitorRealtimePushHostedService(
    MonitorRealtimeBroadcaster broadcaster,
    IMonitorSettingsProvider settings,
    ILogger<MonitorRealtimePushHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先尝试推送一次；如果采样缓存还没准备好，Broadcaster 会自行跳过。
        await BroadcastSafeAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = GetInterval(settings.Current);
            // 设置变化时不等待旧周期结束，立即推送一次，让前端尽快感知新采样节奏。
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
        // SignalR 单轮发送失败不应终止后台服务，下一周期继续尝试。
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
        // 最小 500ms 防止误配置导致广播过于频繁。
        var intervalMs = Math.Max(500, settings.HardwareSampleIntervalMs);
        return TimeSpan.FromMilliseconds(intervalMs);
    }
}
