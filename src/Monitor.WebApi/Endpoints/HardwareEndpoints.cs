using Monitor.Contracts.Dtos;
using Monitor.Hardware.Abstractions;
using Monitor.Storage.Repositories;

namespace Monitor.WebApi.Endpoints;

public static class HardwareEndpoints
{
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
            var (rangeFrom, rangeTo) = NormalizeRange(from, to, TimeSpan.FromHours(1));
            if (rangeFrom >= rangeTo)
            {
                return Results.BadRequest(new { message = "'from' must be earlier than 'to'." });
            }

            var snapshots = await hardwareRepository.QueryRangeAsync(rangeFrom, rangeTo, cancellationToken);
            return Results.Ok(snapshots.Select(ToRealtimeDto).ToArray());
        });

        return app;
    }

    private static HardwareRealtimeDto ToRealtimeDto(Monitor.Hardware.Models.HardwareSnapshot snapshot)
    {
        return new HardwareRealtimeDto
        {
            SampleTime = snapshot.SampleTime,
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

    private static (DateTimeOffset From, DateTimeOffset To) NormalizeRange(
        DateTimeOffset? from,
        DateTimeOffset? to,
        TimeSpan defaultWindow)
    {
        var rangeTo = to ?? DateTimeOffset.UtcNow;
        var rangeFrom = from ?? rangeTo.Subtract(defaultWindow);
        return (rangeFrom, rangeTo);
    }
}
