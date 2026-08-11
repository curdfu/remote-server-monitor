namespace Monitor.Contracts.Dtos;

public sealed class RealtimeOverviewDto
{
    public HardwareRealtimeDto Hardware { get; init; } = new();
}
