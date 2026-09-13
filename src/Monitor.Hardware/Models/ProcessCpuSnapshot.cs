namespace Monitor.Hardware.Models;

public sealed class ProcessCpuSnapshot
{
    public DateTimeOffset SampleTime { get; init; }
    public bool IsReady { get; init; }
    public IReadOnlyList<ProcessCpuUsage> Processes { get; init; } = Array.Empty<ProcessCpuUsage>();
}
