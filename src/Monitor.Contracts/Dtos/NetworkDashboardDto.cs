namespace Monitor.Contracts.Dtos;

public sealed class NetworkDashboardDto
{
    public AppTrafficSummaryDto[] Apps { get; init; } = [];
    public IgnoredNetworkAppDto[] IgnoredApps { get; init; } = [];
    public NetworkPeriodSummaryDto Overview { get; init; } = new();
    public NetworkPeriodSummaryDto Totals { get; init; } = new();
}
