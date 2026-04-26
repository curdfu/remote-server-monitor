namespace Monitor.WebApi.Hubs;

public static class MonitorHubEvents
{
    public const string HardwareRealtime = "hardwareRealtime";

    // Reserved event name.
    // Network realtime push was planned before, but it is intentionally disabled for now.
    // Keep the constant so front-end/back-end event names stay aligned if restored later.
    public const string NetworkRealtime = "networkRealtime";

}
