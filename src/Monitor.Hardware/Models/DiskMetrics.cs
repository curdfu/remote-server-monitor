namespace Monitor.Hardware.Models;

public sealed class DiskMetrics
{
    public int MonitoredDiskCount { get; init; }
    public double? TemperatureC { get; init; }
    public string? TemperatureSource { get; init; }
    public IReadOnlyList<DiskDriveMetrics> Drives { get; init; } = Array.Empty<DiskDriveMetrics>();
}
