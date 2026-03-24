namespace Monitor.Hardware.Models;

public sealed class DiskSpaceInfo
{
    public string Name { get; init; } = string.Empty;
    public uint DiskNumber { get; init; }
    public long? TotalBytes { get; init; }
    public long? UsedBytes { get; init; }
    public long? FreeBytes { get; init; }
}
