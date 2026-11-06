using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Models;
using Monitor.Network.Abstractions;
using Monitor.Network.Models;
using Monitor.WebApi.Endpoints;

namespace Monitor.Tests.Web;

public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task ReportsHealthyCollectionWithCompleteStatistics()
    {
        using var fixture = new HealthFixture();
        var result = await fixture.GetAsync();
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.True(result.GetProperty("network").GetProperty("statisticsComplete").GetBoolean());
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("processing")]
    [InlineData("etw")]
    [InlineData("recovered")]
    public async Task RecoveryDoesNotEraseEarlierStatisticsGaps(string cause)
    {
        using var fixture = new HealthFixture(
            HealthyAggregation(rejected: cause == "rejected" ? 2 : 0, processingFailures: cause == "processing" ? 1 : 0),
            new NetworkCollectorDiagnosticsSnapshot
            {
                IsRunning = true,
                TotalLostEvents = cause == "etw" ? 3 : 0,
                LastFailureAt = cause == "recovered" ? DateTimeOffset.UtcNow.AddMinutes(-2) : null,
                LastRecoveryAt = cause == "recovered" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null
            });
        var result = await fixture.GetAsync();
        var network = result.GetProperty("network");
        Assert.True(network.GetProperty("isHealthy").GetBoolean());
        Assert.False(network.GetProperty("statisticsComplete").GetBoolean());
    }

    [Fact]
    public async Task PersistenceFailureClearsOnlyAfterARecordedSuccess()
    {
        var failure = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var failing = new HealthFixture(HealthyAggregation(failure: failure));
        var degraded = await failing.GetAsync();
        Assert.Equal("degraded", degraded.GetProperty("status").GetString());
        Assert.False(degraded.GetProperty("network").GetProperty("aggregation").GetProperty("persistenceHealthy").GetBoolean());

        using var recovered = new HealthFixture(HealthyAggregation(failure: failure, success: failure.AddSeconds(10)));
        var healthy = await recovered.GetAsync();
        Assert.Equal("ok", healthy.GetProperty("status").GetString());
    }

    [Fact]
    public async Task FullQueueDegradesHealthBeforeUnboundedBacklog()
    {
        using var fixture = new HealthFixture(new TrafficAggregatorDiagnosticsSnapshot
        {
            EventQueueCapacity = 16,
            PendingEventCount = 16,
            BucketCapacity = 100,
            IsAccepting = true,
            IsProcessorRunning = true
        });
        var result = await fixture.GetAsync();
        Assert.Equal("degraded", result.GetProperty("status").GetString());
        Assert.Equal(16, result.GetProperty("network").GetProperty("aggregation").GetProperty("backlogThreshold").GetInt64());
    }

    [Fact]
    public async Task CollectorRecoveryIsExplicitlyVisible()
    {
        using var fixture = new HealthFixture(collector: new NetworkCollectorDiagnosticsSnapshot
        {
            IsRunning = false,
            IsRecovering = true,
            ConsecutiveFailures = 1,
            LastFailureAt = DateTimeOffset.UtcNow
        });
        var result = await fixture.GetAsync();
        var network = result.GetProperty("network");
        Assert.Equal("degraded", result.GetProperty("status").GetString());
        Assert.True(network.GetProperty("isRecovering").GetBoolean());
        Assert.False(network.GetProperty("statisticsComplete").GetBoolean());
    }

    private static TrafficAggregatorDiagnosticsSnapshot HealthyAggregation(
        long rejected = 0,
        long processingFailures = 0,
        DateTimeOffset? failure = null,
        DateTimeOffset? success = null) => new()
        {
            EventQueueCapacity = 128,
            BucketCapacity = 256,
            IsAccepting = true,
            IsProcessorRunning = true,
            RejectedEvents = rejected,
            ProcessingFailures = processingFailures,
            PersistenceFailures = failure.HasValue ? 1 : 0,
            LastPersistenceFailureAt = failure,
            LastPersistenceSuccessAt = success
        };

    private sealed class HealthFixture : IDisposable
    {
        private readonly TestServer _server;
        private readonly HttpClient _client;

        public HealthFixture(TrafficAggregatorDiagnosticsSnapshot? aggregation = null, NetworkCollectorDiagnosticsSnapshot? collector = null)
        {
            var collectorStub = new CollectorStub(collector ?? new NetworkCollectorDiagnosticsSnapshot { IsRunning = true });
            _server = new TestServer(new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IHardwareSnapshotBuffer, HardwareBufferStub>();
                    services.AddSingleton<IMonitorSettingsProvider, SettingsStub>();
                    services.AddSingleton<INetworkAggregator>(new AggregatorStub(aggregation ?? HealthyAggregation()));
                    services.AddSingleton<INetworkCollector>(collectorStub);
                    services.AddSingleton<INetworkCollectorDiagnostics>(collectorStub);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapHealthEndpoints());
                }));
            _client = _server.CreateClient();
        }

        public Task<JsonElement> GetAsync() => _client.GetFromJsonAsync<JsonElement>("/health");

        public void Dispose()
        {
            _client.Dispose();
            _server.Dispose();
        }
    }

    private sealed class AggregatorStub(TrafficAggregatorDiagnosticsSnapshot snapshot) : INetworkAggregator
    {
        public long PendingEventCount => snapshot.PendingEventCount;
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IReadOnlyList<TrafficBucket> PeekPendingBuckets(int maxCount) => [];
        public void ConfirmPendingBuckets(int count) { }
        public TrafficAggregatorDiagnosticsSnapshot GetDiagnostics() => snapshot;
        public void RecordPersistenceFailure() { }
        public void RecordPersistenceSuccess() { }
    }

    private sealed class CollectorStub(NetworkCollectorDiagnosticsSnapshot snapshot) : INetworkCollector, INetworkCollectorDiagnostics
    {
        public event Action<NetworkTraceEvent>? EventReceived { add { } remove { } }
        public bool IsRunning => snapshot.IsRunning;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public NetworkCollectorDiagnosticsSnapshot GetSnapshot() => snapshot;
    }

    private sealed class HardwareBufferStub : IHardwareSnapshotBuffer
    {
        public int PendingCount => 0;
        public HardwareSnapshot GetLatest() => new() { SampleTime = DateTimeOffset.UtcNow };
        public IReadOnlyList<HardwareSnapshot> GetRecent(int maxCount) => [];
        public void Add(HardwareSnapshot snapshot) { }
        public IReadOnlyList<HardwareSnapshot> DequeuePendingBatch(int maxCount) => [];
        public void RequeuePendingBatch(IReadOnlyCollection<HardwareSnapshot> snapshots) { }
    }

    private sealed class SettingsStub : IMonitorSettingsProvider
    {
        public MonitorSettings Current { get; } = new();
        public MonitorSettings Update(AppSettingsDto settings) => Current;
        public IDisposable RegisterChangeCallback(Action<MonitorSettings> callback) => new EmptyRegistration();
    }

    private sealed class EmptyRegistration : IDisposable
    {
        public void Dispose() { }
    }
}
