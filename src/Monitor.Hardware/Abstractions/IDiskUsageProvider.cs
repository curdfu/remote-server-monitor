using Monitor.Hardware.Models;

namespace Monitor.Hardware.Abstractions;

public interface IDiskUsageProvider
{
    IReadOnlyDictionary<uint, long?> GetCurrentUsedBytesByDiskNumber(IEnumerable<uint> diskNumbers);
    IReadOnlyList<DiskSpaceInfo> GetCurrentDiskSpaces();
}
