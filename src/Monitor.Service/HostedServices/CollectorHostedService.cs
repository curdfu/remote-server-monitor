using Monitor.Contracts.Options;
using Monitor.Hardware.Abstractions;
using Monitor.Network.Abstractions;
using Monitor.Storage.Repositories;
using Monitor.Storage.Services;
using Microsoft.Data.Sqlite;

namespace Monitor.Service.HostedServices;

// CollectorHostedService 负责启动网络 ETW 采集，并按配置周期采样硬件快照。
// 硬件快照先进入内存缓冲，再按数量或时间批量落库，避免高频采样时每次都写 SQLite。
public sealed class CollectorHostedService(
    ILogger<CollectorHostedService> logger,
    IHardwareCollector hardwareCollector,
    IHardwareSnapshotBuffer hardwareSnapshotBuffer,
    IHardwareMonitoringDemand hardwareMonitoringDemand,
    HardwareRepository hardwareRepository,
    INetworkCollector networkCollector,
    IMonitorSettingsProvider settings) : BackgroundService
{
    private static readonly TimeSpan PersistenceInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopFlushTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PersistenceFailureLogInterval = TimeSpan.FromMinutes(1);
    private const int PersistenceBatchSize = 10;
    private DateTimeOffset _lastPersistenceFailureLogAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 网络采集是事件驱动，必须先启动 ETW 会话；硬件采样随后立即执行一次，保证首屏有数据。
        await networkCollector.StartAsync(stoppingToken);
        var handledHardwareActivationVersion = hardwareMonitoringDemand.ActivationVersion;
        await CollectSnapshotAsync(stoppingToken);

        var lastPersistedAt = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMilliseconds(settings.Current.HardwareSampleIntervalMs);
            // 设置变化或首页首次订阅时不等待旧 delay 结束，立即采一次并进入新周期。
            var wokeEarly = await WaitForIntervalSettingsOrHardwareDemandAsync(
                interval,
                handledHardwareActivationVersion,
                stoppingToken);
            handledHardwareActivationVersion = hardwareMonitoringDemand.ActivationVersion;
            if (wokeEarly)
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

    private async Task<bool> WaitForIntervalSettingsOrHardwareDemandAsync(
        TimeSpan interval,
        long observedHardwareActivationVersion,
        CancellationToken cancellationToken)
    {
        // 用 TaskCompletionSource 把配置变更和硬件订阅激活并入等待逻辑，避免后台轮询。
        var settingsChangedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = settings.RegisterChangeCallback(_ => settingsChangedSource.TrySetResult());
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(interval, waitCancellation.Token);
        var hardwareActivatedTask = hardwareMonitoringDemand.WaitForActivationAsync(
            observedHardwareActivationVersion,
            waitCancellation.Token);
        var completedTask = await Task.WhenAny(
            delayTask,
            settingsChangedSource.Task,
            hardwareActivatedTask);

        if (completedTask == delayTask)
        {
            await delayTask;
            return false;
        }

        if (completedTask == hardwareActivatedTask)
        {
            await hardwareActivatedTask;
        }

        waitCancellation.Cancel();
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

            try
            {
                await repository.SaveBatchAsync(batch, cancellationToken);
                logger.LogDebug("Persisted {Count} hardware snapshots in batch.", batch.Count);
            }
            catch (SqliteException exception) when (SqliteBusyRetry.IsBusy(exception))
            {
                snapshotBuffer.RequeuePendingBatch(batch);
                LogPersistenceFailure(
                    LogLevel.Warning,
                    exception,
                    "SQLite remained busy after retries. Requeued {Count} hardware snapshots; PendingCount={PendingCount}.",
                    batch.Count,
                    snapshotBuffer.PendingCount);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 硬件历史写入失败时回队重试，避免一次 SQLite 异常终止整个采集服务。
                snapshotBuffer.RequeuePendingBatch(batch);
                LogPersistenceFailure(
                    LogLevel.Error,
                    exception,
                    "SQLite persistence failed unexpectedly. Requeued {Count} hardware snapshots; PendingCount={PendingCount}.",
                    batch.Count,
                    snapshotBuffer.PendingCount);
                return;
            }
        }
    }

    private void LogPersistenceFailure(
        LogLevel level,
        Exception exception,
        string message,
        int batchCount,
        int pendingCount)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPersistenceFailureLogAt < PersistenceFailureLogInterval)
        {
            return;
        }

        _lastPersistenceFailureLogAt = now;
        logger.Log(level, exception, message, batchCount, pendingCount);
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
