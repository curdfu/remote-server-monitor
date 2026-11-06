namespace Monitor.Network.Models;

public sealed class TrafficAggregatorDiagnosticsSnapshot
{
    public int EventQueueCapacity { get; init; }
    public long PendingEventCount { get; init; }
    public int BucketCapacity { get; init; }
    public int ActiveBucketCount { get; init; }
    public int PendingBucketCount { get; init; }
    public long RejectedEvents { get; init; }
    public long RejectedBytes { get; init; }
    public long QueueFullRejectedEvents { get; init; }
    public long BucketCapacityRejectedEvents { get; init; }
    public DateTimeOffset? FirstRejectedAt { get; init; }
    public DateTimeOffset? LastRejectedAt { get; init; }
    public long ProcessingFailures { get; init; }
    public long PersistenceFailures { get; init; }
    public DateTimeOffset? LastPersistenceFailureAt { get; init; }
    public DateTimeOffset? LastPersistenceSuccessAt { get; init; }
    public bool IsAccepting { get; init; }
    public bool IsProcessorRunning { get; init; }
}
