using Microsoft.AspNetCore.SignalR;

namespace Monitor.WebApi.Hubs;

public sealed class MonitorHub : Hub
{
    public const string HardwareGroup = "hardware";
    public const string NetworkGroup = "network";

    public Task SubscribeHardware()
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, HardwareGroup);
    }

    public Task UnsubscribeHardware()
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, HardwareGroup);
    }

    public Task SubscribeNetwork()
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, NetworkGroup);
    }

    public Task UnsubscribeNetwork()
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, NetworkGroup);
    }
}
