using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Storage.Repositories;

namespace Monitor.Service.HostedServices;

public sealed class AggregationHostedService(
    ILogger<AggregationHostedService> logger,
    INetworkAggregator networkAggregator,
    INetworkCollector networkCollector,
    NetworkTrafficRepository networkTrafficRepository,
    IMonitorSettingsProvider settings) : BackgroundService
{
    private const int PersistenceBatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshRealtimeCacheAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMilliseconds(settings.Current.NetworkSampleIntervalMs);
            var settingsChanged = await WaitForIntervalOrSettingsChangeAsync(interval, stoppingToken);
            if (settingsChanged)
            {
                await RefreshRealtimeCacheAsync(stoppingToken);
                continue;
            }

            await RefreshRealtimeCacheAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await networkCollector.StopAsync(cancellationToken);
        await networkAggregator.FlushAsync(cancellationToken);
        await PersistPendingBucketsAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
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

    private async Task PersistPendingBucketsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = networkAggregator.PeekPendingBuckets(PersistenceBatchSize);
            if (batch.Count == 0)
            {
                return;
            }

            await networkTrafficRepository.SaveAsync(batch, cancellationToken);
            networkAggregator.ConfirmPendingBuckets(batch.Count);
            logger.LogDebug("Persisted {Count} network traffic buckets.", batch.Count);
        }
    }

    private async Task RefreshRealtimeCacheAsync(CancellationToken cancellationToken)
    {
        var snapshot = await networkAggregator.GetRealtimeSnapshotAsync(cancellationToken);
        await PersistPendingBucketsAsync(cancellationToken);

        logger.LogDebug(
            "Aggregated network snapshot at {SampleTime}, up={Upload}, down={Download}, apps={AppCount}.",
            snapshot.SampleTime,
            snapshot.TotalUploadBytesPerSecond,
            snapshot.TotalDownloadBytesPerSecond,
            snapshot.AppUsages.Count);
    }
}
