using System.Net;
using Monitor.Service.Configuration;
using Monitor.Service.HostedServices;
using Monitor.Service.Infrastructure;
using Monitor.Storage.Configuration;
using Monitor.WebApi.Extensions;
using Serilog;

if (WindowsServiceCommandHandler.TryHandle(args, out var commandExitCode))
{
    return commandExitCode;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

var persistedSettings = PersistedSettingsLoader.Load();
if (persistedSettings.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(persistedSettings);
}

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = ServiceConstants.ServiceName;
});

builder.ConfigureLogging();

builder.Services
    .AddMonitorModules(builder.Configuration)
    .AddHostedService<CollectorHostedService>()
    .AddHostedService<AggregationHostedService>()
    .AddHostedService<CleanupHostedService>()
    .AddMonitorWebApi(builder.Configuration);

var app = builder.Build();
var appConfiguration = app.Services.GetRequiredService<IAppConfigurationProvider>();
var listenAddress = builder.Configuration["Monitor:ListenAddress"];
if (string.IsNullOrWhiteSpace(listenAddress))
{
    listenAddress = IPAddress.Loopback.ToString();
}

app.UseSerilogRequestLogging();
app.Urls.Add($"http://{listenAddress}:{appConfiguration.Current.HttpPort}");

app.Logger.LogInformation(
    "Monitor service starting on {ListenAddress}:{Port}, hardware interval {HardwareInterval}ms, network interval {NetworkInterval}ms.",
    listenAddress,
    appConfiguration.Current.HttpPort,
    appConfiguration.Current.HardwareSampleIntervalMs,
    appConfiguration.Current.NetworkSampleIntervalMs);

if (!IPAddress.TryParse(listenAddress, out var parsedListenAddress) || !IPAddress.IsLoopback(parsedListenAddress))
{
    app.Logger.LogWarning(
        "Monitor service is listening on a non-loopback address ({ListenAddress}). Ensure the host firewall and reverse proxy rules are configured appropriately.",
        listenAddress);
}

app.MapMonitorWebApi();

app.Run();
return 0;
