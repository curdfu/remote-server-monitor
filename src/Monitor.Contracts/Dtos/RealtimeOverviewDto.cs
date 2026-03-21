namespace Monitor.Contracts.Dtos;

public sealed class RealtimeOverviewDto
{
    public HardwareRealtimeDto Hardware { get; init; } = new();
    public NetworkRealtimeDto Network { get; init; } = new();
    public IReadOnlyList<AppTrafficItemDto> TopApps { get; init; } = Array.Empty<AppTrafficItemDto>();
}
