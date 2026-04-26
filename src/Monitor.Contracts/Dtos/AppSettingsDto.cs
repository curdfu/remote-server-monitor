namespace Monitor.Contracts.Dtos;

public sealed class AppSettingsDto
{
    public int HttpPort { get; init; }
    public int HardwareSampleIntervalMs { get; init; }
    public int AggregateIntervalSeconds { get; init; }
    public int HistoryRetentionDays { get; init; }
    public int TopNDefault { get; init; }
}
