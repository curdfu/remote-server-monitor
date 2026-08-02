using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Storage.Repositories;
using Monitor.Storage.Services;
using Microsoft.Data.Sqlite;

namespace Monitor.Service.HostedServices;

// AggregationHostedService 定期驱动网络聚合器刷新实时速率，并把已经封口的流量 bucket 批量写入 SQLite。
// ETW 原始事件由 collector 推送到聚合器，本服务不直接处理单个包，只负责刷新和持久化节奏。
public sealed class AggregationHostedService(
    ILogger<AggregationHostedService> logger,
    INetworkAggregator networkAggregator,
    INetworkCollector networkCollector,
    NetworkTrafficRepository networkTrafficRepository,
    IMonitorSettingsProvider settings) : BackgroundService
{
    private const int PersistenceBatchSize = 500;
    private static readonly TimeSpan MinimumRealtimeRefreshTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumRealtimeRefreshTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);
    private DateTimeOffset _lastCycleFailureLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPersistenceFailureLogAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先刷新一次，避免前端第一次请求只能读到空的实时缓存。
        await RunRealtimeCycleSafeAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMilliseconds(settings.Current.NetworkRealtimeIntervalMs);
            await WaitForIntervalOrSettingsChangeAsync(interval, stoppingToken);
            await RunRealtimeCycleSafeAsync(stoppingToken);
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

    private async Task WaitForIntervalOrSettingsChangeAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var settingsChangedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = settings.RegisterChangeCallback(_ => settingsChangedSource.TrySetResult());
        var delayTask = Task.Delay(interval, cancellationToken);
        var completedTask = await Task.WhenAny(delayTask, settingsChangedSource.Task);

        if (completedTask == delayTask)
        {
            await delayTask;
        }
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

            try
            {
                await networkTrafficRepository.SaveAsync(batch, cancellationToken);
                // 只有数据库写入成功后才确认 bucket，失败时保留在聚合器 pending 队列等待下一轮。
                networkAggregator.ConfirmPendingBuckets(batch.Count);
                logger.LogDebug("Persisted {Count} network traffic buckets.", batch.Count);
            }
            catch (SqliteException exception) when (SqliteBusyRetry.IsBusy(exception))
            {
                LogPersistenceFailure(
                    LogLevel.Warning,
                    exception,
                    "SQLite remained busy after retries. Retaining {Count} network traffic buckets for the next cycle.",
                    batch.Count);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 实时快照已在当前周期先更新；持久化错误只能延后写入，不能让后台服务退出。
                LogPersistenceFailure(
                    LogLevel.Error,
                    exception,
                    "Network traffic persistence failed unexpectedly. Retaining {Count} network traffic buckets for the next cycle.",
                    batch.Count);
                return;
            }
        }
    }

    private async Task RunRealtimeCycleSafeAsync(CancellationToken stoppingToken)
    {
        using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cycleCancellation.CancelAfter(GetRealtimeRefreshTimeout());

        try
        {
            await RefreshRealtimeCacheAsync(cycleCancellation.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (cycleCancellation.IsCancellationRequested)
        {
            LogCycleFailure(
                LogLevel.Warning,
                null,
                "Network realtime aggregation cycle exceeded its timeout of {Timeout}. The next cycle will retry.",
                GetRealtimeRefreshTimeout());
        }
        catch (Exception exception)
        {
            LogCycleFailure(
                LogLevel.Error,
                exception,
                "Network realtime aggregation cycle failed. The next cycle will retry.");
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

    private TimeSpan GetRealtimeRefreshTimeout()
    {
        var configuredInterval = TimeSpan.FromMilliseconds(settings.Current.NetworkRealtimeIntervalMs);
        var scaledTimeout = TimeSpan.FromTicks(configuredInterval.Ticks * 5);
        return scaledTimeout < MinimumRealtimeRefreshTimeout
            ? MinimumRealtimeRefreshTimeout
            : scaledTimeout > MaximumRealtimeRefreshTimeout
                ? MaximumRealtimeRefreshTimeout
                : scaledTimeout;
    }

    private void LogPersistenceFailure(
        LogLevel level,
        Exception exception,
        string message,
        int bucketCount)
    {
        if (!ShouldLogFailure(ref _lastPersistenceFailureLogAt))
        {
            return;
        }

        logger.Log(level, exception, message, bucketCount);
    }

    private void LogCycleFailure(
        LogLevel level,
        Exception? exception,
        string message,
        params object?[] values)
    {
        if (!ShouldLogFailure(ref _lastCycleFailureLogAt))
        {
            return;
        }

        logger.Log(level, exception, message, values);
    }

    private static bool ShouldLogFailure(ref DateTimeOffset lastFailureLogAt)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastFailureLogAt < FailureLogInterval)
        {
            return false;
        }

        lastFailureLogAt = now;
        return true;
    }
}
