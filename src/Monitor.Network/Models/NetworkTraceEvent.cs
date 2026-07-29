using Monitor.Network.Enums;

namespace Monitor.Network.Models;

// ETW 网络事件属于高频内部消息。使用只读值类型并直接携带 IPAddress，
// 避免每个事件分配消息对象、地址字符串以及分类阶段重新解析地址。
public readonly record struct NetworkTraceEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public int ProcessId { get; init; }
    public TrafficDirection Direction { get; init; }
    public ProtocolType ProtocolType { get; init; }
    public long Bytes { get; init; }
    public System.Net.IPAddress? LocalAddress { get; init; }
    public int? LocalPort { get; init; }
    public System.Net.IPAddress? RemoteAddress { get; init; }
    public int? RemotePort { get; init; }
    public bool IsIPv6 { get; init; }
}
