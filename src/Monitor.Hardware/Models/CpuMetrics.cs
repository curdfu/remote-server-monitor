namespace Monitor.Hardware.Models;

public sealed class CpuMetrics
{
    public string? Name { get; init; }
    public double? UsagePercent { get; init; }
    public double? TemperatureC { get; init; }
    public string? TemperatureSource { get; init; }
    public double? FrequencyMhz { get; init; }
    public string? FrequencySource { get; init; }
}
