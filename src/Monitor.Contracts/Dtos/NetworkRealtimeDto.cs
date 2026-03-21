namespace Monitor.Contracts.Dtos;

public sealed class NetworkRealtimeDto
{
    public DateTimeOffset SampleTime { get; init; }
    public double TotalUploadBytesPerSecond { get; init; }
    public double TotalDownloadBytesPerSecond { get; init; }
    public double WanUploadBytesPerSecond { get; init; }
    public double WanDownloadBytesPerSecond { get; init; }
    public double LanUploadBytesPerSecond { get; init; }
    public double LanDownloadBytesPerSecond { get; init; }
}
