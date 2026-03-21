namespace Monitor.Network.Models;

public sealed class ResolvedProcessInfo
{
    public int ProcessId { get; init; }
    public string AppKey { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? ExecutablePath { get; init; }
    public DateTimeOffset ResolvedAt { get; init; }
}
