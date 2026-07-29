namespace Monitor.Contracts.Dtos;

public sealed class IgnoredNetworkAppDto
{
    public string AppKey { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? ExecutablePath { get; init; }
}
