using Microsoft.Extensions.Options;
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
    IOptionsMonitor<MonitorSettings> settings) : BackgroundService
{
    private static readonly TimeSpan PersistenceInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopFlushTimeout = TimeSpan.FromSeconds(5);
    private const int PersistenceBatchSize = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await networkCollector.StartAsync(stoppingToken);
        await CollectSnapshotAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settings.CurrentValue.HardwareSampleIntervalMs));
        var lastPersistedAt = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
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
