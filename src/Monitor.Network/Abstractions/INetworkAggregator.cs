using Monitor.Network.Models;

namespace Monitor.Network.Abstractions;

public interface INetworkAggregator
{
    Task<NetworkRealtimeSnapshot> GetRealtimeSnapshotAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AppTrafficUsage>> GetTopAppsAsync(int topN, CancellationToken cancellationToken = default);
    IReadOnlyList<TrafficBucket> DequeuePendingBuckets(int maxCount);
}
