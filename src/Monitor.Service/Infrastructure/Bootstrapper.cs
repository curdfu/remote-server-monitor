using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monitor.Contracts.Options;
using Monitor.Service.Configuration;

namespace Monitor.Service.Infrastructure;

public static class Bootstrapper
{
    public static IServiceCollection AddMonitorModules(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<MonitorSettings>()
            .Bind(configuration.GetSection(MonitorSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        Monitor.Hardware.ServiceCollectionExtensions.AddMonitorHardware(services);
        Monitor.Network.ServiceCollectionExtensions.AddMonitorNetwork(services);
        Monitor.Storage.ServiceCollectionExtensions.AddMonitorStorage(services);
        services.AddSingleton<IAppConfigurationProvider, AppConfigurationProvider>();

        return services;
    }
}
