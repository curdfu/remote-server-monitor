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
        app.MapGet("/api/overview", (
            IHardwareSnapshotBuffer hardwareSnapshotBuffer,
            INetworkAggregator networkAggregator,
            IOptionsMonitor<MonitorSettings> settings) =>
        {
            var hardware = hardwareSnapshotBuffer.GetLatest();
            if (hardware is null)
            {
                return Results.Problem(
                    detail: "Hardware snapshot cache is not ready yet.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var network = networkAggregator.GetLatestRealtimeSnapshot();
            if (network is null)
            {
                return Results.Problem(
                    detail: "Network realtime cache is not ready yet.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var topApps = networkAggregator.GetLatestTopApps(settings.CurrentValue.TopNDefault);

            return Results.Ok(new RealtimeOverviewDto
            {
                Hardware = new HardwareRealtimeDto
                {
                    SampleTime = hardware.SampleTime,
                    CpuUsagePercent = hardware.Cpu.UsagePercent,
                    CpuTemperatureC = hardware.Cpu.TemperatureC,
                    CpuFrequencyMhz = hardware.Cpu.FrequencyMhz,
                    CpuPowerWatts = hardware.Cpu.PowerWatts,
                    MemoryTotalMb = hardware.Memory.TotalMb,
                    MemoryUsedMb = hardware.Memory.UsedMb,
                    MemoryUsagePercent = hardware.Memory.UsagePercent,
                    DiskTemperatureC = hardware.Disk.TemperatureC,
                    Disks = hardware.Disk.Drives.Select(drive => new DiskTemperatureDto
                    {
                        Name = drive.Name,
                        TemperatureC = drive.TemperatureC,
                        TemperatureSource = drive.TemperatureSource
                    }).ToArray(),
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
