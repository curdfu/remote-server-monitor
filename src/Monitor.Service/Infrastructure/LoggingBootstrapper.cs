using Serilog;
using Serilog.Events;

namespace Monitor.Service.Infrastructure;

public static class LoggingBootstrapper
{
    private static readonly HashSet<string> ImportantInformationSources = new(StringComparer.Ordinal)
    {
        "Monitor.Storage.Services.DatabaseInitializer",
        "Monitor.Storage.Services.NetworkTrafficRollupHostedService",
        "Monitor.Storage.Services.RetentionService",
        "Monitor.Service.HostedServices.CleanupHostedService",
        "Monitor.Hardware.Implementations.LibreHardwareCollector",
        "Monitor.Network.Collectors.EtwNetworkCollector",
        "Monitor.Storage.Repositories.NetworkTrafficRepository",
        "Monitor.Storage.Repositories.SettingsRepository"
    };

    public static void ConfigureLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, loggerConfiguration) =>
        {
            loggerConfiguration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .MinimumLevel.Is(LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Logger(configureLogger: nestedLogger => nestedLogger
                    .Filter.ByIncludingOnly(static logEvent => ShouldWriteToFile(logEvent))
                    .WriteTo.File(
                        path: Path.Combine(AppContext.BaseDirectory, "logs", "monitor-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        fileSizeLimitBytes: 10 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        restrictedToMinimumLevel: LogEventLevel.Information,
                        shared: true));
        });
    }

    private static bool ShouldWriteToFile(LogEvent logEvent)
    {
        if (logEvent.Level >= LogEventLevel.Warning)
        {
            return true;
        }

        if (logEvent.Level != LogEventLevel.Information)
        {
            return false;
        }

        if (logEvent.Properties.TryGetValue("ImportantInfo", out var importantInfoValue) &&
            importantInfoValue is ScalarValue { Value: true })
        {
            return true;
        }

        if (!logEvent.Properties.TryGetValue("SourceContext", out var sourceContextValue) ||
            sourceContextValue is not ScalarValue { Value: string sourceContext })
        {
            return false;
        }

        return ImportantInformationSources.Contains(sourceContext);
    }
}
