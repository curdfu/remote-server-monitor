using Monitor.Network.Models;

namespace Monitor.Network.Abstractions;

public interface INetworkAggregator
{
    Task<NetworkRealtimeSnapshot> GetRealtimeSnapshotAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AppTrafficUsage>> GetTopAppsAsync(int topN, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
    NetworkRealtimeSnapshot? GetLatestRealtimeSnapshot();
    IReadOnlyList<AppTrafficUsage> GetLatestTopApps(int topN);
    IReadOnlyList<TrafficBucket> PeekPendingBuckets(int maxCount);
    void ConfirmPendingBuckets(int count);
}
