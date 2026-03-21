namespace Monitor.Contracts.Dtos;

public sealed class AppTrafficItemDto
{
    public string AppKey { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public double UploadBytesPerSecond { get; init; }
    public double DownloadBytesPerSecond { get; init; }
    public double WanUploadBytesPerSecond { get; init; }
    public double WanDownloadBytesPerSecond { get; init; }
    public double LanUploadBytesPerSecond { get; init; }
    public double LanDownloadBytesPerSecond { get; init; }
}
