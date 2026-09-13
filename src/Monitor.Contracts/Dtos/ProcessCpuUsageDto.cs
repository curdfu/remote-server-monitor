namespace Monitor.Contracts.Dtos;

public sealed class ProcessCpuUsageDto
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public double CpuUsagePercent { get; init; }
}
