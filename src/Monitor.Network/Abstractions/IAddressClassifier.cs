using System.Net;
using Monitor.Network.Enums;

namespace Monitor.Network.Abstractions;

public interface IAddressClassifier
{
    AddressScopeType Classify(IPAddress? remoteAddress, IPAddress? localAddress = null);
    AddressScopeType Classify(string? remoteAddress, string? localAddress = null);
}
