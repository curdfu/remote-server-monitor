namespace Monitor.Network.Models;

public sealed class NetworkRealtimeSnapshot
{
    public DateTimeOffset SampleTime { get; init; } = DateTimeOffset.UtcNow;
    public double TotalUploadBytesPerSecond { get; init; }
    public double TotalDownloadBytesPerSecond { get; init; }
    public double WanUploadBytesPerSecond { get; init; }
    public double WanDownloadBytesPerSecond { get; init; }
    public double LanUploadBytesPerSecond { get; init; }
    public double LanDownloadBytesPerSecond { get; init; }
}
