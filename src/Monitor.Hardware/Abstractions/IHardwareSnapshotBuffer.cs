using Monitor.Hardware.Models;

namespace Monitor.Hardware.Abstractions;

public interface IHardwareSnapshotBuffer
{
    HardwareSnapshot? GetLatest();
    IReadOnlyList<HardwareSnapshot> GetRecent(int maxCount);
    void Add(HardwareSnapshot snapshot);
    IReadOnlyList<HardwareSnapshot> DequeuePendingBatch(int maxCount);
    int PendingCount { get; }
}
