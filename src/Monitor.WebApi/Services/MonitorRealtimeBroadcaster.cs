using Microsoft.AspNetCore.SignalR;
using Monitor.Contracts.Dtos;
using Monitor.Hardware.Abstractions;
using Monitor.WebApi.Hubs;

namespace Monitor.WebApi.Services;

public sealed class MonitorRealtimeBroadcaster(
    IHardwareSnapshotBuffer hardwareSnapshotBuffer,
    IHubContext<MonitorHub> hubContext,
    ILogger<MonitorRealtimeBroadcaster> logger)
{
    public async Task BroadcastOnceAsync(CancellationToken cancellationToken = default)
    {
        var hardware = hardwareSnapshotBuffer.GetLatest();
        if (hardware is null)
        {
            logger.LogDebug("Skip realtime broadcast because cached hardware payload is not ready yet.");
            return;
        }

        var hardwareDto = new HardwareRealtimeDto
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
        };

        await hubContext.Clients.All.SendAsync(MonitorHubEvents.HardwareRealtime, hardwareDto, cancellationToken);

        logger.LogDebug(
            "Broadcasted hardware realtime hub payload. hardware={HardwareSampleTime}.",
            hardwareDto.SampleTime);
    }
}
