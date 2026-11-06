using Monitor.Network.Models;

namespace Monitor.Network.Abstractions;

public interface INetworkAggregator
{
    long PendingEventCount { get; }
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<TrafficBucket> PeekPendingBuckets(int maxCount);
    void ConfirmPendingBuckets(int count);
    TrafficAggregatorDiagnosticsSnapshot GetDiagnostics();
    void RecordPersistenceFailure();
    void RecordPersistenceSuccess();
}
