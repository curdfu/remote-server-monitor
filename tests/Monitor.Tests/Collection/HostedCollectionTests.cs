using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Models;
using Monitor.Hardware.Services;
using Monitor.Network.Abstractions;
using Monitor.Network.Models;
using Monitor.Service.HostedServices;
using Monitor.Storage.Abstractions;
using Monitor.Storage.Repositories;

namespace Monitor.Tests.Collection;

public sealed class HostedCollectionTests
{
    [Fact]
    public async Task EveryIntervalWinnerCancelsTheOtherWaits()
    {
        var demand = new TrackingDemand();
        var service = CreateCollector(demand, new FlakyHardware(), new FakeProcessCollector());
        var method = typeof(CollectorHostedService).GetMethod("WaitForIntervalSettingsOrHardwareDemandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var index = 0; index < 8; index++)
        {
            await (Task<bool>)method.Invoke(service, [TimeSpan.FromMilliseconds(2), 0L, CancellationToken.None])!;
        }

        Assert.Equal(8, demand.Started);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref demand.Cancelled) == 8, TimeSpan.FromSeconds(2)));
        Assert.Equal(8, demand.Cancelled);
    }

    [Fact]
    public async Task HardwareFailureKeepsLastValueAndActivationRebuildsCpuBaseline()
    {
        var demand = new HardwareMonitoringDemand();
        var hardware = new FlakyHardware();
        var cpu = new FakeProcessCollector();
        var buffer = new HardwareSnapshotBuffer();
        var cpuBuffer = new ProcessCpuSnapshotBuffer();
        var service = CreateCollector(demand, hardware, cpu, buffer, cpuBuffer);
        var collect = typeof(CollectorHostedService).GetMethod("CollectSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)collect.Invoke(service, [CancellationToken.None])!;
        var first = buffer.GetLatest();
        hardware.Fail = true;
        await (Task)collect.Invoke(service, [CancellationToken.None])!;
        Assert.Same(first, buffer.GetLatest());
        Assert.Equal(1, cpu.Captures); // idle sampling is throttled
        demand.AddHardwareSubscriber("subscriber");
        await (Task)collect.Invoke(service, [CancellationToken.None])!;
        Assert.Equal(1, cpu.Resets);
        Assert.False(cpuBuffer.GetLatest()!.IsReady);
        await (Task)collect.Invoke(service, [CancellationToken.None])!;
        Assert.True(cpuBuffer.GetLatest()!.IsReady);
    }

    [Fact]
    public async Task CancellationAfterDequeueRequeuesHardwareBatch()
    {
        var buffer = new HardwareSnapshotBuffer();
        buffer.Add(new HardwareSnapshot());
        var factory = new CancelledFactory();
        var repository = new HardwareRepository(factory, NullLogger<HardwareRepository>.Instance);
        var service = CreateCollector(new HardwareMonitoringDemand(), new FlakyHardware(), new FakeProcessCollector(), buffer, repository: repository);
        using var cancel = new CancellationTokenSource();
        factory.OnOpen = cancel.Cancel;
        var persist = typeof(CollectorHostedService).GetMethod("PersistPendingSnapshotsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)persist.Invoke(service, [buffer, repository, cancel.Token, true])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, buffer.PendingCount);
    }

    [Fact]
    public async Task StopWaitsForCycleBeforeSealingAndPersisting()
    {
        var aggregator = new BlockingAggregator();
        var repository = new NetworkTrafficRepository(new CancelledFactory(), new TrafficAggregatorTests.FakeRegistry(),
            NullLogger<NetworkTrafficRepository>.Instance);
        var service = new AggregationHostedService(NullLogger<AggregationHostedService>.Instance, aggregator,
            new TrafficAggregatorTests.FakeCollector(), repository, new TrafficAggregatorTests.FakeSettings());
        await service.StartAsync(CancellationToken.None);
        await aggregator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = service.StopAsync(CancellationToken.None);
        Assert.False(aggregator.Completed);
        Assert.False(stop.IsCompleted);
        aggregator.Release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(aggregator.Completed);
        Assert.Equal(1, aggregator.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task FailedFlushStillAttemptsPendingPersistence()
    {
        var aggregator = new FakeAggregator { FailFlush = true };
        var repository = new NetworkTrafficRepository(new CancelledFactory(), new TrafficAggregatorTests.FakeRegistry(),
            NullLogger<NetworkTrafficRepository>.Instance);
        var service = new AggregationHostedService(NullLogger<AggregationHostedService>.Instance, aggregator,
            new TrafficAggregatorTests.FakeCollector(), repository, new TrafficAggregatorTests.FakeSettings());
        var cycle = typeof(AggregationHostedService).GetMethod("RunAggregationCycleSafeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)cycle.Invoke(service, [CancellationToken.None])!;
        Assert.Equal(1, aggregator.Failures);
        Assert.Equal(0, aggregator.Confirmed);
    }

    [Fact]
    public async Task FlushTimeoutUsesFreshTokenForPersistence()
    {
        var aggregator = new FakeAggregator { TimeoutFlush = true };
        var repository = new NetworkTrafficRepository(new CancelledFactory(), new TrafficAggregatorTests.FakeRegistry(),
            NullLogger<NetworkTrafficRepository>.Instance);
        var settings = new TrafficAggregatorTests.FakeSettings();
        settings.Current.NetworkProcessingIntervalMs = 1;
        var service = new AggregationHostedService(NullLogger<AggregationHostedService>.Instance, aggregator,
            new TrafficAggregatorTests.FakeCollector(), repository, settings);
        var cycle = typeof(AggregationHostedService).GetMethod("RunAggregationCycleSafeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)cycle.Invoke(service, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, aggregator.Failures);
        Assert.Equal(0, aggregator.Confirmed);
    }

    [Fact]
    public async Task FailedNetworkSaveRetainsUnconfirmedBucketsAndReportsFailure()
    {
        var aggregator = new FakeAggregator();
        var registry = new TrafficAggregatorTests.FakeRegistry();
        var repository = new NetworkTrafficRepository(new CancelledFactory(), registry, NullLogger<NetworkTrafficRepository>.Instance);
        var service = new AggregationHostedService(NullLogger<AggregationHostedService>.Instance, aggregator,
            new TrafficAggregatorTests.FakeCollector(), repository, new TrafficAggregatorTests.FakeSettings());
        var persist = typeof(AggregationHostedService).GetMethod("PersistPendingBucketsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)persist.Invoke(service, [CancellationToken.None])!;
        Assert.Equal(0, aggregator.Confirmed);
        Assert.Equal(1, aggregator.Failures);
    }

    private static CollectorHostedService CreateCollector(IHardwareMonitoringDemand demand, IHardwareCollector hardware,
        IProcessCpuCollector cpu, HardwareSnapshotBuffer? buffer = null, ProcessCpuSnapshotBuffer? cpuBuffer = null,
        HardwareRepository? repository = null) => new(
        NullLogger<CollectorHostedService>.Instance, hardware, buffer ?? new HardwareSnapshotBuffer(), cpu,
        cpuBuffer ?? new ProcessCpuSnapshotBuffer(), demand,
        repository ?? new HardwareRepository(new CancelledFactory(), NullLogger<HardwareRepository>.Instance),
        new TrafficAggregatorTests.FakeCollector(), new TrafficAggregatorTests.FakeSettings());

    private sealed class TrackingDemand : IHardwareMonitoringDemand
    {
        public int Started;
        public int Cancelled;
        public bool HasHardwareSubscribers => false;
        public long ActivationVersion => 0;
        public bool AddHardwareSubscriber(string connectionId) => false;
        public bool RemoveHardwareSubscriber(string connectionId) => false;
        public async Task WaitForActivationAsync(long observedVersion, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Started);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
        }
    }

    private sealed class FlakyHardware : IHardwareCollector
    {
        public bool Fail { get; set; }
        public Task<HardwareSnapshot> GetCurrentSnapshotAsync(CancellationToken cancellationToken = default) =>
            Fail ? throw new InvalidOperationException("sensor failed") : Task.FromResult(new HardwareSnapshot());
    }

    private sealed class FakeProcessCollector : IProcessCpuCollector
    {
        public int Captures;
        public int Resets;
        private bool _baseline;
        public void Reset() { Resets++; _baseline = false; }
        public Task<ProcessCpuSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
        {
            Captures++;
            var snapshot = new ProcessCpuSnapshot { IsReady = _baseline, SampleTime = DateTimeOffset.UtcNow };
            _baseline = true;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class CancelledFactory : IDbConnectionFactory
    {
        public Action? OnOpen { get; set; }
        public string DatabasePath => ":memory:";
        public SqliteConnection CreateConnection() => throw new InvalidOperationException("test persistence failure");
        public Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            OnOpen?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("test persistence failure");
        }
    }

    private sealed class BlockingAggregator : INetworkAggregator
    {
        private int _active;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Completed { get; private set; }
        public int MaximumConcurrentOperations { get; private set; }
        public long PendingEventCount => 0;
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrentOperations = Math.Max(MaximumConcurrentOperations, active);
            Entered.TrySetResult();
            await Release.Task;
            Interlocked.Decrement(ref _active);
            cancellationToken.ThrowIfCancellationRequested();
        }
        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrentOperations = Math.Max(MaximumConcurrentOperations, active);
            Completed = true;
            Interlocked.Decrement(ref _active);
            return Task.CompletedTask;
        }
        public IReadOnlyList<TrafficBucket> PeekPendingBuckets(int maxCount) => [];
        public void ConfirmPendingBuckets(int count) { }
        public TrafficAggregatorDiagnosticsSnapshot GetDiagnostics() => new();
        public void RecordPersistenceFailure() { }
        public void RecordPersistenceSuccess() { }
    }

    private sealed class FakeAggregator : INetworkAggregator
    {
        public int Confirmed;
        public int Failures;
        public bool FailFlush;
        public bool TimeoutFlush;
        public long PendingEventCount => 0;
        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            FailFlush ? Task.FromException(new InvalidOperationException("simulated flush failure")) :
            TimeoutFlush ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken) : Task.CompletedTask;
        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IReadOnlyList<TrafficBucket> PeekPendingBuckets(int maxCount) => [new TrafficBucket { AppKey = "test" }];
        public void ConfirmPendingBuckets(int count) => Confirmed += count;
        public TrafficAggregatorDiagnosticsSnapshot GetDiagnostics() => new();
        public void RecordPersistenceFailure() => Failures++;
        public void RecordPersistenceSuccess() { }
    }
}
