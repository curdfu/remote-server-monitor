using Microsoft.AspNetCore.SignalR;
using Monitor.Contracts.Dtos;
using Monitor.Hardware.Abstractions;
using Monitor.WebApi.Hubs;

namespace Monitor.WebApi.Services;

public sealed class MonitorRealtimeBroadcaster(
    IHardwareSnapshotBuffer hardwareSnapshotBuffer,
    IDiskUsageProvider diskUsageProvider,
    IHubContext<MonitorHub> hubContext,
    ILogger<MonitorRealtimeBroadcaster> logger)
{
    private readonly object _syncRoot = new();
    private DateTimeOffset _lastBroadcastSampleTime = DateTimeOffset.MinValue;

    public async Task BroadcastOnceAsync(CancellationToken cancellationToken = default)
    {
        var hardware = hardwareSnapshotBuffer.GetLatest();
        if (hardware is null)
        {
            logger.LogDebug("Skip realtime broadcast because cached hardware payload is not ready yet.");
            return;
        }

        lock (_syncRoot)
        {
            if (hardware.SampleTime <= _lastBroadcastSampleTime)
            {
                return;
            }
        }

        var diskUsedBytes = diskUsageProvider.GetCurrentUsedBytesByDiskNumber(
            hardware.Disk.Drives
                .Where(drive => drive.DiskNumber.HasValue)
                .Select(drive => drive.DiskNumber!.Value));
        var diskSpaces = diskUsageProvider.GetCurrentDiskSpaces();

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
                SizeBytes = drive.SizeBytes,
                UsedBytes = drive.DiskNumber.HasValue &&
                            diskUsedBytes.TryGetValue(drive.DiskNumber.Value, out var usedBytes)
                    ? usedBytes
                    : null,
                TemperatureC = drive.TemperatureC,
                TemperatureSource = drive.TemperatureSource
            }).ToArray(),
            DiskSpaces = diskSpaces.Select(space => new DiskSpaceDto
            {
                Name = space.Name,
                TotalBytes = space.TotalBytes,
                UsedBytes = space.UsedBytes,
                FreeBytes = space.FreeBytes
            }).ToArray(),
            UptimeSeconds = hardware.System.UptimeSeconds
        };

        await hubContext.Clients.All.SendAsync(MonitorHubEvents.HardwareRealtime, hardwareDto, cancellationToken);

        lock (_syncRoot)
        {
            _lastBroadcastSampleTime = hardware.SampleTime;
        }

        logger.LogDebug(
            "Broadcasted hardware realtime hub payload. hardware={HardwareSampleTime}.",
            hardwareDto.SampleTime);
    }
}
