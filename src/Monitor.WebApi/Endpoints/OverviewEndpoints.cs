using Microsoft.Extensions.Options;
using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Monitor.Hardware.Abstractions;
using Monitor.Network.Abstractions;

namespace Monitor.WebApi.Endpoints;

public static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/overview", async (
            IHardwareCollector hardwareCollector,
            INetworkAggregator networkAggregator,
            IOptionsMonitor<MonitorSettings> settings,
            CancellationToken cancellationToken) =>
        {
            var hardware = await hardwareCollector.GetCurrentSnapshotAsync(cancellationToken);
            var network = await networkAggregator.GetRealtimeSnapshotAsync(cancellationToken);
            var topApps = await networkAggregator.GetTopAppsAsync(settings.CurrentValue.TopNDefault, cancellationToken);

            return Results.Ok(new RealtimeOverviewDto
            {
                Hardware = new HardwareRealtimeDto
                {
                    SampleTime = hardware.SampleTime,
                    CpuUsagePercent = hardware.Cpu.UsagePercent,
                    CpuTemperatureC = hardware.Cpu.TemperatureC,
                    CpuFrequencyMhz = hardware.Cpu.FrequencyMhz,
                    MemoryTotalMb = hardware.Memory.TotalMb,
                    MemoryUsedMb = hardware.Memory.UsedMb,
                    MemoryUsagePercent = hardware.Memory.UsagePercent,
                    DiskTemperatureC = hardware.Disk.TemperatureC,
                    UptimeSeconds = hardware.System.UptimeSeconds
                },
                Network = new NetworkRealtimeDto
                {
                    SampleTime = network.SampleTime,
                    TotalUploadBytesPerSecond = network.TotalUploadBytesPerSecond,
                    TotalDownloadBytesPerSecond = network.TotalDownloadBytesPerSecond,
                    WanUploadBytesPerSecond = network.WanUploadBytesPerSecond,
                    WanDownloadBytesPerSecond = network.WanDownloadBytesPerSecond,
                    LanUploadBytesPerSecond = network.LanUploadBytesPerSecond,
                    LanDownloadBytesPerSecond = network.LanDownloadBytesPerSecond
                },
                TopApps = topApps.Select(x => new AppTrafficItemDto
                {
                    AppKey = x.AppKey,
                    ProcessName = x.ProcessName,
                    DisplayName = x.DisplayName,
                    UploadBytesPerSecond = x.UploadBytesPerSecond,
                    DownloadBytesPerSecond = x.DownloadBytesPerSecond,
                    WanUploadBytesPerSecond = x.WanUploadBytesPerSecond,
                    WanDownloadBytesPerSecond = x.WanDownloadBytesPerSecond,
                    LanUploadBytesPerSecond = x.LanUploadBytesPerSecond,
                    LanDownloadBytesPerSecond = x.LanDownloadBytesPerSecond
                }).ToArray()
            });
        });

        return app;
    }
}
