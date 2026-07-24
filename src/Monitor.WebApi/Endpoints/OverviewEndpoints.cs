using Monitor.Contracts.Dtos;
using Monitor.Hardware.Abstractions;
using Monitor.Network.Abstractions;
using Monitor.WebApi.Services;

namespace Monitor.WebApi.Endpoints;

public static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/overview", (
            IHardwareSnapshotBuffer hardwareSnapshotBuffer,
            DiskUsageSnapshotCache diskUsageSnapshotCache,
            INetworkAggregator networkAggregator) =>
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

            var diskUsageSnapshot = diskUsageSnapshotCache.GetSnapshot(
                hardware.Disk.Drives
                    .Where(drive => drive.DiskNumber.HasValue)
                    .Select(drive => drive.DiskNumber!.Value));

            return Results.Ok(new RealtimeOverviewDto
            {
                Hardware = new HardwareRealtimeDto
                {
                    SampleTime = hardware.SampleTime,
                    CpuName = hardware.Cpu.Name,
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
                        SizeBytes = drive.SizeBytes,
                        UsedBytes = drive.DiskNumber.HasValue &&
                                    diskUsageSnapshot.UsedBytesByDiskNumber.TryGetValue(
                                        drive.DiskNumber.Value,
                                        out var usedBytes)
                            ? usedBytes
                            : null,
                        TemperatureC = drive.TemperatureC,
                        TemperatureSource = drive.TemperatureSource
                    }).ToArray(),
                    DiskSpaces = diskUsageSnapshot.DiskSpaces.Select(space => new DiskSpaceDto
                    {
                        Name = space.Name,
                        TotalBytes = space.TotalBytes,
                        UsedBytes = space.UsedBytes,
                        FreeBytes = space.FreeBytes
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
                }
            });
        });

        return app;
    }
}
