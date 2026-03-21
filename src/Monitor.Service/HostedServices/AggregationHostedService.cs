using Microsoft.Extensions.Options;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Storage.Repositories;

namespace Monitor.Service.HostedServices;

public sealed class AggregationHostedService(
    ILogger<AggregationHostedService> logger,
    INetworkAggregator networkAggregator,
    NetworkTrafficRepository networkTrafficRepository,
    IOptionsMonitor<MonitorSettings> settings) : BackgroundService
{
    private const int PersistenceBatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshRealtimeCacheAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settings.CurrentValue.NetworkSampleIntervalMs));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshRealtimeCacheAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await PersistPendingBucketsAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task PersistPendingBucketsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = networkAggregator.DequeuePendingBuckets(PersistenceBatchSize);
            if (batch.Count == 0)
            {
                return;
            }

            await networkTrafficRepository.SaveAsync(batch, cancellationToken);
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
