namespace Monitor.Hardware.Models;

public sealed class DiskDriveMetrics
{
    public string Name { get; init; } = string.Empty;
    public uint? DiskNumber { get; init; }
    public long? SizeBytes { get; init; }
    public double? TemperatureC { get; init; }
    public string? TemperatureSource { get; init; }
}
