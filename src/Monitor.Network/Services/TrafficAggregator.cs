using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;
using Monitor.Network.Models;

namespace Monitor.Network.Services;

// 网络聚合器是 ETW 原始事件进入业务层后的第一道缓冲和降噪边界。
// 它按配置粒度、应用、方向和地址范围累计流量 bucket，供后续批量写入 SQLite。
// 这里刻意不在 ETW 回调线程中做重计算，避免采集线程被进程解析、地址分类或落库阻塞。
public sealed class TrafficAggregator : INetworkAggregator, IDisposable
{
    private readonly object _syncRoot = new();
    // 当前仍可写入的聚合桶。key 包含时间窗、应用、方向和 WAN/LAN/Loopback 范围。
    private readonly Dictionary<BucketKey, BucketAccumulator> _activeBuckets = new();
    // 已完成时间窗的聚合桶。Repository 成功保存后才会从这里确认出队。
    private readonly Queue<TrafficBucket> _pendingBuckets = new();
    private readonly INetworkCollector _networkCollector;
    private readonly IAppRegistry _appRegistry;
    private readonly IAddressClassifier _addressClassifier;
    private readonly ILogger<TrafficAggregator> _logger;
    private readonly IDisposable _settingsRegistration;
    private readonly Channel<QueuedTraceEvent> _eventChannel;
    private readonly Task _eventProcessingTask;

    private AggregationSettingsSnapshot _settingsSnapshot;
    // 入队/处理序号用于在读取快照或停止服务时等待后台消费者追上生产者。
    private long _enqueuedSequence;
    private long _processedSequence;
    private bool _disposed;

    public long PendingEventCount => Math.Max(
        0,
        Interlocked.Read(ref _enqueuedSequence) - Interlocked.Read(ref _processedSequence));

    public TrafficAggregator(
        INetworkCollector networkCollector,
        IAppRegistry appRegistry,
        IAddressClassifier addressClassifier,
        IMonitorSettingsProvider settingsMonitor,
        ILogger<TrafficAggregator> logger)
    {
        _networkCollector = networkCollector;
        _appRegistry = appRegistry;
        _addressClassifier = addressClassifier;
        _logger = logger;
        _settingsSnapshot = AggregationSettingsSnapshot.From(settingsMonitor.Current);
        _settingsRegistration = settingsMonitor.RegisterChangeCallback(OnSettingsChanged);
        _eventChannel = Channel.CreateUnbounded<QueuedTraceEvent>(new UnboundedChannelOptions
        {
            // ETW 回调可能来自多个线程；Channel 允许多生产者写入。
            // 聚合状态只由后台单消费者修改，减少锁持有时间和跨线程状态竞争。
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _eventProcessingTask = Task.Run(ProcessEventsAsync);

        _networkCollector.EventReceived += OnEventReceived;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 停止或落库前等待队列清空，保证已收到的 ETW 事件都进入聚合状态。
        // 注意这里只等待内存聚合完成，不负责把 pending bucket 写入数据库。
        await WaitForEventProcessingAsync(cancellationToken);
    }

    public IReadOnlyList<TrafficBucket> PeekPendingBuckets(int maxCount)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<TrafficBucket>();
        }

        lock (_syncRoot)
        {
            // 读取待落库 bucket 前先旋转已完成时间窗。
            // 当前时间窗仍可能继续写入，所以只暴露已经封口的旧 bucket 给持久化层。
            RotateBucketsCore(DateTimeOffset.UtcNow);

            if (_pendingBuckets.Count == 0)
            {
                return Array.Empty<TrafficBucket>();
            }

            var count = Math.Min(maxCount, _pendingBuckets.Count);
            var result = new List<TrafficBucket>(count);

            foreach (var bucket in _pendingBuckets)
            {
                result.Add(bucket);
                if (result.Count == count)
                {
                    break;
                }
            }

            return result;
        }
    }

    public void ConfirmPendingBuckets(int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            // SaveAsync 成功后才确认出队。
            // 如果数据库写入失败，调用方不会执行 Confirm，bucket 会留在队列中等待下一轮重试，避免丢流量。
            var confirmedCount = Math.Min(count, _pendingBuckets.Count);
            for (var index = 0; index < confirmedCount; index++)
            {
                _pendingBuckets.Dequeue();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _networkCollector.EventReceived -= OnEventReceived;
        _settingsRegistration.Dispose();
        _eventChannel.Writer.TryComplete();

        try
        {
            _eventProcessingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed while waiting for network aggregation event processor to stop.");
        }
    }

    private void OnSettingsChanged(MonitorSettings settings)
    {
        // 设置热更新只影响后续 bucket 的粒度；已经形成的 active/pending bucket 保持原粒度。
        // 因此聚合设置快照只保留 bucket 所需字段，避免在事件处理路径读取可变配置对象。
        Volatile.Write(ref _settingsSnapshot, AggregationSettingsSnapshot.From(settings));
    }

    private void OnEventReceived(NetworkTraceEvent traceEvent)
    {
        if (_disposed)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _enqueuedSequence);
        // ETW 事件回调保持轻量，只分配序号并入队。
        // 进程解析、地址分类和聚合都放到后台单线程处理，避免采集线程被业务逻辑拖慢。
        if (!_eventChannel.Writer.TryWrite(new QueuedTraceEvent(sequence, traceEvent)))
        {
            Interlocked.Decrement(ref _enqueuedSequence);
            _logger.LogDebug("Dropped network trace event because the aggregation queue is no longer accepting items.");
        }
    }

    private async Task ProcessEventsAsync()
    {
        // 后台消费者是聚合状态的唯一写入者；单线程顺序处理能让 bucket 累计保持一致。
        // 单个事件处理失败时只记录 Debug 日志并继续消费，避免一个异常中断整条聚合链路。
        try
        {
            await foreach (var queuedTraceEvent in _eventChannel.Reader.ReadAllAsync())
            {
                try
                {
                    ProcessTraceEvent(queuedTraceEvent.TraceEvent);
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Failed to aggregate network trace event.");
                }
                finally
                {
                    Interlocked.Exchange(ref _processedSequence, queuedTraceEvent.Sequence);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Network aggregation event processor stopped with exception.");
        }
    }

    private void ProcessTraceEvent(NetworkTraceEvent traceEvent)
    {
        // appKey 由进程信息解析得到，scopeType 由远端/本地地址共同判定。
        var timestamp = traceEvent.Timestamp == default ? DateTimeOffset.UtcNow : traceEvent.Timestamp;
        var appEntry = _appRegistry.GetOrAdd(traceEvent.ProcessId);
        var scopeType = _addressClassifier.Classify(traceEvent.RemoteAddress, traceEvent.LocalAddress);

        lock (_syncRoot)
        {
            RotateBucketsCore(timestamp);
            AddToBucketCore(timestamp, appEntry.AppKey, traceEvent.Direction, scopeType, traceEvent.Bytes);
        }
    }

    private async Task WaitForEventProcessingAsync(CancellationToken cancellationToken)
    {
        var targetSequence = Interlocked.Read(ref _enqueuedSequence);
        await WaitForEventProcessingAsync(targetSequence, cancellationToken);
    }

    private async Task WaitForEventProcessingAsync(long targetSequence, CancellationToken cancellationToken)
    {
        if (targetSequence <= 0)
        {
            return;
        }

        var spinner = new SpinWait();
        // 使用序号作为追赶边界：调用方只需等待自己进入前已经排队的事件完成。
        // 前几轮自旋用于吸收短暂队列延迟，之后退让到 1ms delay，避免忙等占满 CPU。
        while (Interlocked.Read(ref _processedSequence) < targetSequence)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (spinner.Count < 10)
            {
                spinner.SpinOnce();
                continue;
            }

            await Task.Delay(1, cancellationToken);
        }
    }

    private void AddToBucketCore(
        DateTimeOffset timestamp,
        string appKey,
        TrafficDirection direction,
        AddressScopeType scopeType,
        long bytes)
    {
        // bucket 维度必须和后续 SQL 聚合维度一致：时间窗 + 应用 + 方向 + 地址范围。
        // 这里不做 TopN 或页面筛选，保留足够细的事实数据给查询层组合。
        var settings = Volatile.Read(ref _settingsSnapshot);
        var bucketStartTime = AlignToBucketStart(timestamp, settings.AggregateIntervalSeconds);
        var key = new BucketKey(bucketStartTime, settings.AggregateIntervalSeconds, appKey, direction, scopeType);

        if (!_activeBuckets.TryGetValue(key, out var accumulator))
        {
            accumulator = new BucketAccumulator(key);
            _activeBuckets[key] = accumulator;
        }

        accumulator.Bytes += bytes;
        accumulator.Packets += 1;
    }

    private void RotateBucketsCore(DateTimeOffset referenceTime)
    {
        if (_activeBuckets.Count == 0)
        {
            return;
        }

        var settings = Volatile.Read(ref _settingsSnapshot);
        var currentBucketStart = AlignToBucketStart(referenceTime, settings.AggregateIntervalSeconds);
        var completedKeys = new List<BucketKey>();

        // 当前 bucket 还可能继续写入，只把早于当前时间窗的 bucket 推入持久化队列。
        // 旋转动作发生在事件写入和持久化读取两条路径上，保证低流量场景也能及时封口旧时间窗。
        foreach (var pair in _activeBuckets)
        {
            if (pair.Key.BucketStartTime < currentBucketStart)
            {
                completedKeys.Add(pair.Key);
            }
        }

        foreach (var key in completedKeys)
        {
            if (!_activeBuckets.Remove(key, out var accumulator))
            {
                continue;
            }

            _pendingBuckets.Enqueue(accumulator.ToBucket());
        }
    }

    private static DateTimeOffset AlignToBucketStart(DateTimeOffset timestamp, int granularitySeconds)
    {
        var ticksPerBucket = TimeSpan.FromSeconds(granularitySeconds).Ticks;
        var alignedTicks = timestamp.UtcTicks - (timestamp.UtcTicks % ticksPerBucket);
        return new DateTimeOffset(alignedTicks, TimeSpan.Zero);
    }

    private sealed record AggregationSettingsSnapshot(int AggregateIntervalSeconds)
    {
        public static AggregationSettingsSnapshot From(MonitorSettings settings)
        {
            return new AggregationSettingsSnapshot(settings.AggregateIntervalSeconds);
        }
    }

    private readonly record struct QueuedTraceEvent(long Sequence, NetworkTraceEvent TraceEvent);

    private sealed class BucketAccumulator(BucketKey key)
    {
        public BucketKey Key { get; } = key;
        public long Bytes { get; set; }
        public long Packets { get; set; }

        public TrafficBucket ToBucket()
        {
            return new TrafficBucket
            {
                BucketStartTime = Key.BucketStartTime,
                BucketGranularitySeconds = Key.BucketGranularitySeconds,
                AppKey = Key.AppKey,
                Direction = Key.Direction,
                ScopeType = Key.ScopeType,
                Bytes = Bytes,
                Packets = Packets
            };
        }
    }

    private readonly record struct BucketKey(
        DateTimeOffset BucketStartTime,
        int BucketGranularitySeconds,
        string AppKey,
        TrafficDirection Direction,
        AddressScopeType ScopeType);

}
