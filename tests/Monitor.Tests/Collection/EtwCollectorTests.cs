using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using Monitor.Network.Collectors;

namespace Monitor.Tests.Collection;

public sealed class EtwCollectorTests
{
    [Fact]
    public async Task FailedStartRetriesAndUnexpectedProcessExitRecovers()
    {
        var calls = 0;
        var sessions = new System.Collections.Concurrent.ConcurrentQueue<FakeSession>();
        using var collector = new EtwNetworkCollector(NullLogger<EtwNetworkCollector>.Instance,
            new TrafficAggregatorTests.FakeSettings(), (_, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new UnauthorizedAccessException();
                var session = new FakeSession();
                sessions.Enqueue(session);
                return session;
            }, TimeSpan.FromMilliseconds(200), false);

        await collector.StartAsync();
        var failed = collector.GetSnapshot();
        Assert.False(failed.IsRunning);
        Assert.True(failed.IsRecovering);
        Assert.NotNull(failed.LastFailureAt);
        await Eventually(() => collector.IsRunning && sessions.Count == 1);
        Assert.NotNull(collector.GetSnapshot().LastRecoveryAt);
        Assert.True(sessions.TryPeek(out var first));
        first!.Fail();
        await Eventually(() => sessions.Count >= 2 && collector.IsRunning);
        var recovered = collector.GetSnapshot();
        Assert.NotNull(recovered.LastFailureAt);
        Assert.NotNull(recovered.LastRecoveryAt);
        Assert.True(recovered.IsRunning);
        await collector.StopAsync();
        Assert.False(collector.IsRunning);
    }

    [Fact]
    public async Task StopDuringBackoffPreventsSubsequentStarts()
    {
        var calls = 0;
        using var collector = new EtwNetworkCollector(NullLogger<EtwNetworkCollector>.Instance,
            new TrafficAggregatorTests.FakeSettings(), (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new UnauthorizedAccessException();
            }, TimeSpan.FromMilliseconds(100), false);
        await collector.StartAsync();
        await Task.WhenAll(collector.StopAsync(), collector.StopAsync());
        await collector.StartAsync();
        await Task.Delay(200);
        Assert.Equal(1, calls);
        Assert.True(collector.GetSnapshot().IsStopping);
        Assert.False(collector.GetSnapshot().IsRecovering);
    }

    [Fact]
    public async Task AdaptiveRestartIsCoordinatedWithStop()
    {
        var calls = 0;
        var first = new FakeSession();
        using var collector = new EtwNetworkCollector(NullLogger<EtwNetworkCollector>.Instance,
            new TrafficAggregatorTests.FakeSettings(), (_, _, _) =>
            {
                var index = Interlocked.Increment(ref calls);
                return index == 1 ? first : new FakeSession();
            }, TimeSpan.FromMilliseconds(20), false);
        await collector.StartAsync();
        first.Lost = 1;
        _ = collector.GetSnapshot(); // polls the loss counter and schedules adaptive restart
        await collector.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var afterStop = Volatile.Read(ref calls);
        await Task.Delay(150);
        Assert.Equal(afterStop, calls);
        Assert.False(collector.IsRunning);
        Assert.Equal(1, collector.GetSnapshot().TotalLostEvents);
    }

    [Fact]
    public void CleanupOnlyStopsConfirmedDeadOwnersAndOwnKnownSessions()
    {
        const string prefix = "Monitor.Network.Etw.";
        var dead = prefix + "101." + Guid.NewGuid().ToString("N");
        var live = prefix + "102." + Guid.NewGuid().ToString("N");
        var reused = prefix + "103." + Guid.NewGuid().ToString("N");
        var inaccessible = prefix + "104." + Guid.NewGuid().ToString("N");
        var throws = prefix + "105." + Guid.NewGuid().ToString("N");
        var owned = prefix + "106." + Guid.NewGuid().ToString("N");
        var current = prefix + "107." + Guid.NewGuid().ToString("N");
        var malformed = prefix + "101.not-a-guid";
        var foreign = "Other.101." + Guid.NewGuid().ToString("N");
        var stopped = new List<string>();
        bool? Alive(int pid) => pid switch
        {
            101 => false,
            102 or 103 or 106 => true,
            104 => null,
            105 => throw new UnauthorizedAccessException(),
            107 => false,
            _ => throw new InvalidOperationException()
        };

        var count = EtwNetworkCollector.CleanupStaleMonitorSessions(
            [dead, live, reused, inaccessible, throws, owned, current, malformed, foreign], current,
            Alive, name => { stopped.Add(name); return true; }, new HashSet<string> { owned, current });
        Assert.Equal(2, count);
        Assert.Equal(new[] { dead, owned }, stopped);
    }

    private static async Task Eventually(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class FakeSession : IEtwSession
    {
        private readonly ManualResetEventSlim _release = new();
        private int _fail;
        public long Lost;
        public long EventsLost => Interlocked.Read(ref Lost);
        public void RegisterHandlers(Action<KernelTraceEventParser> register) { }
        public void Process()
        {
            _release.Wait();
            if (Volatile.Read(ref _fail) != 0) throw new InvalidOperationException("simulated ETW process failure");
        }
        public void Fail() { Volatile.Write(ref _fail, 1); _release.Set(); }
        public void Stop() => _release.Set();
        public void Dispose() => _release.Set();
    }
}
