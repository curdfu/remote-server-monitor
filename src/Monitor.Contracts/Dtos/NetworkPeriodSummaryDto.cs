namespace Monitor.Contracts.Dtos;

public sealed class NetworkPeriodSummaryDto
{
    public long TotalUploadBytes { get; init; }
    public long TotalDownloadBytes { get; init; }
    public long WanUploadBytes { get; init; }
    public long WanDownloadBytes { get; init; }
    public long LanUploadBytes { get; init; }
    public long LanDownloadBytes { get; init; }
    public long LoopbackUploadBytes { get; init; }
    public long LoopbackDownloadBytes { get; init; }
    public long OtherUploadBytes { get; init; }
    public long OtherDownloadBytes { get; init; }
}
