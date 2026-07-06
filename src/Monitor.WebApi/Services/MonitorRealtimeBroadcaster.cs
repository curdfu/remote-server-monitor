using Microsoft.AspNetCore.SignalR;
using Monitor.Contracts.Dtos;
using Monitor.Hardware.Abstractions;
using Monitor.WebApi.Hubs;

namespace Monitor.WebApi.Services;

// Broadcaster 把内存中的最新硬件采样转换为前端实时 DTO，并通过 SignalR 推送给所有客户端。
// 它不主动采样硬件，只消费 CollectorHostedService 已写入 HardwareSnapshotBuffer 的最新快照。
public sealed class MonitorRealtimeBroadcaster(
    IHardwareSnapshotBuffer hardwareSnapshotBuffer,
    IDiskUsageProvider diskUsageProvider,
    IHubContext<MonitorHub> hubContext,
    ILogger<MonitorRealtimeBroadcaster> logger)
{
    private readonly object _syncRoot = new();
    // 用采样时间去重，避免推送周期比硬件采样周期短时重复发送同一份数据。
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

        // 温度数据来自硬件快照，磁盘空间可能需要额外查询 Win32 API，因此在组装 DTO 时按磁盘号补齐。
        var diskUsedBytes = diskUsageProvider.GetCurrentUsedBytesByDiskNumber(
            hardware.Disk.Drives
                .Where(drive => drive.DiskNumber.HasValue)
                .Select(drive => drive.DiskNumber!.Value));
        var diskSpaces = diskUsageProvider.GetCurrentDiskSpaces();

        var hardwareDto = new HardwareRealtimeDto
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

        // 只有发送成功后才更新游标；失败时下一轮仍可重试同一个采样点。
        lock (_syncRoot)
        {
            _lastBroadcastSampleTime = hardware.SampleTime;
        }

        logger.LogDebug(
            "Broadcasted hardware realtime hub payload. hardware={HardwareSampleTime}.",
            hardwareDto.SampleTime);
    }
}
