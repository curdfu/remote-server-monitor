using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Models;

namespace Monitor.WebApi.Services;

public sealed class DiskUsageSnapshotCache(IDiskUsageProvider diskUsageProvider)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private DiskUsageSnapshot? _snapshot;
    private DateTimeOffset _expiresAt;

    public DiskUsageSnapshot GetSnapshot(IEnumerable<uint> diskNumbers)
    {
        var requestedDiskNumbers = diskNumbers.Distinct().ToArray();

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var coversRequestedDisks = requestedDiskNumbers.All(
                diskNumber => _snapshot?.UsedBytesByDiskNumber.ContainsKey(diskNumber) == true);

            if (_snapshot is not null && now < _expiresAt && coversRequestedDisks)
            {
                return _snapshot;
            }

            _snapshot = new DiskUsageSnapshot(
                diskUsageProvider.GetCurrentUsedBytesByDiskNumber(requestedDiskNumbers),
                diskUsageProvider.GetCurrentDiskSpaces());
            _expiresAt = now.Add(CacheDuration);

            return _snapshot;
        }
    }
}

public sealed record DiskUsageSnapshot(
    IReadOnlyDictionary<uint, long?> UsedBytesByDiskNumber,
    IReadOnlyList<DiskSpaceInfo> DiskSpaces);
