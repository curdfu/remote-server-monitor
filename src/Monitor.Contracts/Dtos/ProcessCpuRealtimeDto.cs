namespace Monitor.Contracts.Dtos;

public sealed class ProcessCpuRealtimeDto
{
    public DateTimeOffset? SampleTime { get; init; }
    public bool IsReady { get; init; }
    public IReadOnlyList<ProcessCpuUsageDto> Processes { get; init; } = Array.Empty<ProcessCpuUsageDto>();
}
