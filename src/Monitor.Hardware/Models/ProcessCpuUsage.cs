namespace Monitor.Hardware.Models;

public sealed class ProcessCpuUsage
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public double CpuUsagePercent { get; init; }
}
