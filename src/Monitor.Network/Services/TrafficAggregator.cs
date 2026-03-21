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
    private readonly object _syncRoot = new();
    private readonly Queue<RealtimeTrafficEvent> _recentEvents = new();
    private readonly Dictionary<BucketKey, BucketAccumulator> _activeBuckets = new();
    private readonly Queue<TrafficBucket> _pendingBuckets = new();
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
            CleanupExpiredRealtimeEventsCore(now);
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
            CleanupExpiredRealtimeEventsCore(now);
            view = BuildRealtimeViewCore(now);
        }

        var apps = view.AppUsages
            .OrderByDescending(static x => x.UploadBytesPerSecond + x.DownloadBytesPerSecond)
            .ThenByDescending(static x => x.DownloadBytesPerSecond)
            .ThenBy(static x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(topN)
            .ToArray();

        return Task.FromResult<IReadOnlyList<AppTrafficUsage>>(apps);
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

            var realtimeEvent = new RealtimeTrafficEvent
            {
                Timestamp = timestamp,
                AppKey = appEntry.AppKey,
                ProcessName = appEntry.ProcessName,
                DisplayName = appEntry.DisplayName,
                Direction = traceEvent.Direction,
                ScopeType = scopeType,
                Bytes = traceEvent.Bytes
            };

            lock (_syncRoot)
            {
                RotateBucketsCore(timestamp);
                _recentEvents.Enqueue(realtimeEvent);
                AddToBucketCore(realtimeEvent);
                CleanupExpiredRealtimeEventsCore(timestamp);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to aggregate network trace event.");
        }
    }

    private void AddToBucketCore(RealtimeTrafficEvent realtimeEvent)
    {
        var bucketGranularitySeconds = _settingsMonitor.CurrentValue.AggregateIntervalSeconds;
        var bucketStartTime = AlignToBucketStart(realtimeEvent.Timestamp, bucketGranularitySeconds);
        var key = new BucketKey(
            bucketStartTime,
            bucketGranularitySeconds,
            realtimeEvent.AppKey,
            realtimeEvent.Direction,
            realtimeEvent.ScopeType);

        if (!_activeBuckets.TryGetValue(key, out var accumulator))
        {
            accumulator = new BucketAccumulator(key);
            _activeBuckets[key] = accumulator;
        }

        accumulator.Bytes += realtimeEvent.Bytes;
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

    private void CleanupExpiredRealtimeEventsCore(DateTimeOffset referenceTime)
    {
        var retention = GetRealtimeRetentionWindow();
        var cutoff = referenceTime - retention;

        while (_recentEvents.Count > 0 && _recentEvents.Peek().Timestamp < cutoff)
        {
            _recentEvents.Dequeue();
        }
    }

    private RealtimeView BuildRealtimeViewCore(DateTimeOffset referenceTime)
    {
        var intervalSeconds = Math.Max(_settingsMonitor.CurrentValue.NetworkSampleIntervalMs / 1000d, 0.1d);
        var windowStart = referenceTime.AddSeconds(-intervalSeconds);
        var appUsageMap = new Dictionary<string, AppUsageAccumulator>(StringComparer.OrdinalIgnoreCase);

        double totalUpload = 0;
        double totalDownload = 0;
        double wanUpload = 0;
        double wanDownload = 0;
        double lanUpload = 0;
        double lanDownload = 0;

        foreach (var traceEvent in _recentEvents)
        {
            if (traceEvent.Timestamp < windowStart)
            {
                continue;
            }

            var rate = traceEvent.Bytes / intervalSeconds;
            if (!appUsageMap.TryGetValue(traceEvent.AppKey, out var appUsage))
            {
                appUsage = new AppUsageAccumulator(traceEvent.AppKey, traceEvent.ProcessName, traceEvent.DisplayName);
                appUsageMap[traceEvent.AppKey] = appUsage;
            }

            if (traceEvent.Direction == TrafficDirection.Outbound)
            {
                totalUpload += rate;
                appUsage.UploadBytesPerSecond += rate;

                if (traceEvent.ScopeType == AddressScopeType.Wan)
                {
                    wanUpload += rate;
                    appUsage.WanUploadBytesPerSecond += rate;
                }
                else if (traceEvent.ScopeType == AddressScopeType.Lan)
                {
                    lanUpload += rate;
                    appUsage.LanUploadBytesPerSecond += rate;
                }
            }
            else
            {
                totalDownload += rate;
                appUsage.DownloadBytesPerSecond += rate;

                if (traceEvent.ScopeType == AddressScopeType.Wan)
                {
                    wanDownload += rate;
                    appUsage.WanDownloadBytesPerSecond += rate;
                }
                else if (traceEvent.ScopeType == AddressScopeType.Lan)
                {
                    lanDownload += rate;
                    appUsage.LanDownloadBytesPerSecond += rate;
                }
            }
        }

        return new RealtimeView(
            totalUpload,
            totalDownload,
            wanUpload,
            wanDownload,
            lanUpload,
            lanDownload,
            appUsageMap.Values
                .Where(static x => x.UploadBytesPerSecond > 0 || x.DownloadBytesPerSecond > 0)
                .Select(static x => x.ToModel())
                .ToArray());
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
        _latestTopApps = view.AppUsages
            .OrderByDescending(static x => x.UploadBytesPerSecond + x.DownloadBytesPerSecond)
            .ThenByDescending(static x => x.DownloadBytesPerSecond)
            .ThenBy(static x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return snapshot;
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
        public double UploadBytesPerSecond { get; set; }
        public double DownloadBytesPerSecond { get; set; }
        public double WanUploadBytesPerSecond { get; set; }
        public double WanDownloadBytesPerSecond { get; set; }
        public double LanUploadBytesPerSecond { get; set; }
        public double LanDownloadBytesPerSecond { get; set; }

        public AppTrafficUsage ToModel()
        {
            return new AppTrafficUsage
            {
                AppKey = AppKey,
                ProcessName = ProcessName,
                DisplayName = DisplayName,
                UploadBytesPerSecond = UploadBytesPerSecond,
                DownloadBytesPerSecond = DownloadBytesPerSecond,
                WanUploadBytesPerSecond = WanUploadBytesPerSecond,
                WanDownloadBytesPerSecond = WanDownloadBytesPerSecond,
                LanUploadBytesPerSecond = LanUploadBytesPerSecond,
                LanDownloadBytesPerSecond = LanDownloadBytesPerSecond
            };
        }
    }

    private sealed record RealtimeTrafficEvent
    {
        public DateTimeOffset Timestamp { get; init; }
        public string AppKey { get; init; } = string.Empty;
        public string ProcessName { get; init; } = string.Empty;
        public string? DisplayName { get; init; }
        public TrafficDirection Direction { get; init; }
        public AddressScopeType ScopeType { get; init; }
        public long Bytes { get; init; }
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
