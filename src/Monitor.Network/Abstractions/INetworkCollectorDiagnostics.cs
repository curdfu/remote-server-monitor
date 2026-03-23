using Monitor.Network.Models;

namespace Monitor.Network.Abstractions;

public interface INetworkCollectorDiagnostics
{
    NetworkCollectorDiagnosticsSnapshot GetSnapshot();
}
