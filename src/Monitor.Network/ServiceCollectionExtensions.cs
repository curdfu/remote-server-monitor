using Microsoft.Extensions.DependencyInjection;
using Monitor.Network.Abstractions;
using Monitor.Network.Collectors;
using Monitor.Network.Services;

namespace Monitor.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorNetwork(this IServiceCollection services)
    {
        services.AddSingleton<INetworkCollector, EtwNetworkCollector>();
        services.AddSingleton<INetworkCollectorDiagnostics>(sp =>
            (INetworkCollectorDiagnostics)sp.GetRequiredService<INetworkCollector>());
        services.AddSingleton<IProcessResolver, ProcessResolver>();
        services.AddSingleton<IAppRegistry, AppRegistryService>();
        services.AddSingleton<IAddressClassifier, AddressClassifier>();
        services.AddSingleton<INetworkAggregator, TrafficAggregator>();
        return services;
    }
}
