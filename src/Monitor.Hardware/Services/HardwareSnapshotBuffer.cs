using System.Collections.Concurrent;
using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Models;

namespace Monitor.Hardware.Services;

public sealed class HardwareSnapshotBuffer : IHardwareSnapshotBuffer
{
    private readonly ConcurrentQueue<HardwareSnapshot> _recentSnapshots = new();
    private readonly ConcurrentQueue<HardwareSnapshot> _pendingSnapshots = new();
    private readonly int _maxRecentSnapshots;
    private HardwareSnapshot? _latest;

    public HardwareSnapshotBuffer(int maxRecentSnapshots = 900)
    {
        _maxRecentSnapshots = Math.Max(10, maxRecentSnapshots);
    }

    public int PendingCount => _pendingSnapshots.Count;

    public HardwareSnapshot? GetLatest()
    {
        return Volatile.Read(ref _latest);
    }

    public IReadOnlyList<HardwareSnapshot> GetRecent(int maxCount)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<HardwareSnapshot>();
        }

        return _recentSnapshots
            .ToArray()
            .TakeLast(maxCount)
            .ToArray();
    }

    public void Add(HardwareSnapshot snapshot)
    {
        _latest = snapshot;
        _recentSnapshots.Enqueue(snapshot);
        _pendingSnapshots.Enqueue(snapshot);

        while (_recentSnapshots.Count > _maxRecentSnapshots && _recentSnapshots.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<HardwareSnapshot> DequeuePendingBatch(int maxCount)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<HardwareSnapshot>();
        }

        var items = new List<HardwareSnapshot>(maxCount);
        while (items.Count < maxCount && _pendingSnapshots.TryDequeue(out var snapshot))
        {
            items.Add(snapshot);
        }

        return items;
    }

    public void RequeuePendingBatch(IReadOnlyCollection<HardwareSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        foreach (var snapshot in snapshots)
        {
            _pendingSnapshots.Enqueue(snapshot);
        }
    }
}
