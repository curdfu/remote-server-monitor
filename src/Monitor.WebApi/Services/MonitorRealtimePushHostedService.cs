using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monitor.Contracts.Options;

namespace Monitor.WebApi.Services;

public sealed class MonitorRealtimePushHostedService(
    MonitorRealtimeBroadcaster broadcaster,
    IOptionsMonitor<MonitorSettings> settings,
    ILogger<MonitorRealtimePushHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await BroadcastSafeAsync(stoppingToken);

        using var timer = new PeriodicTimer(GetInterval(settings.CurrentValue));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await BroadcastSafeAsync(stoppingToken);
        }
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
        var intervalMs = Math.Max(500, Math.Min(settings.HardwareSampleIntervalMs, settings.NetworkSampleIntervalMs));
        return TimeSpan.FromMilliseconds(intervalMs);
    }
}
