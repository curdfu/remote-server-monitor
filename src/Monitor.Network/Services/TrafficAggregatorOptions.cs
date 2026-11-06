namespace Monitor.Network.Services;

// Startup-only limits. These do not change while buckets are in memory.
public sealed class TrafficAggregatorOptions
{
    public int EventQueueCapacity { get; init; } = 65_536;
    public int BucketCapacity { get; init; } = 100_000;
}
