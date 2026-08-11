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
                // 持久化错误只能延后写入，不能让后台服务退出。
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
        using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cycleCancellation.CancelAfter(GetAggregationTimeout());

        try
        {
            await FlushAggregationAsync(cycleCancellation.Token);
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
                "Network aggregation cycle exceeded its timeout of {Timeout}. The next cycle will retry.",
                GetAggregationTimeout());
        }
        catch (Exception exception)
        {
            LogCycleFailure(
                LogLevel.Error,
                exception,
                "Network aggregation cycle failed. The next cycle will retry.");
        }
    }

    private async Task FlushAggregationAsync(CancellationToken cancellationToken)
    {
        // 先等待调用前已入队事件处理完，再旋转并持久化已经封口的 bucket。
        await networkAggregator.FlushAsync(cancellationToken);
        await PersistPendingBucketsAsync(cancellationToken);
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
