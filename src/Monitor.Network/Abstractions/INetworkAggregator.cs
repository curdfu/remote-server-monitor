using Monitor.Network.Models;

namespace Monitor.Network.Abstractions;

public interface INetworkAggregator
{
    long PendingEventCount { get; }
    Task<NetworkRealtimeSnapshot> GetRealtimeSnapshotAsync(CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
    NetworkRealtimeSnapshot? GetLatestRealtimeSnapshot();
    IReadOnlyList<NetworkRealtimeSnapshot> GetRecentRealtimeSnapshots();
    IReadOnlyList<TrafficBucket> PeekPendingBuckets(int maxCount);
    void ConfirmPendingBuckets(int count);
}
