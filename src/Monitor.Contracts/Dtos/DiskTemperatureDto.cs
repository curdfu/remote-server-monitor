namespace Monitor.Contracts.Dtos;

public sealed class DiskTemperatureDto
{
    public string Name { get; init; } = string.Empty;
    public long? SizeBytes { get; init; }
    public long? UsedBytes { get; init; }
    public double? TemperatureC { get; init; }
    public string? TemperatureSource { get; init; }
}
