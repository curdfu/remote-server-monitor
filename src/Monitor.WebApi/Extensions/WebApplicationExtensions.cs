using Microsoft.Extensions.Options;
using Monitor.Contracts.Options;
using Monitor.Hardware.Abstractions;
using Monitor.Network.Abstractions;
using Monitor.WebApi.Endpoints;
using Monitor.WebApi.Hubs;
using Monitor.WebApi.Middleware;

namespace Monitor.WebApi.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication MapMonitorWebApi(this WebApplication app)
    {
        var indexFilePath = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");
        if (File.Exists(indexFilePath))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        app.UseCors();

        app.UseMiddleware<GlobalExceptionMiddleware>();

        app.MapGet("/health", (
            IHardwareSnapshotBuffer hardwareSnapshotBuffer,
            INetworkAggregator networkAggregator,
            INetworkCollector networkCollector,
            IOptionsMonitor<MonitorSettings> settings) =>
        {
            var utcNow = DateTimeOffset.UtcNow;
            var hardware = hardwareSnapshotBuffer.GetLatest();
            var network = networkAggregator.GetLatestRealtimeSnapshot();

            var hardwareAgeSeconds = hardware is null
                ? (double?)null
                : (utcNow - hardware.SampleTime).TotalSeconds;
            var networkAgeSeconds = network is null
                ? (double?)null
                : (utcNow - network.SampleTime).TotalSeconds;

            var hardwareThresholdSeconds = Math.Max(settings.CurrentValue.HardwareSampleIntervalMs / 1000d * 3d, 5d);
            var networkThresholdSeconds = Math.Max(settings.CurrentValue.NetworkSampleIntervalMs / 1000d * 3d, 5d);

            var hardwareHealthy = hardwareAgeSeconds.HasValue && hardwareAgeSeconds.Value <= hardwareThresholdSeconds;
            var networkHealthy = networkCollector.IsRunning &&
                                 networkAgeSeconds.HasValue &&
                                 networkAgeSeconds.Value <= networkThresholdSeconds;

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
                    latestSampleTime = network?.SampleTime,
                    sampleAgeSeconds = networkAgeSeconds,
                    thresholdSeconds = networkThresholdSeconds,
                    isHealthy = networkHealthy
                }
            });
        });

        app.MapOverviewEndpoints();
        app.MapHardwareEndpoints();
        app.MapNetworkEndpoints();
        app.MapSettingsEndpoints();
        app.MapHub<MonitorHub>("/hubs/monitor");

        if (File.Exists(indexFilePath))
        {
            app.MapFallbackToFile("index.html");
        }

        return app;
    }
}
