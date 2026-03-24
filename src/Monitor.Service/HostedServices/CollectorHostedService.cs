using Monitor.Contracts.Options;
using Monitor.Hardware.Abstractions;
using Monitor.Network.Abstractions;
using Monitor.Storage.Repositories;

namespace Monitor.Service.HostedServices;

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
        await networkCollector.StartAsync(stoppingToken);
        await CollectSnapshotAsync(stoppingToken);

        var lastPersistedAt = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMilliseconds(settings.Current.HardwareSampleIntervalMs);
            var settingsChanged = await WaitForIntervalOrSettingsChangeAsync(interval, stoppingToken);
            if (settingsChanged)
            {
                await CollectSnapshotAsync(stoppingToken);
                continue;
            }

            await CollectSnapshotAsync(stoppingToken);

            var now = DateTimeOffset.UtcNow;
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
