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
var currentSettings = appConfiguration.Current;
var listenAddress = builder.Configuration["Monitor:ListenAddress"];
if (string.IsNullOrWhiteSpace(listenAddress))
{
    listenAddress = IPAddress.Loopback.ToString();
}

app.UseSerilogRequestLogging();
app.Urls.Add($"http://{listenAddress}:{currentSettings.HttpPort}");

Log.ForContext("ImportantInfo", true).Information(
    "Monitor service listen configuration applied. ListenAddress={ListenAddress}, Port={Port}, PersistedSettingsLoaded={PersistedSettingsLoaded}",
    listenAddress,
    currentSettings.HttpPort,
    persistedSettings.Count > 0);

Log.ForContext("ImportantInfo", true).Information(
    "Monitor service runtime configuration applied. HardwareIntervalMs={HardwareIntervalMs}, NetworkIntervalMs={NetworkIntervalMs}, AggregateIntervalSeconds={AggregateIntervalSeconds}, HistoryRetentionDays={HistoryRetentionDays}, TopNDefault={TopNDefault}, EtwBufferSizeMb={EtwBufferSizeMb}",
    currentSettings.HardwareSampleIntervalMs,
    currentSettings.NetworkSampleIntervalMs,
    currentSettings.AggregateIntervalSeconds,
    currentSettings.HistoryRetentionDays,
    currentSettings.TopNDefault,
    currentSettings.EtwBufferSizeMb);

if (!IPAddress.TryParse(listenAddress, out var parsedListenAddress) || !IPAddress.IsLoopback(parsedListenAddress))
{
    app.Logger.LogWarning(
        "Monitor service is listening on a non-loopback address ({ListenAddress}). Ensure the host firewall and reverse proxy rules are configured appropriately.",
        listenAddress);
}

app.MapMonitorWebApi();

app.Run();
return 0;
