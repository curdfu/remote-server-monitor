using Monitor.Network.Enums;

namespace Monitor.Network.Models;

public sealed class NetworkTraceEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public int ProcessId { get; init; }
    public TrafficDirection Direction { get; init; }
    public ProtocolType ProtocolType { get; init; }
    public long Bytes { get; init; }
    public string? LocalAddress { get; init; }
    public int? LocalPort { get; init; }
    public string? RemoteAddress { get; init; }
    public int? RemotePort { get; init; }
    public bool IsIPv6 { get; init; }
}
