namespace Monitor.Hardware.Models;

public sealed class SystemMetrics
{
    public DateTimeOffset BootTime { get; init; }
    public long UptimeSeconds { get; init; }
}
