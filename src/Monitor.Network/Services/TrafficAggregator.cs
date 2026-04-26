using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;
using Monitor.Network.Models;

namespace Monitor.Network.Services;

public sealed class TrafficAggregator : INetworkAggregator, IDisposable
{
    private static readonly TimeSpan MinimumRealtimeRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RealtimeSlotDuration = TimeSpan.FromMilliseconds(250);

    private readonly object _syncRoot = new();
    private readonly Dictionary<BucketKey, BucketAccumulator> _activeBuckets = new();
    private readonly Queue<TrafficBucket> _pendingBuckets = new();
    private readonly INetworkCollector _networkCollector;
    private readonly IAppRegistry _appRegistry;
    private readonly IAddressClassifier _addressClassifier;
    private readonly ILogger<TrafficAggregator> _logger;
    private readonly IDisposable _settingsRegistration;
    private readonly Channel<QueuedTraceEvent> _eventChannel;
    private readonly Task _eventProcessingTask;

    private RealtimeSlot[] _realtimeSlots = Array.Empty<RealtimeSlot>();
    private NetworkRealtimeSnapshot? _latestRealtimeSnapshot;
    private AggregationSettingsSnapshot _settingsSnapshot;
    private long _enqueuedSequence;
    private long _processedSequence;
    private bool _disposed;

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
        await WaitForEventProcessingAsync(cancellationToken);
    }

    public NetworkRealtimeSnapshot? GetLatestRealtimeSnapshot()
    {
        lock (_syncRoot)
        {
            return _latestRealtimeSnapshot;
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
        Volatile.Write(ref _settingsSnapshot, AggregationSettingsSnapshot.From(settings));
    }

    private void OnEventReceived(NetworkTraceEvent traceEvent)
    {
        if (_disposed)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _enqueuedSequence);
        if (!_eventChannel.Writer.TryWrite(new QueuedTraceEvent(sequence, traceEvent)))
        {
            Interlocked.Decrement(ref _enqueuedSequence);
            _logger.LogDebug("Dropped network trace event because the aggregation queue is no longer accepting items.");
        }
    }

    private async Task ProcessEventsAsync()
    {
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
        var intervalSeconds = Math.Max(MonitorSettings.NetworkRealtimeIntervalMs / 1000d, 0.1d);
        if (_realtimeSlots.Length == 0)
        {
            return new RealtimeView(0, 0, 0, 0, 0, 0);
        }

        var currentSlotIndex = GetRealtimeSlotIndex(referenceTime);
        var windowSlotCount = Math.Max(1, (int)Math.Ceiling(intervalSeconds / RealtimeSlotDuration.TotalSeconds));
        var minimumSlotIndex = currentSlotIndex - windowSlotCount + 1;

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

        return snapshot;
    }

    private void EnsureRealtimeSlotsCapacityCore()
    {
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
        var settings = Volatile.Read(ref _settingsSnapshot);
        var realtimeWindow = TimeSpan.FromMilliseconds(MonitorSettings.NetworkRealtimeIntervalMs);
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

    private sealed record AggregationSettingsSnapshot(int AggregateIntervalSeconds)
    {
        public static AggregationSettingsSnapshot From(MonitorSettings settings)
        {
            return new AggregationSettingsSnapshot(settings.AggregateIntervalSeconds);
        }
    }

    private sealed record QueuedTraceEvent(long Sequence, NetworkTraceEvent TraceEvent);

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

    private sealed record BucketKey(
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
