namespace Monitor.Contracts.Dtos;

public sealed class RealtimeOverviewDto
{
    public HardwareRealtimeDto Hardware { get; init; } = new();
    public ProcessCpuRealtimeDto ProcessCpu { get; init; } = new();
}
