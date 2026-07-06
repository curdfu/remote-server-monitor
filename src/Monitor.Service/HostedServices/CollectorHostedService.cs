using Monitor.Contracts.Options;
using Monitor.Hardware.Abstractions;
using Monitor.Network.Abstractions;
using Monitor.Storage.Repositories;

namespace Monitor.Service.HostedServices;

// CollectorHostedService 负责启动网络 ETW 采集，并按配置周期采样硬件快照。
// 硬件快照先进入内存缓冲，再按数量或时间批量落库，避免高频采样时每次都写 SQLite。
public sealed class CollectorHostedService(
    ILogger<CollectorHostedService> logger,
    IHardwareCollector hardwareCollector,
    IHardwareSnapshotBuffer hardwareSnapshotBuffer,
    HardwareRepository hardwareRepository,
    INetworkCollector networkCollector,
    IMonitorSettingsProvider settings) : BackgroundService
{
    private static readonly TimeSpan PersistenceInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopFlushTimeout = TimeSpan.FromSeconds(5);
    private const int PersistenceBatchSize = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 网络采集是事件驱动，必须先启动 ETW 会话；硬件采样随后立即执行一次，保证首屏有数据。
        await networkCollector.StartAsync(stoppingToken);
        await CollectSnapshotAsync(stoppingToken);

        var lastPersistedAt = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMilliseconds(settings.Current.HardwareSampleIntervalMs);
            // 采样间隔支持运行时修改；设置变化时不等旧 delay 结束，立即采一次并进入新周期。
            var settingsChanged = await WaitForIntervalOrSettingsChangeAsync(interval, stoppingToken);
            if (settingsChanged)
            {
                await CollectSnapshotAsync(stoppingToken);
                continue;
            }

            await CollectSnapshotAsync(stoppingToken);

            var now = DateTimeOffset.UtcNow;
            // 数量阈值限制内存增长，时间阈值保证低频变化时也会定期落库。
            if (hardwareSnapshotBuffer.PendingCount >= PersistenceBatchSize || now - lastPersistedAt >= PersistenceInterval)
            {
                await PersistPendingSnapshotsAsync(hardwareSnapshotBuffer, hardwareRepository, stoppingToken);
                lastPersistedAt = now;
            }
        }

        await FlushPendingSnapshotsAsync(CancellationToken.None);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await networkCollector.StopAsync(cancellationToken);
        await FlushPendingSnapshotsAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task<bool> WaitForIntervalOrSettingsChangeAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        // 用 TaskCompletionSource 把配置变更回调并入等待逻辑，避免后台循环轮询配置版本。
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

    private async Task FlushPendingSnapshotsAsync(CancellationToken cancellationToken)
    {
        // 关闭时尽力刷盘，但设置超时，避免 Windows 服务停止被 SQLite 写入长时间阻塞。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(StopFlushTimeout);

        try
        {
            await PersistPendingSnapshotsAsync(hardwareSnapshotBuffer, hardwareRepository, timeoutCts.Token, stopWhenCancellationRequested: false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            logger.LogWarning("Timed out while flushing pending hardware snapshots during shutdown.");
        }
    }

    private async Task PersistPendingSnapshotsAsync(
        IHardwareSnapshotBuffer snapshotBuffer,
        HardwareRepository repository,
        CancellationToken cancellationToken,
        bool stopWhenCancellationRequested = true)
    {
        while (true)
        {
            if (stopWhenCancellationRequested && cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // 硬件历史是辅助数据，按批从内存缓冲取出后直接保存；网络流量这类不可丢数据另有确认机制。
            var batch = snapshotBuffer.DequeuePendingBatch(PersistenceBatchSize);
            if (batch.Count == 0)
            {
                return;
            }

            await repository.SaveBatchAsync(batch, cancellationToken);
            logger.LogDebug("Persisted {Count} hardware snapshots in batch.", batch.Count);
        }
    }

    private async Task CollectSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await hardwareCollector.GetCurrentSnapshotAsync(cancellationToken);
        hardwareSnapshotBuffer.Add(snapshot);

        logger.LogDebug(
            "Collected hardware snapshot at {SampleTime}. Pending hardware samples: {PendingCount}.",
            snapshot.SampleTime,
            hardwareSnapshotBuffer.PendingCount);
    }
}
