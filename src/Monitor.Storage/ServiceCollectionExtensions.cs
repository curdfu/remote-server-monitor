using Microsoft.Extensions.DependencyInjection;
using Monitor.Storage.Abstractions;
using Monitor.Storage.Repositories;
using Monitor.Storage.Services;

namespace Monitor.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorStorage(this IServiceCollection services)
    {
        services.AddSingleton<IDbConnectionFactory, SqliteDbConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddHostedService<DatabaseInitializationHostedService>();
        services.AddHostedService<DatabaseOptimizationHostedService>();
        services.AddSingleton<HardwareRepository>();
        services.AddSingleton<NetworkTrafficRepository>();
        services.AddHostedService<NetworkTrafficRollupHostedService>();
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<IgnoredNetworkAppRepository>();
        services.AddSingleton<RetentionService>();
        return services;
    }
}
