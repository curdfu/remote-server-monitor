using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Monitor.Hardware.Abstractions;
using Monitor.Network.Abstractions;
using Monitor.WebApi.Hubs;

namespace Monitor.WebApi.Services;

public sealed class MonitorRealtimeBroadcaster(
    IHardwareSnapshotBuffer hardwareSnapshotBuffer,
    INetworkAggregator networkAggregator,
    IHubContext<MonitorHub> hubContext,
    IOptionsMonitor<MonitorSettings> settings,
    ILogger<MonitorRealtimeBroadcaster> logger)
{
    public async Task BroadcastOnceAsync(CancellationToken cancellationToken = default)
    {
        var hardware = hardwareSnapshotBuffer.GetLatest();
        var network = networkAggregator.GetLatestRealtimeSnapshot();
        if (hardware is null || network is null)
        {
            logger.LogDebug("Skip realtime broadcast because cached payload is not ready yet.");
            return;
        }

        var topApps = networkAggregator.GetLatestTopApps(settings.CurrentValue.TopNDefault);

        var hardwareDto = new HardwareRealtimeDto
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
        };

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

        var topAppsDto = topApps.Select(x => new AppTrafficItemDto
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
        }).ToArray();

        await hubContext.Clients.All.SendAsync(MonitorHubEvents.HardwareRealtime, hardwareDto, cancellationToken);
        await hubContext.Clients.All.SendAsync(MonitorHubEvents.NetworkRealtime, networkDto, cancellationToken);
        await hubContext.Clients.All.SendAsync(MonitorHubEvents.TopAppsRealtime, topAppsDto, cancellationToken);

        logger.LogDebug(
            "Broadcasted realtime hub payloads. hardware={HardwareSampleTime}, network={NetworkSampleTime}, topApps={TopAppsCount}.",
            hardwareDto.SampleTime,
            networkDto.SampleTime,
            topAppsDto.Length);
    }
}
