using Microsoft.Extensions.DependencyInjection;
using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Implementations;
using Monitor.Hardware.Services;

namespace Monitor.Hardware;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorHardware(this IServiceCollection services)
    {
        services.AddSingleton<LibreHardwareCollector>();
        services.AddSingleton<IHardwareCollector>(sp => sp.GetRequiredService<LibreHardwareCollector>());
        services.AddSingleton<IDiskUsageProvider>(sp => sp.GetRequiredService<LibreHardwareCollector>());
        services.AddSingleton<IHardwareSnapshotBuffer, HardwareSnapshotBuffer>();
        return services;
    }
}
