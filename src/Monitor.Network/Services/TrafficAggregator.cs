using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;
using Monitor.Network.Models;

namespace Monitor.Network.Services;

public sealed class TrafficAggregator : INetworkAggregator
{
    private static readonly TimeSpan MinimumRealtimeRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RealtimeSlotDuration = TimeSpan.FromMilliseconds(250);

    private readonly object _syncRoot = new();
    private readonly Dictionary<BucketKey, BucketAccumulator> _activeBuckets = new();
    private readonly Queue<TrafficBucket> _pendingBuckets = new();
    private RealtimeSlot[] _realtimeSlots = Array.Empty<RealtimeSlot>();
    private NetworkRealtimeSnapshot? _latestRealtimeSnapshot;
    private IReadOnlyList<AppTrafficUsage> _latestTopApps = Array.Empty<AppTrafficUsage>();

    private readonly INetworkCollector _networkCollector;
    private readonly IAppRegistry _appRegistry;
    private readonly IAddressClassifier _addressClassifier;
    private readonly IOptionsMonitor<MonitorSettings> _settingsMonitor;
    private readonly ILogger<TrafficAggregator> _logger;

    public TrafficAggregator(
        INetworkCollector networkCollector,
        IAppRegistry appRegistry,
        IAddressClassifier addressClassifier,
        IOptionsMonitor<MonitorSettings> settingsMonitor,
        ILogger<TrafficAggregator> logger)
    {
        _networkCollector = networkCollector;
        _appRegistry = appRegistry;
        _addressClassifier = addressClassifier;
        _settingsMonitor = settingsMonitor;
        _logger = logger;

        _networkCollector.EventReceived += OnEventReceived;
    }

    public Task<NetworkRealtimeSnapshot> GetRealtimeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        NetworkRealtimeSnapshot snapshot;
        var now = DateTimeOffset.UtcNow;
        lock (_syncRoot)
        {
            RotateBucketsCore(now);
            EnsureRealtimeSlotsCapacityCore();
            var view = BuildRealtimeViewCore(now);
            snapshot = UpdateLatestRealtimeCacheCore(now, view);
        }

        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<AppTrafficUsage>> GetTopAppsAsync(int topN, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (topN <= 0)
        {
            return Task.FromResult<IReadOnlyList<AppTrafficUsage>>(Array.Empty<AppTrafficUsage>());
        }

        RealtimeView view;
        var now = DateTimeOffset.UtcNow;
        lock (_syncRoot)
        {
            RotateBucketsCore(now);
            EnsureRealtimeSlotsCapacityCore();
            view = BuildRealtimeViewCore(now);
        }

        return Task.FromResult<IReadOnlyList<AppTrafficUsage>>(SortAndTakeTopApps(view.AppUsages, topN));
    }

    public NetworkRealtimeSnapshot? GetLatestRealtimeSnapshot()
    {
        lock (_syncRoot)
        {
            return _latestRealtimeSnapshot;
        }
    }

    public IReadOnlyList<AppTrafficUsage> GetLatestTopApps(int topN)
    {
        if (topN <= 0)
        {
            return Array.Empty<AppTrafficUsage>();
        }

        lock (_syncRoot)
        {
            return _latestTopApps.Take(topN).ToArray();
        }
    }

    public IReadOnlyList<TrafficBucket> DequeuePendingBuckets(int maxCount)
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

            for (var index = 0; index < count; index++)
            {
                result.Add(_pendingBuckets.Dequeue());
            }

            return result;
        }
    }

    private void OnEventReceived(NetworkTraceEvent traceEvent)
    {
        try
        {
            var timestamp = traceEvent.Timestamp == default ? DateTimeOffset.UtcNow : traceEvent.Timestamp;
            var appEntry = _appRegistry.GetOrAdd(traceEvent.ProcessId);
            var scopeType = _addressClassifier.Classify(traceEvent.RemoteAddress, traceEvent.LocalAddress);

            lock (_syncRoot)
            {
                RotateBucketsCore(timestamp);
                EnsureRealtimeSlotsCapacityCore();
                AddToRealtimeSlotsCore(timestamp, appEntry, traceEvent.Direction, scopeType, traceEvent.Bytes);
                AddToBucketCore(timestamp, appEntry.AppKey, traceEvent.Direction, scopeType, traceEvent.Bytes);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to aggregate network trace event.");
        }
    }

    private void AddToRealtimeSlotsCore(
        DateTimeOffset timestamp,
        AppRegistryEntry appEntry,
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

        slot.Add(appEntry.AppKey, appEntry.ProcessName, appEntry.DisplayName, direction, scopeType, bytes);
    }

    private void AddToBucketCore(
        DateTimeOffset timestamp,
        string appKey,
        TrafficDirection direction,
        AddressScopeType scopeType,
        long bytes)
    {
        var bucketGranularitySeconds = _settingsMonitor.CurrentValue.AggregateIntervalSeconds;
        var bucketStartTime = AlignToBucketStart(timestamp, bucketGranularitySeconds);
        var key = new BucketKey(bucketStartTime, bucketGranularitySeconds, appKey, direction, scopeType);

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

        var settings = _settingsMonitor.CurrentValue;
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
        var intervalSeconds = Math.Max(_settingsMonitor.CurrentValue.NetworkSampleIntervalMs / 1000d, 0.1d);
        if (_realtimeSlots.Length == 0)
        {
            return new RealtimeView(0, 0, 0, 0, 0, 0, Array.Empty<AppTrafficUsage>());
        }

        var currentSlotIndex = GetRealtimeSlotIndex(referenceTime);
        var windowSlotCount = Math.Max(1, (int)Math.Ceiling(intervalSeconds / RealtimeSlotDuration.TotalSeconds));
        var minimumSlotIndex = currentSlotIndex - windowSlotCount + 1;
        var appUsageMap = new Dictionary<string, AppUsageAccumulator>(StringComparer.OrdinalIgnoreCase);

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

            foreach (var pair in slot.AppUsages)
            {
                if (!appUsageMap.TryGetValue(pair.Key, out var accumulator))
                {
                    accumulator = new AppUsageAccumulator(pair.Key, pair.Value.ProcessName, pair.Value.DisplayName);
                    appUsageMap[pair.Key] = accumulator;
                }

                accumulator.UploadBytes += pair.Value.UploadBytes;
                accumulator.DownloadBytes += pair.Value.DownloadBytes;
                accumulator.WanUploadBytes += pair.Value.WanUploadBytes;
                accumulator.WanDownloadBytes += pair.Value.WanDownloadBytes;
                accumulator.LanUploadBytes += pair.Value.LanUploadBytes;
                accumulator.LanDownloadBytes += pair.Value.LanDownloadBytes;
            }
        }

        var appUsages = new List<AppTrafficUsage>(appUsageMap.Count);
        foreach (var pair in appUsageMap)
        {
            if (pair.Value.UploadBytes <= 0 && pair.Value.DownloadBytes <= 0)
            {
                continue;
            }

            appUsages.Add(pair.Value.ToModel(intervalSeconds));
        }

        return new RealtimeView(
            totalUploadBytes / intervalSeconds,
            totalDownloadBytes / intervalSeconds,
            wanUploadBytes / intervalSeconds,
            wanDownloadBytes / intervalSeconds,
            lanUploadBytes / intervalSeconds,
            lanDownloadBytes / intervalSeconds,
            appUsages.ToArray());
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
            LanDownloadBytesPerSecond = view.LanDownloadBytesPerSecond,
            AppUsages = view.AppUsages
        };

        _latestRealtimeSnapshot = snapshot;
        _latestTopApps = SortAndTakeTopApps(view.AppUsages, null);

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
        var settings = _settingsMonitor.CurrentValue;
        var realtimeWindow = TimeSpan.FromMilliseconds(settings.NetworkSampleIntervalMs);
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

    private static IReadOnlyList<AppTrafficUsage> SortAndTakeTopApps(IReadOnlyList<AppTrafficUsage> appUsages, int? topN)
    {
        if (appUsages.Count == 0)
        {
            return Array.Empty<AppTrafficUsage>();
        }

        var items = appUsages.ToArray();
        Array.Sort(items, static (left, right) =>
        {
            var leftTotal = left.UploadBytesPerSecond + left.DownloadBytesPerSecond;
            var rightTotal = right.UploadBytesPerSecond + right.DownloadBytesPerSecond;
            var totalComparison = rightTotal.CompareTo(leftTotal);
            if (totalComparison != 0)
            {
                return totalComparison;
            }

            var downloadComparison = right.DownloadBytesPerSecond.CompareTo(left.DownloadBytesPerSecond);
            if (downloadComparison != 0)
            {
                return downloadComparison;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left.ProcessName, right.ProcessName);
        });

        if (topN.HasValue && topN.Value < items.Length)
        {
            Array.Resize(ref items, topN.Value);
        }

        return items;
    }

    private static DateTimeOffset AlignToBucketStart(DateTimeOffset timestamp, int granularitySeconds)
    {
        var ticksPerBucket = TimeSpan.FromSeconds(granularitySeconds).Ticks;
        var alignedTicks = timestamp.UtcTicks - (timestamp.UtcTicks % ticksPerBucket);
        return new DateTimeOffset(alignedTicks, TimeSpan.Zero);
    }

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

    private sealed class AppUsageAccumulator(string appKey, string processName, string? displayName)
    {
        public string AppKey { get; } = appKey;
        public string ProcessName { get; } = processName;
        public string? DisplayName { get; } = displayName;
        public long UploadBytes { get; set; }
        public long DownloadBytes { get; set; }
        public long WanUploadBytes { get; set; }
        public long WanDownloadBytes { get; set; }
        public long LanUploadBytes { get; set; }
        public long LanDownloadBytes { get; set; }

        public AppTrafficUsage ToModel(double intervalSeconds)
        {
            return new AppTrafficUsage
            {
                AppKey = AppKey,
                ProcessName = ProcessName,
                DisplayName = DisplayName,
                UploadBytesPerSecond = UploadBytes / intervalSeconds,
                DownloadBytesPerSecond = DownloadBytes / intervalSeconds,
                WanUploadBytesPerSecond = WanUploadBytes / intervalSeconds,
                WanDownloadBytesPerSecond = WanDownloadBytes / intervalSeconds,
                LanUploadBytesPerSecond = LanUploadBytes / intervalSeconds,
                LanDownloadBytesPerSecond = LanDownloadBytes / intervalSeconds
            };
        }
    }

    private sealed class RealtimeSlot
    {
        private readonly Dictionary<string, SlotAppUsageAccumulator> _appUsages = new(StringComparer.OrdinalIgnoreCase);

        public long SlotIndex { get; private set; } = -1;
        public bool IsValid => SlotIndex >= 0;
        public long TotalUploadBytes { get; private set; }
        public long TotalDownloadBytes { get; private set; }
        public long WanUploadBytes { get; private set; }
        public long WanDownloadBytes { get; private set; }
        public long LanUploadBytes { get; private set; }
        public long LanDownloadBytes { get; private set; }
        public IReadOnlyDictionary<string, SlotAppUsageAccumulator> AppUsages => _appUsages;

        public void Reset(long slotIndex)
        {
            SlotIndex = slotIndex;
            TotalUploadBytes = 0;
            TotalDownloadBytes = 0;
            WanUploadBytes = 0;
            WanDownloadBytes = 0;
            LanUploadBytes = 0;
            LanDownloadBytes = 0;
            _appUsages.Clear();
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

            foreach (var pair in other._appUsages)
            {
                _appUsages[pair.Key] = pair.Value.Clone();
            }
        }

        public void Add(
            string appKey,
            string processName,
            string? displayName,
            TrafficDirection direction,
            AddressScopeType scopeType,
            long bytes)
        {
            if (!_appUsages.TryGetValue(appKey, out var appUsage))
            {
                appUsage = new SlotAppUsageAccumulator(processName, displayName);
                _appUsages[appKey] = appUsage;
            }

            if (direction == TrafficDirection.Outbound)
            {
                TotalUploadBytes += bytes;
                appUsage.UploadBytes += bytes;

                if (scopeType == AddressScopeType.Wan)
                {
                    WanUploadBytes += bytes;
                    appUsage.WanUploadBytes += bytes;
                }
                else if (scopeType == AddressScopeType.Lan)
                {
                    LanUploadBytes += bytes;
                    appUsage.LanUploadBytes += bytes;
                }
            }
            else
            {
                TotalDownloadBytes += bytes;
                appUsage.DownloadBytes += bytes;

                if (scopeType == AddressScopeType.Wan)
                {
                    WanDownloadBytes += bytes;
                    appUsage.WanDownloadBytes += bytes;
                }
                else if (scopeType == AddressScopeType.Lan)
                {
                    LanDownloadBytes += bytes;
                    appUsage.LanDownloadBytes += bytes;
                }
            }
        }
    }

    private sealed class SlotAppUsageAccumulator(string processName, string? displayName)
    {
        public string ProcessName { get; } = processName;
        public string? DisplayName { get; } = displayName;
        public long UploadBytes { get; set; }
        public long DownloadBytes { get; set; }
        public long WanUploadBytes { get; set; }
        public long WanDownloadBytes { get; set; }
        public long LanUploadBytes { get; set; }
        public long LanDownloadBytes { get; set; }

        public SlotAppUsageAccumulator Clone()
        {
            return new SlotAppUsageAccumulator(ProcessName, DisplayName)
            {
                UploadBytes = UploadBytes,
                DownloadBytes = DownloadBytes,
                WanUploadBytes = WanUploadBytes,
                WanDownloadBytes = WanDownloadBytes,
                LanUploadBytes = LanUploadBytes,
                LanDownloadBytes = LanDownloadBytes
            };
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
        double LanDownloadBytesPerSecond,
        IReadOnlyList<AppTrafficUsage> AppUsages);
}
