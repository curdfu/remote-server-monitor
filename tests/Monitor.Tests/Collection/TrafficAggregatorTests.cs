using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;
using Monitor.Network.Models;
using Monitor.Network.Services;

namespace Monitor.Tests.Collection;

public sealed class TrafficAggregatorTests
{
    [Fact]
    public async Task QueueIsBoundedAndFlushWaitsForPriorEvents()
    {
        var collector = new FakeCollector();
        using var registry = new BlockingRegistry();
        using var aggregator = Create(collector, registry, queueCapacity: 1);
        collector.Publish(Event(1, 10));
        Assert.True(registry.Entered.Wait(TimeSpan.FromSeconds(5)));
        collector.Publish(Event(2, 20));
        collector.Publish(Event(3, 30));
        var diagnostics = aggregator.GetDiagnostics();
        Assert.Equal(1, diagnostics.QueueFullRejectedEvents);
        Assert.Equal(1, diagnostics.RejectedEvents);
        Assert.Equal(30, diagnostics.RejectedBytes);
        Assert.NotNull(diagnostics.FirstRejectedAt);
        var flush = aggregator.FlushAsync();
        Assert.False(flush.IsCompleted);
        registry.Release.Set();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, aggregator.PendingEventCount);
        await aggregator.CompleteAsync();
        Assert.Equal(30, aggregator.PeekPendingBuckets(10).Sum(bucket => bucket.Bytes));
    }

    [Fact]
    public async Task BucketBudgetRetainsUnconfirmedDataAndResumesAfterConfirm()
    {
        var collector = new FakeCollector();
        using var aggregator = Create(collector, new FakeRegistry(), bucketCapacity: 1);
        collector.Publish(Event(1, 10, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await aggregator.FlushAsync();
        var first = Assert.Single(aggregator.PeekPendingBuckets(10));
        collector.Publish(Event(2, 25));
        await aggregator.FlushAsync();
        var full = aggregator.GetDiagnostics();
        Assert.Equal(1, full.BucketCapacityRejectedEvents);
        Assert.Equal(25, full.RejectedBytes);
        Assert.Equal(first, Assert.Single(aggregator.PeekPendingBuckets(10)));
        aggregator.ConfirmPendingBuckets(1);
        collector.Publish(Event(2, 40));
        await aggregator.CompleteAsync();
        var resumed = Assert.Single(aggregator.PeekPendingBuckets(10));
        Assert.Equal(40, resumed.Bytes);
        Assert.False(aggregator.GetDiagnostics().IsAccepting);
    }

    [Fact]
    public async Task FullBucketBudgetStillAcceptsExistingKeyButRejectsNewKey()
    {
        var collector = new FakeCollector();
        using var aggregator = Create(collector, new FakeRegistry(), bucketCapacity: 1);
        var timestamp = DateTimeOffset.UtcNow;
        collector.Publish(Event(1, 10, timestamp));
        await aggregator.FlushAsync();
        collector.Publish(Event(1, 20, timestamp));
        collector.Publish(Event(2, 30, timestamp));
        await aggregator.FlushAsync();
        var diagnostics = aggregator.GetDiagnostics();
        Assert.Equal(1, diagnostics.ActiveBucketCount);
        Assert.Equal(1, diagnostics.BucketCapacityRejectedEvents);
        Assert.Equal(30, diagnostics.RejectedBytes);
        await aggregator.CompleteAsync();
        var bucket = Assert.Single(aggregator.PeekPendingBuckets(10));
        Assert.Equal(30, bucket.Bytes);
        Assert.Equal(2, bucket.Packets);
    }

    [Fact]
    public async Task ReservedBarrierMakesProgressWhileEventSlotRemainsSaturated()
    {
        var collector = new FakeCollector();
        using var registry = new StepRegistry();
        using var aggregator = Create(collector, registry, queueCapacity: 1);
        collector.Publish(Event(1, 10));
        Assert.True(registry.FirstEntered.Wait(TimeSpan.FromSeconds(5)));
        collector.Publish(Event(2, 20));
        var firstBarrier = aggregator.FlushAsync();
        Assert.NotNull(typeof(TrafficAggregator).GetField("_flushBarrier", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(aggregator));
        using var cancelled = new CancellationTokenSource();
        var secondBarrier = aggregator.FlushAsync(cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondBarrier);
        collector.Publish(Event(3, 30)); // rejected without consuming the control slot
        registry.ReleaseFirst.Set();
        Assert.True(registry.SecondEntered.Wait(TimeSpan.FromSeconds(5)));
        collector.Publish(Event(4, 40)); // refills the event slot behind the barrier
        registry.ReleaseSecond.Set();
        await firstBarrier.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.ThirdEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, aggregator.GetDiagnostics().QueueFullRejectedEvents);
        registry.ReleaseThird.Set();
        await aggregator.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(70, aggregator.PeekPendingBuckets(10).Sum(bucket => bucket.Bytes));
    }

    [Fact]
    public async Task LaterFlushWaitsForEventsBehindCancelledBarrier()
    {
        var collector = new FakeCollector();
        using var registry = new StepRegistry();
        using var aggregator = Create(collector, registry, queueCapacity: 1);
        collector.Publish(Event(1, 10));
        Assert.True(registry.FirstEntered.Wait(TimeSpan.FromSeconds(5)));
        using var cancelled = new CancellationTokenSource();
        var abandoned = aggregator.FlushAsync(cancelled.Token);
        collector.Publish(Event(2, 20));
        var later = aggregator.FlushAsync();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        registry.ReleaseFirst.Set();
        Assert.True(registry.SecondEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(later.IsCompleted);
        registry.ReleaseSecond.Set();
        await later.WaitAsync(TimeSpan.FromSeconds(5));
        await aggregator.CompleteAsync();
        Assert.Equal(30, aggregator.PeekPendingBuckets(10).Sum(bucket => bucket.Bytes));
    }

    [Fact]
    public async Task CompleteSealsCurrentAndLateBucketsAcrossSettingsChanges()
    {
        var collector = new FakeCollector();
        var settings = new FakeSettings();
        using var aggregator = Create(collector, new FakeRegistry(), settings: settings);
        var now = DateTimeOffset.UtcNow;
        collector.Publish(Event(1, 10, now));
        await aggregator.FlushAsync();
        settings.SetGranularity(30);
        collector.Publish(Event(1, 20, now.AddMinutes(-1)));
        collector.Publish(Event(1, 30, now));
        await aggregator.CompleteAsync();
        var buckets = aggregator.PeekPendingBuckets(10);
        Assert.Equal(60, buckets.Sum(bucket => bucket.Bytes));
        Assert.Contains(buckets, bucket => bucket.BucketGranularitySeconds == 10);
        Assert.Equal(2, buckets.Count(bucket => bucket.BucketGranularitySeconds == 30));
    }

    [Fact]
    public async Task CancelledFlushDoesNotConsumeOrConfirmEvents()
    {
        var collector = new FakeCollector();
        using var registry = new BlockingRegistry();
        using var aggregator = Create(collector, registry);
        collector.Publish(Event(1, 11));
        Assert.True(registry.Entered.Wait(TimeSpan.FromSeconds(5)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aggregator.FlushAsync(cancellation.Token));
        registry.Release.Set();
        await aggregator.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(11, Assert.Single(aggregator.PeekPendingBuckets(10)).Bytes);
    }

    [Fact]
    public void PidCacheExpiresFromResolutionRatherThanLastEvent()
    {
        var clock = new ManualTimeProvider();
        var resolver = new ChangingResolver(clock);
        var registry = new AppRegistryService(resolver, clock);
        Assert.Equal("app-1", registry.GetOrAdd(7).AppKey);
        for (var index = 0; index < 29; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("app-1", registry.GetOrAdd(7).AppKey);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("app-2", registry.GetOrAdd(7).AppKey);
        Assert.Equal(2, resolver.Calls);
    }

    private static NetworkTraceEvent Event(int pid, long bytes, DateTimeOffset? timestamp = null) => new()
    {
        ProcessId = pid,
        Bytes = bytes,
        Timestamp = timestamp ?? DateTimeOffset.UtcNow,
        Direction = TrafficDirection.Outbound
    };

    private static TrafficAggregator Create(FakeCollector collector, IAppRegistry registry, int queueCapacity = 16, int bucketCapacity = 16, FakeSettings? settings = null) =>
        new(collector, registry, new FakeClassifier(), settings ?? new FakeSettings(), NullLogger<TrafficAggregator>.Instance,
            new TrafficAggregatorOptions { EventQueueCapacity = queueCapacity, BucketCapacity = bucketCapacity });

    internal sealed class FakeCollector : INetworkCollector
    {
        public event Action<NetworkTraceEvent>? EventReceived;
        public bool IsRunning { get; private set; }
        public void Publish(NetworkTraceEvent value) => EventReceived?.Invoke(value);
        public Task StartAsync(CancellationToken cancellationToken = default) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { IsRunning = false; return Task.CompletedTask; }
    }

    internal class FakeRegistry : IAppRegistry
    {
        public virtual AppRegistryEntry GetOrAdd(int pid) => new() { AppKey = $"app-{pid}", ProcessName = $"app-{pid}" };
        public AppRegistryEntry? GetByAppKey(string appKey) => null;
        public IReadOnlyList<AppRegistryEntry> GetAll() => [];
    }

    private sealed class BlockingRegistry : FakeRegistry, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public override AppRegistryEntry GetOrAdd(int pid)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return base.GetOrAdd(pid);
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class StepRegistry : FakeRegistry, IDisposable
    {
        private int _count;
        public ManualResetEventSlim FirstEntered { get; } = new();
        public ManualResetEventSlim SecondEntered { get; } = new();
        public ManualResetEventSlim ThirdEntered { get; } = new();
        public ManualResetEventSlim ReleaseFirst { get; } = new();
        public ManualResetEventSlim ReleaseSecond { get; } = new();
        public ManualResetEventSlim ReleaseThird { get; } = new();
        public override AppRegistryEntry GetOrAdd(int pid)
        {
            var count = Interlocked.Increment(ref _count);
            var (entered, release) = count switch
            {
                1 => (FirstEntered, ReleaseFirst),
                2 => (SecondEntered, ReleaseSecond),
                _ => (ThirdEntered, ReleaseThird)
            };
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return base.GetOrAdd(pid);
        }
        public void Dispose()
        {
            ReleaseFirst.Set(); ReleaseSecond.Set(); ReleaseThird.Set();
            FirstEntered.Dispose(); SecondEntered.Dispose(); ThirdEntered.Dispose();
            ReleaseFirst.Dispose(); ReleaseSecond.Dispose(); ReleaseThird.Dispose();
        }
    }

    private sealed class FakeClassifier : IAddressClassifier
    {
        public AddressScopeType Classify(IPAddress? remoteAddress, IPAddress? localAddress = null) => AddressScopeType.Wan;
        public AddressScopeType Classify(string? remoteAddress, string? localAddress = null) => AddressScopeType.Wan;
    }

    internal sealed class FakeSettings : IMonitorSettingsProvider
    {
        private Action<MonitorSettings>? _callback;
        public MonitorSettings Current { get; } = new();
        public MonitorSettings Update(AppSettingsDto settings) => Current;
        public IDisposable RegisterChangeCallback(Action<MonitorSettings> callback) { _callback += callback; return new Callback(() => _callback -= callback); }
        public void SetGranularity(int value) { Current.AggregateIntervalSeconds = value; _callback?.Invoke(Current); }
        private sealed class Callback(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ChangingResolver(TimeProvider clock) : IProcessResolver
    {
        public int Calls { get; private set; }
        public ResolvedProcessInfo Resolve(int pid) => new()
        {
            ProcessId = pid, AppKey = $"app-{++Calls}", ProcessName = "process", ResolvedAt = clock.GetUtcNow()
        };
    }
}
