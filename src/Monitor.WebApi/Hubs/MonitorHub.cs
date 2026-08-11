using Microsoft.AspNetCore.SignalR;

namespace Monitor.WebApi.Hubs;

public sealed class MonitorHub(
    Monitor.Hardware.Abstractions.IHardwareMonitoringDemand hardwareMonitoringDemand) : Hub
{
    public const string HardwareGroup = "hardware";

    public async Task SubscribeHardware()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, HardwareGroup);
        hardwareMonitoringDemand.AddHardwareSubscriber(Context.ConnectionId);
    }

    public async Task UnsubscribeHardware()
    {
        hardwareMonitoringDemand.RemoveHardwareSubscriber(Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HardwareGroup);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        hardwareMonitoringDemand.RemoveHardwareSubscriber(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
