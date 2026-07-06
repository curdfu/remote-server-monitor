namespace Monitor.WebApi.Hubs;

public static class MonitorHubEvents
{
    public const string HardwareRealtime = "hardwareRealtime";

    // 保留的事件名：当前后端不主动推送 networkRealtime，但前后端都保留常量。
    // 后续恢复网络实时推送时，可以复用同一个事件名，避免破坏已有客户端兼容性。
    public const string NetworkRealtime = "networkRealtime";

}
