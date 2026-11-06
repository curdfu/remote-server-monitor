namespace Monitor.Network.Models;

public sealed class NetworkCollectorDiagnosticsSnapshot
{
    public bool IsRunning { get; init; }
    public string? SessionName { get; init; }
    public int BufferSizeMb { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public long PublishedEvents { get; init; }
    public long LostEvents { get; init; }
    public long TotalPublishedEvents { get; init; }
    public long TotalLostEvents { get; init; }
    public int AdaptiveRestartCount { get; init; }
    public bool IsRecovering { get; init; }
    public bool IsStopping { get; init; }
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public DateTimeOffset? LastRecoveryAt { get; init; }
}
