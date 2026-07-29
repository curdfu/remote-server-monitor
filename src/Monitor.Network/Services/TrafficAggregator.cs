using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;
using Monitor.Network.Models;

namespace Monitor.Network.Services;

// 网络聚合器是 ETW 原始事件进入业务层后的第一道缓冲和降噪边界。
// 它同时维护两条互不替代的数据路径：
// 1. 250ms 环形槽位：服务于首页/网络页的实时速率展示，允许快速覆盖旧槽位；
// 2. 聚合 bucket：按配置粒度、应用、方向和地址范围累计，用于后续批量写入 SQLite。
// 这里刻意不在 ETW 回调线程中做重计算，避免采集线程被进程解析、地址分类或落库阻塞。
public sealed class TrafficAggregator : INetworkAggregator, IDisposable
{
    private static readonly TimeSpan MinimumRealtimeRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RealtimeHistoryRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RealtimeSlotDuration = TimeSpan.FromMilliseconds(250);

    private readonly object _syncRoot = new();
    // 当前仍可写入的聚合桶。key 包含时间窗、应用、方向和 WAN/LAN/Loopback 范围。
    private readonly Dictionary<BucketKey, BucketAccumulator> _activeBuckets = new();
    // 已完成时间窗的聚合桶。Repository 成功保存后才会从这里确认出队。
    private readonly Queue<TrafficBucket> _pendingBuckets = new();
    // 复用既有实时刷新节奏，仅在内存保留最近两分钟的展示快照；不触发额外采集或数据库写入。
    private readonly Queue<NetworkRealtimeSnapshot> _realtimeHistory = new();
    private readonly INetworkCollector _networkCollector;
    private readonly IAppRegistry _appRegistry;
    private readonly IAddressClassifier _addressClassifier;
    private readonly ILogger<TrafficAggregator> _logger;
    private readonly IDisposable _settingsRegistration;
    private readonly Channel<QueuedTraceEvent> _eventChannel;
    private readonly Task _eventProcessingTask;

    // 固定时长槽位组成的环形数组，用 slotIndex 区分同一个数组位置对应的真实时间片。
    private RealtimeSlot[] _realtimeSlots = Array.Empty<RealtimeSlot>();
    private NetworkRealtimeSnapshot? _latestRealtimeSnapshot;
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

    public async Task<NetworkRealtimeSnapshot> GetRealtimeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 先等进入本方法前已经入队的事件处理完，再构建快照。
        // 这样页面刷新拿到的是队列已追平的视图，而不是 ETW 高峰期滞后的旧速率。
        await WaitForEventProcessingAsync(cancellationToken);

        NetworkRealtimeSnapshot snapshot;
        var now = DateTimeOffset.UtcNow;
        lock (_syncRoot)
        {
            RotateBucketsCore(now);
            EnsureRealtimeSlotsCapacityCore();
            var view = BuildRealtimeViewCore(now);
            snapshot = UpdateLatestRealtimeCacheCore(now, view);
        }

        return snapshot;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 停止或落库前等待队列清空，保证已收到的 ETW 事件都进入聚合状态。
        // 注意这里只等待内存聚合完成，不负责把 pending bucket 写入数据库。
        await WaitForEventProcessingAsync(cancellationToken);
    }

    public NetworkRealtimeSnapshot? GetLatestRealtimeSnapshot()
    {
        lock (_syncRoot)
        {
            return _latestRealtimeSnapshot;
        }
    }

    public IReadOnlyList<NetworkRealtimeSnapshot> GetRecentRealtimeSnapshots()
    {
        lock (_syncRoot)
        {
            PruneRealtimeHistoryCore(DateTimeOffset.UtcNow);
            return _realtimeHistory.ToArray();
        }
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
        // 因此快照对象只保留聚合所需字段，避免在事件处理路径读取可变配置对象。
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
        // 后台消费者是聚合状态的唯一写入者；单线程顺序处理能让实时槽位和 bucket 累计保持一致。
        // 单个事件处理失败时只记录 Debug 日志并继续消费，避免一个异常中断整条实时链路。
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
        // 每个 ETW 事件会同时进入实时槽位和持久化 bucket。
        // appKey 由进程信息解析得到，scopeType 由远端/本地地址共同判定。
        var timestamp = traceEvent.Timestamp == default ? DateTimeOffset.UtcNow : traceEvent.Timestamp;
        var appEntry = _appRegistry.GetOrAdd(traceEvent.ProcessId);
        var scopeType = _addressClassifier.Classify(traceEvent.RemoteAddress, traceEvent.LocalAddress);

        lock (_syncRoot)
        {
            RotateBucketsCore(timestamp);
            EnsureRealtimeSlotsCapacityCore();
            AddToRealtimeSlotsCore(timestamp, traceEvent.Direction, scopeType, traceEvent.Bytes);
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

    private void AddToRealtimeSlotsCore(
        DateTimeOffset timestamp,
        TrafficDirection direction,
        AddressScopeType scopeType,
        long bytes)
    {
        if (_realtimeSlots.Length == 0)
        {
            return;
        }

        var slotIndex = GetRealtimeSlotIndex(timestamp);
        var slot = _realtimeSlots[(int)(slotIndex % _realtimeSlots.Length)];
        // 环形槽位复用时必须按 slotIndex 清空。
        // 数组下标会循环复用，但 slotIndex 单调递增，用它判断该位置是否仍属于当前时间片。
        if (slot.SlotIndex != slotIndex)
        {
            slot.Reset(slotIndex);
        }

        slot.Add(direction, scopeType, bytes);
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

    private RealtimeView BuildRealtimeViewCore(DateTimeOffset referenceTime)
    {
        var settings = Volatile.Read(ref _settingsSnapshot);
        var intervalSeconds = Math.Max(settings.NetworkRealtimeIntervalMs / 1000d, 0.1d);
        if (_realtimeSlots.Length == 0)
        {
            return new RealtimeView(0, 0, 0, 0, 0, 0);
        }

        var currentSlotIndex = GetRealtimeSlotIndex(referenceTime);
        var windowSlotCount = Math.Max(1, (int)Math.Ceiling(intervalSeconds / RealtimeSlotDuration.TotalSeconds));
        var minimumSlotIndex = currentSlotIndex - windowSlotCount + 1;

        // 只统计实时窗口内的槽位。
        // 过旧槽位即使还在环形数组里也不能参与速率计算，否则空闲后会残留历史流量。
        long totalUploadBytes = 0;
        long totalDownloadBytes = 0;
        long wanUploadBytes = 0;
        long wanDownloadBytes = 0;
        long lanUploadBytes = 0;
        long lanDownloadBytes = 0;

        foreach (var slot in _realtimeSlots)
        {
            if (!slot.IsValid || slot.SlotIndex < minimumSlotIndex || slot.SlotIndex > currentSlotIndex)
            {
                continue;
            }

            totalUploadBytes += slot.TotalUploadBytes;
            totalDownloadBytes += slot.TotalDownloadBytes;
            wanUploadBytes += slot.WanUploadBytes;
            wanDownloadBytes += slot.WanDownloadBytes;
            lanUploadBytes += slot.LanUploadBytes;
            lanDownloadBytes += slot.LanDownloadBytes;
        }

        return new RealtimeView(
            totalUploadBytes / intervalSeconds,
            totalDownloadBytes / intervalSeconds,
            wanUploadBytes / intervalSeconds,
            wanDownloadBytes / intervalSeconds,
            lanUploadBytes / intervalSeconds,
            lanDownloadBytes / intervalSeconds);
    }

    private NetworkRealtimeSnapshot UpdateLatestRealtimeCacheCore(DateTimeOffset sampleTime, RealtimeView view)
    {
        var snapshot = new NetworkRealtimeSnapshot
        {
            SampleTime = sampleTime,
            TotalUploadBytesPerSecond = view.TotalUploadBytesPerSecond,
            TotalDownloadBytesPerSecond = view.TotalDownloadBytesPerSecond,
            WanUploadBytesPerSecond = view.WanUploadBytesPerSecond,
            WanDownloadBytesPerSecond = view.WanDownloadBytesPerSecond,
            LanUploadBytesPerSecond = view.LanUploadBytesPerSecond,
            LanDownloadBytesPerSecond = view.LanDownloadBytesPerSecond
        };

        _latestRealtimeSnapshot = snapshot;
        _realtimeHistory.Enqueue(snapshot);
        PruneRealtimeHistoryCore(sampleTime);

        return snapshot;
    }

    private void PruneRealtimeHistoryCore(DateTimeOffset referenceTime)
    {
        var cutoff = referenceTime.Subtract(RealtimeHistoryRetention);
        while (_realtimeHistory.TryPeek(out var oldest) && oldest.SampleTime < cutoff)
        {
            _realtimeHistory.Dequeue();
        }
    }

    private void EnsureRealtimeSlotsCapacityCore()
    {
        // 实时槽位容量随配置调整，至少覆盖实时窗口、两个聚合周期和最小保留窗口。
        // 扩容/缩容时按 slotIndex 迁移最新槽位，避免数组长度变化导致实时速率突然清零。
        var requiredSlotCount = Math.Max(
            2,
            (int)Math.Ceiling(GetRealtimeRetentionWindow().TotalMilliseconds / RealtimeSlotDuration.TotalMilliseconds) + 2);

        if (_realtimeSlots.Length == requiredSlotCount)
        {
            return;
        }

        var resizedSlots = new RealtimeSlot[requiredSlotCount];
        for (var index = 0; index < resizedSlots.Length; index++)
        {
            resizedSlots[index] = new RealtimeSlot();
        }

        foreach (var slot in _realtimeSlots)
        {
            if (!slot.IsValid)
            {
                continue;
            }

            var target = resizedSlots[(int)(slot.SlotIndex % resizedSlots.Length)];
            if (!target.IsValid || target.SlotIndex < slot.SlotIndex)
            {
                target.CopyFrom(slot);
            }
        }

        _realtimeSlots = resizedSlots;
    }

    private TimeSpan GetRealtimeRetentionWindow()
    {
        // 保留窗口不能只看前端刷新间隔。聚合粒度变大时，需要更长的槽位历史来平滑跨 bucket 的实时读数。
        var settings = Volatile.Read(ref _settingsSnapshot);
        var realtimeWindow = TimeSpan.FromMilliseconds(settings.NetworkRealtimeIntervalMs);
        var aggregateWindow = TimeSpan.FromSeconds(settings.AggregateIntervalSeconds * 2d);
        return new[]
        {
            realtimeWindow,
            aggregateWindow,
            MinimumRealtimeRetention
        }.Max();
    }

    private static long GetRealtimeSlotIndex(DateTimeOffset timestamp)
    {
        return timestamp.UtcTicks / RealtimeSlotDuration.Ticks;
    }

    private static DateTimeOffset AlignToBucketStart(DateTimeOffset timestamp, int granularitySeconds)
    {
        var ticksPerBucket = TimeSpan.FromSeconds(granularitySeconds).Ticks;
        var alignedTicks = timestamp.UtcTicks - (timestamp.UtcTicks % ticksPerBucket);
        return new DateTimeOffset(alignedTicks, TimeSpan.Zero);
    }

    private sealed record AggregationSettingsSnapshot(
        int AggregateIntervalSeconds,
        int NetworkRealtimeIntervalMs)
    {
        public static AggregationSettingsSnapshot From(MonitorSettings settings)
        {
            return new AggregationSettingsSnapshot(
                settings.AggregateIntervalSeconds,
                settings.NetworkRealtimeIntervalMs);
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

    private sealed class RealtimeSlot
    {
        // RealtimeSlot 只保存实时速率需要的最小累计值，不保存 appKey。
        // 应用维度的历史统计走 bucket/SQLite，避免实时环形数组随进程数量膨胀。
        public long SlotIndex { get; private set; } = -1;
        public bool IsValid => SlotIndex >= 0;
        public long TotalUploadBytes { get; private set; }
        public long TotalDownloadBytes { get; private set; }
        public long WanUploadBytes { get; private set; }
        public long WanDownloadBytes { get; private set; }
        public long LanUploadBytes { get; private set; }
        public long LanDownloadBytes { get; private set; }

        public void Reset(long slotIndex)
        {
            SlotIndex = slotIndex;
            TotalUploadBytes = 0;
            TotalDownloadBytes = 0;
            WanUploadBytes = 0;
            WanDownloadBytes = 0;
            LanUploadBytes = 0;
            LanDownloadBytes = 0;
        }

        public void CopyFrom(RealtimeSlot other)
        {
            Reset(other.SlotIndex);
            TotalUploadBytes = other.TotalUploadBytes;
            TotalDownloadBytes = other.TotalDownloadBytes;
            WanUploadBytes = other.WanUploadBytes;
            WanDownloadBytes = other.WanDownloadBytes;
            LanUploadBytes = other.LanUploadBytes;
            LanDownloadBytes = other.LanDownloadBytes;
        }

        public void Add(
            TrafficDirection direction,
            AddressScopeType scopeType,
            long bytes)
        {
            if (direction == TrafficDirection.Outbound)
            {
                TotalUploadBytes += bytes;

                if (scopeType == AddressScopeType.Wan)
                {
                    WanUploadBytes += bytes;
                }
                else if (scopeType == AddressScopeType.Lan)
                {
                    LanUploadBytes += bytes;
                }
            }
            else
            {
                TotalDownloadBytes += bytes;

                if (scopeType == AddressScopeType.Wan)
                {
                    WanDownloadBytes += bytes;
                }
                else if (scopeType == AddressScopeType.Lan)
                {
                    LanDownloadBytes += bytes;
                }
            }
        }
    }

    private readonly record struct BucketKey(
        DateTimeOffset BucketStartTime,
        int BucketGranularitySeconds,
        string AppKey,
        TrafficDirection Direction,
        AddressScopeType ScopeType);

    private sealed record RealtimeView(
        double TotalUploadBytesPerSecond,
        double TotalDownloadBytesPerSecond,
        double WanUploadBytesPerSecond,
        double WanDownloadBytesPerSecond,
        double LanUploadBytesPerSecond,
        double LanDownloadBytesPerSecond);
}
