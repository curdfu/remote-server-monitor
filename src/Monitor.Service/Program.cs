using Monitor.Service.Configuration;
using Monitor.Service.HostedServices;
using Monitor.Service.Infrastructure;
using Monitor.Storage.Configuration;
using Monitor.WebApi.Extensions;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var persistedSettings = PersistedSettingsLoader.Load();
if (persistedSettings.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(persistedSettings);
}

builder.ConfigureLogging();

builder.Services
    .AddMonitorModules(builder.Configuration)
    .AddHostedService<CollectorHostedService>()
    .AddHostedService<AggregationHostedService>()
    .AddHostedService<CleanupHostedService>()
    .AddMonitorWebApi(builder.Configuration);

var app = builder.Build();
var appConfiguration = app.Services.GetRequiredService<IAppConfigurationProvider>();

app.UseSerilogRequestLogging();
app.Urls.Add($"http://0.0.0.0:{appConfiguration.Current.HttpPort}");

app.Logger.LogInformation(
    "Monitor service starting on port {Port}, hardware interval {HardwareInterval}ms, network interval {NetworkInterval}ms.",
    appConfiguration.Current.HttpPort,
    appConfiguration.Current.HardwareSampleIntervalMs,
    appConfiguration.Current.NetworkSampleIntervalMs);

app.MapMonitorWebApi();

app.Run();
