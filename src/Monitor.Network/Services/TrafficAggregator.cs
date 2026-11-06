using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;
using Monitor.Network.Models;

namespace Monitor.Network.Services;

// ETW callbacks never wait for disk or the consumer. The queue and all live buckets
// have separate, fixed startup budgets; rejected traffic is reported explicitly.
public sealed class TrafficAggregator : INetworkAggregator, IDisposable
{
    private readonly object _syncRoot = new();
    private readonly object _inputGate = new();
    private readonly Dictionary<BucketKey, BucketAccumulator> _activeBuckets = new();
    private readonly PriorityQueue<BucketKey, long> _bucketStarts = new();
    private readonly Queue<TrafficBucket> _pendingBuckets = new();
    private readonly INetworkCollector _networkCollector;
    private readonly IAppRegistry _appRegistry;
    private readonly IAddressClassifier _addressClassifier;
    private readonly ILogger<TrafficAggregator> _logger;
    private readonly IDisposable _settingsRegistration;
    private readonly Channel<QueueItem> _eventChannel;
    private readonly Task _eventProcessingTask;
    private readonly int _eventQueueCapacity;
    private readonly int _bucketCapacity;
    private AggregationSettingsSnapshot _settingsSnapshot;
    private long _pendingEventCount;
    // Guarded by _inputGate. The channel reserves one extra slot for a shared barrier.
    private int _queuedEventCount;
    private TaskCompletionSource? _flushBarrier;
    private long _rejectedEvents;
    private long _rejectedBytes;
    private long _queueFullRejectedEvents;
    private long _bucketCapacityRejectedEvents;
    private long _firstRejectedAtTicks;
    private long _lastRejectedAtTicks;
    private long _processingFailures;
    private long _persistenceFailures;
    private long _lastPersistenceFailureTicks;
    private long _lastPersistenceSuccessTicks;
    private int _accepting = 1;
    private bool _disposed;

    public long PendingEventCount => Interlocked.Read(ref _pendingEventCount);

    public TrafficAggregator(
        INetworkCollector networkCollector,
        IAppRegistry appRegistry,
        IAddressClassifier addressClassifier,
        IMonitorSettingsProvider settingsMonitor,
        ILogger<TrafficAggregator> logger)
        : this(networkCollector, appRegistry, addressClassifier, settingsMonitor, logger, new TrafficAggregatorOptions())
    {
    }

    public TrafficAggregator(
        INetworkCollector networkCollector,
        IAppRegistry appRegistry,
        IAddressClassifier addressClassifier,
        IMonitorSettingsProvider settingsMonitor,
        ILogger<TrafficAggregator> logger,
        TrafficAggregatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.EventQueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.BucketCapacity);
        if (options.EventQueueCapacity == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Event queue capacity must leave room for a control barrier.");
        }
        _networkCollector = networkCollector;
        _appRegistry = appRegistry;
        _addressClassifier = addressClassifier;
        _logger = logger;
        _eventQueueCapacity = options.EventQueueCapacity;
        _bucketCapacity = options.BucketCapacity;
        _settingsSnapshot = AggregationSettingsSnapshot.From(settingsMonitor.Current);
        _settingsRegistration = settingsMonitor.RegisterChangeCallback(OnSettingsChanged);
        _eventChannel = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(_eventQueueCapacity + 1)
        {
            FullMode = BoundedChannelFullMode.Wait,
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
        Task barrierTask;
        while (true)
        {
            Task? existingBarrier;
            lock (_inputGate)
            {
                if (_accepting == 0)
                {
                    barrierTask = _eventProcessingTask;
                    break;
                }

                // A cancelled caller leaves its barrier in the queue. Later callers first
                // await that barrier, then insert their own after all preceding writes.
                existingBarrier = _flushBarrier?.Task;
                if (existingBarrier is null)
                {
                    var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!_eventChannel.Writer.TryWrite(new QueueItem(default, barrier)))
                    {
                        throw new InvalidOperationException("The reserved aggregation barrier slot is unavailable.");
                    }

                    _flushBarrier = barrier;
                    barrierTask = barrier.Task;
                    break;
                }
            }

            await WaitForBarrierAsync(existingBarrier, cancellationToken);
        }

        await WaitForBarrierAsync(barrierTask, cancellationToken);
    }

    private async Task WaitForBarrierAsync(Task barrierTask, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(barrierTask, _eventProcessingTask).WaitAsync(cancellationToken);
        if (completed == _eventProcessingTask)
        {
            await _eventProcessingTask;
            if (!barrierTask.IsCompleted)
            {
                throw new InvalidOperationException("The network event processor stopped before reaching the flush barrier.");
            }
        }

        await barrierTask;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        lock (_inputGate)
        {
            if (_accepting != 0)
            {
                Volatile.Write(ref _accepting, 0);
                _networkCollector.EventReceived -= OnEventReceived;
                _eventChannel.Writer.TryComplete();
            }
        }

        // If shutdown times out, a later CompleteAsync can resume waiting and sealing.
        await _eventProcessingTask.WaitAsync(cancellationToken);
        lock (_syncRoot)
        {
            RotateBucketsCore(DateTimeOffset.MaxValue, sealAll: true);
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
            return _pendingBuckets.Take(maxCount).ToArray();
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

    public TrafficAggregatorDiagnosticsSnapshot GetDiagnostics()
    {
        lock (_syncRoot)
        {
            return new TrafficAggregatorDiagnosticsSnapshot
            {
                EventQueueCapacity = _eventQueueCapacity,
                PendingEventCount = PendingEventCount,
                BucketCapacity = _bucketCapacity,
                ActiveBucketCount = _activeBuckets.Count,
                PendingBucketCount = _pendingBuckets.Count,
                RejectedEvents = Interlocked.Read(ref _rejectedEvents),
                RejectedBytes = Interlocked.Read(ref _rejectedBytes),
                QueueFullRejectedEvents = Interlocked.Read(ref _queueFullRejectedEvents),
                BucketCapacityRejectedEvents = Interlocked.Read(ref _bucketCapacityRejectedEvents),
                FirstRejectedAt = FromTicks(Interlocked.Read(ref _firstRejectedAtTicks)),
                LastRejectedAt = FromTicks(Interlocked.Read(ref _lastRejectedAtTicks)),
                ProcessingFailures = Interlocked.Read(ref _processingFailures),
                PersistenceFailures = Interlocked.Read(ref _persistenceFailures),
                LastPersistenceFailureAt = FromTicks(Interlocked.Read(ref _lastPersistenceFailureTicks)),
                LastPersistenceSuccessAt = FromTicks(Interlocked.Read(ref _lastPersistenceSuccessTicks)),
                IsAccepting = Volatile.Read(ref _accepting) != 0,
                IsProcessorRunning = !_eventProcessingTask.IsCompleted
            };
        }
    }

    public void RecordPersistenceFailure()
    {
        Interlocked.Increment(ref _persistenceFailures);
        Interlocked.Exchange(ref _lastPersistenceFailureTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordPersistenceSuccess()
    {
        Interlocked.Exchange(ref _lastPersistenceSuccessTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void Dispose()
    {
        lock (_inputGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Volatile.Write(ref _accepting, 0);
            _networkCollector.EventReceived -= OnEventReceived;
            _settingsRegistration.Dispose();
            _eventChannel.Writer.TryComplete();
        }

        try
        {
            _eventProcessingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed while waiting for network aggregation event processor to stop.");
        }
    }

    private void OnSettingsChanged(MonitorSettings settings) =>
        Volatile.Write(ref _settingsSnapshot, AggregationSettingsSnapshot.From(settings));

    private void OnEventReceived(NetworkTraceEvent traceEvent)
    {
        lock (_inputGate)
        {
            if (_accepting == 0)
            {
                return;
            }

            // The event producer never consumes the one slot reserved for a barrier.
            if (_queuedEventCount >= _eventQueueCapacity)
            {
                RecordRejection(traceEvent, bucketCapacity: false);
                return;
            }

            _queuedEventCount++;
            Interlocked.Increment(ref _pendingEventCount);
            if (!_eventChannel.Writer.TryWrite(new QueueItem(traceEvent, null)))
            {
                _queuedEventCount--;
                Interlocked.Decrement(ref _pendingEventCount);
                RecordRejection(traceEvent, bucketCapacity: false);
            }
        }
    }

    private async Task ProcessEventsAsync()
    {
        await foreach (var item in _eventChannel.Reader.ReadAllAsync())
        {
            if (item.Barrier is not null)
            {
                lock (_inputGate)
                {
                    if (ReferenceEquals(_flushBarrier, item.Barrier))
                    {
                        _flushBarrier = null;
                    }
                    item.Barrier.TrySetResult();
                }
                continue;
            }

            lock (_inputGate)
            {
                _queuedEventCount--;
            }

            try
            {
                ProcessTraceEvent(item.TraceEvent);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _processingFailures);
                _logger.LogDebug(exception, "Failed to aggregate network trace event.");
            }
            finally
            {
                Interlocked.Decrement(ref _pendingEventCount);
            }
        }
    }

    private void ProcessTraceEvent(NetworkTraceEvent traceEvent)
    {
        var timestamp = traceEvent.Timestamp == default ? DateTimeOffset.UtcNow : traceEvent.Timestamp;
        var appEntry = _appRegistry.GetOrAdd(traceEvent.ProcessId);
        var scopeType = _addressClassifier.Classify(traceEvent.RemoteAddress, traceEvent.LocalAddress);
        var settings = Volatile.Read(ref _settingsSnapshot);
        var bucketStartTime = AlignToBucketStart(timestamp, settings.AggregateIntervalSeconds);
        var key = new BucketKey(bucketStartTime, settings.AggregateIntervalSeconds, appEntry.AppKey, traceEvent.Direction, scopeType);

        lock (_syncRoot)
        {
            RotateBucketsCore(timestamp);
            if (!_activeBuckets.TryGetValue(key, out var accumulator))
            {
                if (_activeBuckets.Count + _pendingBuckets.Count >= _bucketCapacity)
                {
                    RecordRejection(traceEvent, bucketCapacity: true);
                    return;
                }

                accumulator = new BucketAccumulator(key);
                _activeBuckets.Add(key, accumulator);
                _bucketStarts.Enqueue(key, key.BucketStartTime.UtcTicks);
            }

            accumulator.Bytes += traceEvent.Bytes;
            accumulator.Packets++;
        }
    }

    private void RotateBucketsCore(DateTimeOffset referenceTime, bool sealAll = false)
    {
        var cutoff = sealAll ? long.MaxValue :
            AlignToBucketStart(referenceTime, Volatile.Read(ref _settingsSnapshot).AggregateIntervalSeconds).UtcTicks;
        // Only inspect windows whose start is before the current boundary. A late event
        // creates a new bucket, which is rotated on the next event or persistence read.
        while (_bucketStarts.TryPeek(out var key, out var start) && (sealAll || start < cutoff))
        {
            _bucketStarts.Dequeue();
            if (_activeBuckets.Remove(key, out var accumulator))
            {
                _pendingBuckets.Enqueue(accumulator.ToBucket());
            }
        }
    }

    private void RecordRejection(NetworkTraceEvent traceEvent, bool bucketCapacity)
    {
        Interlocked.Increment(ref _rejectedEvents);
        Interlocked.Add(ref _rejectedBytes, Math.Max(0, traceEvent.Bytes));
        if (bucketCapacity)
        {
            Interlocked.Increment(ref _bucketCapacityRejectedEvents);
        }
        else
        {
            Interlocked.Increment(ref _queueFullRejectedEvents);
        }

        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        Interlocked.CompareExchange(ref _firstRejectedAtTicks, ticks, 0);
        long previous;
        do
        {
            previous = Interlocked.Read(ref _lastRejectedAtTicks);
            if (ticks <= previous) break;
        }
        while (Interlocked.CompareExchange(ref _lastRejectedAtTicks, ticks, previous) != previous);
    }

    private static DateTimeOffset? FromTicks(long ticks) => ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);

    private static DateTimeOffset AlignToBucketStart(DateTimeOffset timestamp, int granularitySeconds)
    {
        var ticksPerBucket = TimeSpan.FromSeconds(granularitySeconds).Ticks;
        return new DateTimeOffset(timestamp.UtcTicks - timestamp.UtcTicks % ticksPerBucket, TimeSpan.Zero);
    }

    private sealed record AggregationSettingsSnapshot(int AggregateIntervalSeconds)
    {
        public static AggregationSettingsSnapshot From(MonitorSettings settings) => new(settings.AggregateIntervalSeconds);
    }

    private readonly record struct QueueItem(NetworkTraceEvent TraceEvent, TaskCompletionSource? Barrier);

    private sealed class BucketAccumulator(BucketKey key)
    {
        public long Bytes { get; set; }
        public long Packets { get; set; }

        public TrafficBucket ToBucket() => new()
        {
            BucketStartTime = key.BucketStartTime,
            BucketGranularitySeconds = key.BucketGranularitySeconds,
            AppKey = key.AppKey,
            Direction = key.Direction,
            ScopeType = key.ScopeType,
            Bytes = Bytes,
            Packets = Packets
        };
    }

    private readonly record struct BucketKey(
        DateTimeOffset BucketStartTime,
        int BucketGranularitySeconds,
        string AppKey,
        TrafficDirection Direction,
        AddressScopeType ScopeType);
}
