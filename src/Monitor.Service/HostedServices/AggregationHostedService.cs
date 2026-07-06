using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Storage.Repositories;

namespace Monitor.Service.HostedServices;

// AggregationHostedService 定期驱动网络聚合器刷新实时速率，并把已经封口的流量 bucket 批量写入 SQLite。
// ETW 原始事件由 collector 推送到聚合器，本服务不直接处理单个包，只负责刷新和持久化节奏。
public sealed class AggregationHostedService(
    ILogger<AggregationHostedService> logger,
    INetworkAggregator networkAggregator,
    INetworkCollector networkCollector,
    NetworkTrafficRepository networkTrafficRepository) : BackgroundService
{
    private const int PersistenceBatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先刷新一次，避免前端第一次请求只能读到空的实时缓存。
        await RefreshRealtimeCacheAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(MonitorSettings.NetworkRealtimeIntervalMs), stoppingToken);
            await RefreshRealtimeCacheAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // 停止顺序先截断 ETW 输入，再等待聚合队列追平，最后持久化 pending bucket。
        await networkCollector.StopAsync(cancellationToken);
        await networkAggregator.FlushAsync(cancellationToken);
        await PersistPendingBucketsAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
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
            // 只有数据库写入成功后才确认 bucket，失败时保留在聚合器 pending 队列等待下一轮。
            networkAggregator.ConfirmPendingBuckets(batch.Count);
            logger.LogDebug("Persisted {Count} network traffic buckets.", batch.Count);
        }
    }

    private async Task RefreshRealtimeCacheAsync(CancellationToken cancellationToken)
    {
        // GetRealtimeSnapshotAsync 会等待已入队事件处理完，因此这里也是网络链路的定期追平点。
        var snapshot = await networkAggregator.GetRealtimeSnapshotAsync(cancellationToken);
        await PersistPendingBucketsAsync(cancellationToken);

        logger.LogDebug(
            "Aggregated network snapshot at {SampleTime}, up={Upload}, down={Download}.",
            snapshot.SampleTime,
            snapshot.TotalUploadBytesPerSecond,
            snapshot.TotalDownloadBytesPerSecond);
    }
}
