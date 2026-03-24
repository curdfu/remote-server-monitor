namespace Monitor.Contracts.Dtos;

public sealed class HardwareRealtimeDto
{
    public DateTimeOffset SampleTime { get; init; }
    public string? CpuName { get; init; }
    public double? CpuUsagePercent { get; init; }
    public double? CpuTemperatureC { get; init; }
    public double? CpuFrequencyMhz { get; init; }
    public double? CpuPowerWatts { get; init; }
    public double? MemoryTotalMb { get; init; }
    public double? MemoryUsedMb { get; init; }
    public double? MemoryUsagePercent { get; init; }
    public double? DiskTemperatureC { get; init; }
    public IReadOnlyList<DiskTemperatureDto> Disks { get; init; } = Array.Empty<DiskTemperatureDto>();
    public IReadOnlyList<DiskSpaceDto> DiskSpaces { get; init; } = Array.Empty<DiskSpaceDto>();
    public long UptimeSeconds { get; init; }
}
