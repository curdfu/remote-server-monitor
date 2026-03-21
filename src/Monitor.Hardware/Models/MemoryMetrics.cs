namespace Monitor.Hardware.Models;

public sealed class MemoryMetrics
{
    public string? Name { get; init; }
    public double? TotalMb { get; init; }
    public double? UsedMb { get; init; }
    public double? AvailableMb { get; init; }
    public double? UsagePercent { get; init; }
}
