using Monitor.Contracts.Dtos;
using Monitor.Hardware.Abstractions;
using Monitor.Storage.Repositories;

namespace Monitor.WebApi.Endpoints;

public static class HardwareEndpoints
{
    private const int MaxHistoryPoints = 2000;

    public static IEndpointRouteBuilder MapHardwareEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/hardware/realtime", (
            IHardwareSnapshotBuffer hardwareSnapshotBuffer) =>
        {
            var snapshot = hardwareSnapshotBuffer.GetLatest();
            if (snapshot is null)
            {
                return Results.Problem(
                    detail: "Hardware snapshot cache is not ready yet.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(ToRealtimeDto(snapshot));
        });

        app.MapGet("/api/hardware/history", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            HardwareRepository hardwareRepository,
            CancellationToken cancellationToken) =>
        {
            if (!HistoryRange.TryNormalize(from, to, TimeSpan.FromHours(1), out var rangeFrom, out var rangeTo, out var error))
            {
                return Results.BadRequest(new { message = error });
            }

            var snapshots = await hardwareRepository.QueryRangeAsync(
                rangeFrom,
                rangeTo,
                MaxHistoryPoints,
                cancellationToken);
            return Results.Ok(snapshots.Select(ToRealtimeDto).ToArray());
        });

        return app;
    }

    private static HardwareRealtimeDto ToRealtimeDto(Monitor.Hardware.Models.HardwareSnapshot snapshot)
    {
        return new HardwareRealtimeDto
        {
            SampleTime = snapshot.SampleTime,
            CpuName = snapshot.Cpu.Name,
            CpuUsagePercent = snapshot.Cpu.UsagePercent,
            CpuTemperatureC = snapshot.Cpu.TemperatureC,
            CpuFrequencyMhz = snapshot.Cpu.FrequencyMhz,
            CpuPowerWatts = snapshot.Cpu.PowerWatts,
            MemoryTotalMb = snapshot.Memory.TotalMb,
            MemoryUsedMb = snapshot.Memory.UsedMb,
            MemoryUsagePercent = snapshot.Memory.UsagePercent,
            DiskTemperatureC = snapshot.Disk.TemperatureC,
            Disks = snapshot.Disk.Drives.Select(drive => new DiskTemperatureDto
            {
                Name = drive.Name,
                SizeBytes = drive.SizeBytes,
                TemperatureC = drive.TemperatureC,
                TemperatureSource = drive.TemperatureSource
            }).ToArray(),
            DiskSpaces = Array.Empty<DiskSpaceDto>(),
            UptimeSeconds = snapshot.System.UptimeSeconds
        };
    }

}
