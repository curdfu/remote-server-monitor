using Monitor.Network.Enums;

namespace Monitor.Network.Models;

public sealed class TrafficBucket
{
    public DateTimeOffset BucketStartTime { get; init; }
    public int BucketGranularitySeconds { get; init; }
    public string AppKey { get; init; } = string.Empty;
    public TrafficDirection Direction { get; init; }
    public AddressScopeType ScopeType { get; init; }
    public long Bytes { get; init; }
    public long Packets { get; init; }
}
