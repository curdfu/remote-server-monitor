using Microsoft.AspNetCore.SignalR;
using Monitor.Contracts.Dtos;
using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Models;
using Monitor.Network.Abstractions;
using Monitor.Network.Models;
using Monitor.WebApi.Hubs;

namespace Monitor.WebApi.Services;

// 广播器只消费采集服务已经写入内存的最新快照，不触发额外硬件或网络采样。
public sealed class MonitorRealtimeBroadcaster(
    IHardwareSnapshotBuffer hardwareSnapshotBuffer,
    IHardwareMonitoringDemand hardwareMonitoringDemand,
    DiskUsageSnapshotCache diskUsageSnapshotCache,
    INetworkAggregator networkAggregator,
    IHubContext<MonitorHub> hubContext,
    ILogger<MonitorRealtimeBroadcaster> logger)
{
    private readonly object _syncRoot = new();
    private DateTimeOffset _lastHardwareSampleTime = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNetworkSampleTime = DateTimeOffset.MinValue;

    public async Task BroadcastOnceAsync(CancellationToken cancellationToken = default)
    {
        var hardware = hardwareSnapshotBuffer.GetLatest();
        var network = networkAggregator.GetLatestRealtimeSnapshot();
        var broadcastTasks = new List<Task>(2);

        if (hardwareMonitoringDemand.HasHardwareSubscribers &&
            hardware is not null &&
            ShouldBroadcastHardware(hardware.SampleTime))
        {
            broadcastTasks.Add(BroadcastHardwareAsync(hardware, cancellationToken));
        }

        if (network is not null && ShouldBroadcastNetwork(network.SampleTime))
        {
            broadcastTasks.Add(BroadcastNetworkAsync(network, cancellationToken));
        }

        if (broadcastTasks.Count > 0)
        {
            await Task.WhenAll(broadcastTasks);
        }
    }

    private async Task BroadcastHardwareAsync(
        HardwareSnapshot hardware,
        CancellationToken cancellationToken)
    {
        var diskUsageSnapshot = diskUsageSnapshotCache.GetSnapshot(
            hardware.Disk.Drives
                .Where(drive => drive.DiskNumber.HasValue)
                .Select(drive => drive.DiskNumber!.Value));

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
        };

        await hubContext.Clients
            .Group(MonitorHub.HardwareGroup)
            .SendAsync(MonitorHubEvents.HardwareRealtime, hardwareDto, cancellationToken);

        lock (_syncRoot)
        {
            _lastHardwareSampleTime = hardware.SampleTime;
        }

        logger.LogDebug(
            "Broadcasted hardware realtime payload. sampleTime={SampleTime}.",
            hardware.SampleTime);
    }

    private async Task BroadcastNetworkAsync(
        NetworkRealtimeSnapshot network,
        CancellationToken cancellationToken)
    {
        var networkDto = new NetworkRealtimeDto
        {
            SampleTime = network.SampleTime,
            TotalUploadBytesPerSecond = network.TotalUploadBytesPerSecond,
            TotalDownloadBytesPerSecond = network.TotalDownloadBytesPerSecond,
            WanUploadBytesPerSecond = network.WanUploadBytesPerSecond,
            WanDownloadBytesPerSecond = network.WanDownloadBytesPerSecond,
            LanUploadBytesPerSecond = network.LanUploadBytesPerSecond,
            LanDownloadBytesPerSecond = network.LanDownloadBytesPerSecond
        };

        await hubContext.Clients
            .Group(MonitorHub.NetworkGroup)
            .SendAsync(MonitorHubEvents.NetworkRealtime, networkDto, cancellationToken);

        lock (_syncRoot)
        {
            _lastNetworkSampleTime = network.SampleTime;
        }

        logger.LogDebug(
            "Broadcasted network realtime payload. sampleTime={SampleTime}.",
            network.SampleTime);
    }

    private bool ShouldBroadcastHardware(DateTimeOffset sampleTime)
    {
        lock (_syncRoot)
        {
            return sampleTime > _lastHardwareSampleTime;
        }
    }

    private bool ShouldBroadcastNetwork(DateTimeOffset sampleTime)
    {
        lock (_syncRoot)
        {
            return sampleTime > _lastNetworkSampleTime;
        }
    }
}
