namespace Monitor.Contracts.Dtos;

public sealed class DiskSpaceDto
{
    public string Name { get; init; } = string.Empty;
    public long? TotalBytes { get; init; }
    public long? UsedBytes { get; init; }
    public long? FreeBytes { get; init; }
}
