using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monitor.WebApi.Services;

namespace Monitor.WebApi.Extensions;

public static class WebApiServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorWebApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins("http://localhost:5173")
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
            });
        });

        services.AddSignalR();
        services.AddEndpointsApiExplorer();
        services.AddSingleton<MonitorRealtimeBroadcaster>();
        services.AddHostedService<MonitorRealtimePushHostedService>();
        return services;
    }
}
