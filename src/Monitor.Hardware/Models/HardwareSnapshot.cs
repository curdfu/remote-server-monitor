namespace Monitor.Hardware.Models;

public sealed class HardwareSnapshot
{
    public string CollectorName { get; init; } = "LibreHardwareMonitor";
    public DateTimeOffset SampleTime { get; init; } = DateTimeOffset.UtcNow;
    public bool IsPartial { get; init; }
    public CpuMetrics Cpu { get; init; } = new();
    public MemoryMetrics Memory { get; init; } = new();
    public DiskMetrics Disk { get; init; } = new();
    public SystemMetrics System { get; init; } = new();
}
