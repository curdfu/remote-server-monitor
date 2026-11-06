using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Storage.Repositories;
using Monitor.Storage.Services;
using Microsoft.Data.Sqlite;

namespace Monitor.Service.HostedServices;

// AggregationHostedService 定期追平网络事件，并把已经封口的流量 bucket 批量写入 SQLite。
// ETW 原始事件由 collector 推送到聚合器，本服务不直接处理单个包，只负责追平和持久化节奏。
public sealed class AggregationHostedService(
    ILogger<AggregationHostedService> logger,
    INetworkAggregator networkAggregator,
    INetworkCollector networkCollector,
    NetworkTrafficRepository networkTrafficRepository,
    IMonitorSettingsProvider settings) : BackgroundService
{
    private const int PersistenceBatchSize = 500;
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private static readonly TimeSpan MinimumAggregationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumAggregationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);
    private DateTimeOffset _lastCycleFailureLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPersistenceFailureLogAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先追平一次，尽快把已经封口的流量桶写入数据库。
        await RunAggregationCycleSafeAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMilliseconds(settings.Current.NetworkProcessingIntervalMs);
            await WaitForIntervalOrSettingsChangeAsync(interval, stoppingToken);
            await RunAggregationCycleSafeAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // 先停周期循环，保证 peek/save/confirm 只有一个执行路径；再截断输入、排空并封口当前桶。
        await base.StopAsync(cancellationToken);
        await networkCollector.StopAsync(cancellationToken);
        if (ExecuteTask is { IsCompleted: false })
        {
            logger.LogWarning("Network aggregation cycle did not stop before the shutdown deadline; pending buckets remain buffered.");
            return;
        }

        await networkAggregator.CompleteAsync(cancellationToken);
        await PersistPendingBucketsAsync(cancellationToken);
    }

    private async Task WaitForIntervalOrSettingsChangeAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var settingsChangedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = settings.RegisterChangeCallback(_ => settingsChangedSource.TrySetResult());
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(interval, waitCancellation.Token);
        try
        {
            var completedTask = await Task.WhenAny(delayTask, settingsChangedSource.Task);
            await completedTask;
        }
        finally
        {
            waitCancellation.Cancel();
        }
    }

    private async Task PersistPendingBucketsAsync(CancellationToken cancellationToken)
    {
        await _persistenceGate.WaitAsync(cancellationToken);
        try
        {
            await PersistPendingBucketsCoreAsync(cancellationToken);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private async Task PersistPendingBucketsCoreAsync(CancellationToken cancellationToken)
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
                networkAggregator.RecordPersistenceSuccess();
                logger.LogDebug("Persisted {Count} network traffic buckets.", batch.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                networkAggregator.RecordPersistenceFailure();
                throw;
            }
            catch (SqliteException exception) when (SqliteBusyRetry.IsBusy(exception))
            {
                networkAggregator.RecordPersistenceFailure();
                LogPersistenceFailure(
                    LogLevel.Warning,
                    exception,
                    "SQLite remained busy after retries. Retaining {Count} network traffic buckets for the next cycle.",
                    batch.Count);
                return;
            }
            catch (Exception exception)
            {
                // 持久化错误只能延后写入，不能让后台服务退出。
                networkAggregator.RecordPersistenceFailure();
                LogPersistenceFailure(
                    LogLevel.Error,
                    exception,
                    "Network traffic persistence failed unexpectedly. Retaining {Count} network traffic buckets for the next cycle.",
                    batch.Count);
                return;
            }
        }
    }

    private async Task RunAggregationCycleSafeAsync(CancellationToken stoppingToken)
    {
        using (var flushCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            flushCancellation.CancelAfter(GetAggregationTimeout());
            try
            {
                await networkAggregator.FlushAsync(flushCancellation.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (flushCancellation.IsCancellationRequested)
            {
                LogCycleFailure(
                    LogLevel.Warning,
                    null,
                    "Network aggregation flush exceeded its timeout of {Timeout}; persisting completed buckets anyway.",
                    GetAggregationTimeout());
            }
            catch (Exception exception)
            {
                LogCycleFailure(
                    LogLevel.Error,
                    exception,
                    "Network aggregation flush failed; persisting completed buckets anyway.");
            }
        }

        // A stalled barrier must not prevent old pending buckets from freeing capacity.
        // Use a fresh deadline rather than the already-cancelled flush token.
        using var persistenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        persistenceCancellation.CancelAfter(GetAggregationTimeout());
        try
        {
            await PersistPendingBucketsAsync(persistenceCancellation.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (persistenceCancellation.IsCancellationRequested)
        {
            LogCycleFailure(LogLevel.Warning, null, "Network aggregation persistence timed out; pending buckets will be retried.");
        }
        catch (Exception exception)
        {
            LogCycleFailure(LogLevel.Error, exception, "Network aggregation persistence failed; pending buckets will be retried.");
        }
    }

    private TimeSpan GetAggregationTimeout()
    {
        var configuredInterval = TimeSpan.FromMilliseconds(settings.Current.NetworkProcessingIntervalMs);
        var scaledTimeout = TimeSpan.FromTicks(configuredInterval.Ticks * 5);
        return scaledTimeout < MinimumAggregationTimeout
            ? MinimumAggregationTimeout
            : scaledTimeout > MaximumAggregationTimeout
                ? MaximumAggregationTimeout
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
