using Monitor.Contracts.Options;
using Monitor.Hardware.Abstractions;
using Monitor.Network.Abstractions;

namespace Monitor.WebApi.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (
            IHardwareSnapshotBuffer hardwareSnapshotBuffer,
            INetworkAggregator networkAggregator,
            INetworkCollector networkCollector,
            INetworkCollectorDiagnostics networkCollectorDiagnostics,
            IMonitorSettingsProvider settings) =>
        {
            const long networkAggregationBacklogThreshold = 100_000;
            var hardware = hardwareSnapshotBuffer.GetLatest();
            var utcNow = DateTimeOffset.UtcNow;
            var collectorDiagnostics = networkCollectorDiagnostics.GetSnapshot();
            var aggregationDiagnostics = networkAggregator.GetDiagnostics();
            var currentSettings = settings.Current;
            var hardwareAgeSeconds = hardware is null
                ? (double?)null
                : (utcNow - hardware.SampleTime).TotalSeconds;
            var hardwareThresholdSeconds = Math.Max(currentSettings.HardwareSampleIntervalMs / 1000d * 3d, 5d);
            var sessionObservedEvents = collectorDiagnostics.PublishedEvents + collectorDiagnostics.LostEvents;
            var sessionLossRate = sessionObservedEvents > 0
                ? collectorDiagnostics.LostEvents * 100d / sessionObservedEvents
                : 0d;
            var totalObservedEvents = collectorDiagnostics.TotalPublishedEvents + collectorDiagnostics.TotalLostEvents;
            var totalLossRate = totalObservedEvents > 0
                ? collectorDiagnostics.TotalLostEvents * 100d / totalObservedEvents
                : 0d;

            var hardwareHealthy = hardwareAgeSeconds.HasValue && hardwareAgeSeconds.Value is >= 0 &&
                                  hardwareAgeSeconds.Value <= hardwareThresholdSeconds;
            var persistenceHealthy = !aggregationDiagnostics.LastPersistenceFailureAt.HasValue ||
                                     aggregationDiagnostics.LastPersistenceSuccessAt >= aggregationDiagnostics.LastPersistenceFailureAt;
            // Recovery does not erase earlier gaps; current availability and historical completeness are separate.
            var statisticsComplete = collectorDiagnostics.TotalLostEvents == 0 &&
                                     collectorDiagnostics.LastFailureAt is null &&
                                     aggregationDiagnostics.RejectedEvents == 0 &&
                                     aggregationDiagnostics.ProcessingFailures == 0;
            var backlogThreshold = Math.Min(networkAggregationBacklogThreshold, aggregationDiagnostics.EventQueueCapacity);
            var networkHealthy = networkCollector.IsRunning &&
                                 !collectorDiagnostics.IsRecovering &&
                                 aggregationDiagnostics.IsAccepting &&
                                 aggregationDiagnostics.IsProcessorRunning &&
                                 persistenceHealthy &&
                                 aggregationDiagnostics.ActiveBucketCount + aggregationDiagnostics.PendingBucketCount < aggregationDiagnostics.BucketCapacity &&
                                 aggregationDiagnostics.PendingEventCount < backlogThreshold;

            return Results.Ok(new
            {
                status = hardwareHealthy && networkHealthy ? "ok" : "degraded",
                utcNow,
                hardware = new
                {
                    latestSampleTime = hardware?.SampleTime,
                    sampleAgeSeconds = hardwareAgeSeconds,
                    thresholdSeconds = hardwareThresholdSeconds,
                    isHealthy = hardwareHealthy
                },
                network = new
                {
                    collectorRunning = networkCollector.IsRunning,
                    isHealthy = networkHealthy,
                    statisticsComplete,
                    collectorDiagnostics.IsRecovering,
                    aggregation = new
                    {
                        aggregationDiagnostics.PendingEventCount,
                        backlogThreshold,
                        aggregationDiagnostics.EventQueueCapacity,
                        aggregationDiagnostics.BucketCapacity,
                        aggregationDiagnostics.ActiveBucketCount,
                        aggregationDiagnostics.PendingBucketCount,
                        aggregationDiagnostics.RejectedEvents,
                        aggregationDiagnostics.RejectedBytes,
                        aggregationDiagnostics.QueueFullRejectedEvents,
                        aggregationDiagnostics.BucketCapacityRejectedEvents,
                        aggregationDiagnostics.FirstRejectedAt,
                        aggregationDiagnostics.LastRejectedAt,
                        aggregationDiagnostics.ProcessingFailures,
                        aggregationDiagnostics.PersistenceFailures,
                        aggregationDiagnostics.LastPersistenceFailureAt,
                        aggregationDiagnostics.LastPersistenceSuccessAt,
                        aggregationDiagnostics.IsAccepting,
                        aggregationDiagnostics.IsProcessorRunning,
                        persistenceHealthy
                    },
                    etw = new
                    {
                        collectorDiagnostics.SessionName,
                        collectorDiagnostics.StartedAt,
                        collectorDiagnostics.BufferSizeMb,
                        collectorDiagnostics.AdaptiveRestartCount,
                        collectorDiagnostics.IsRecovering,
                        collectorDiagnostics.IsStopping,
                        collectorDiagnostics.ConsecutiveFailures,
                        collectorDiagnostics.LastFailureAt,
                        collectorDiagnostics.LastRecoveryAt,
                        sessionPublishedEvents = collectorDiagnostics.PublishedEvents,
                        sessionLostEvents = collectorDiagnostics.LostEvents,
                        sessionLossRate,
                        totalPublishedEvents = collectorDiagnostics.TotalPublishedEvents,
                        totalLostEvents = collectorDiagnostics.TotalLostEvents,
                        totalLossRate
                    }
                }
            });
        });
        return app;
    }
}
