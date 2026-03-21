namespace Monitor.Network.Abstractions;

public interface INetworkCollector
{
    event Action<Monitor.Network.Models.NetworkTraceEvent>? EventReceived;
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
