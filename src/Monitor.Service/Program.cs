using System.Net;
using Monitor.Service.Configuration;
using Monitor.Service.HostedServices;
using Monitor.Service.Infrastructure;
using Monitor.Storage.Configuration;
using Monitor.WebApi.Extensions;
using Serilog;

// Windows 服务安装/卸载命令在构建 Host 前处理，避免命令行维护操作误启动 Web 服务和采集线程。
if (WindowsServiceCommandHandler.TryHandle(args, out var commandExitCode))
{
    return commandExitCode;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// SQLite 中保存的是用户运行时配置，启动时要先注入 Configuration，后续 Kestrel、DI 和 Options 才能读取最终值。
var persistedSettings = PersistedSettingsLoader.Load();
if (persistedSettings.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(persistedSettings);
}

// UseUrls 必须在 Build() 前设置，否则 Kestrel 默认地址可能和用户保存的监听地址同时生效。
// 此时 IConfiguration 已经合并 appsettings.json 和 SQLite 持久化设置。
var listenAddress = builder.Configuration["Monitor:ListenAddress"];
if (string.IsNullOrWhiteSpace(listenAddress))
{
    listenAddress = IPAddress.Loopback.ToString();
}
var httpPort = builder.Configuration.GetValue<int>("Monitor:HttpPort");
builder.WebHost.UseUrls($"http://{listenAddress}:{httpPort}");

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = ServiceConstants.ServiceName;
});

builder.ConfigureLogging();

// 后台服务顺序体现数据流：采集器先产出硬件/网络原始数据，聚合服务再刷新实时缓存并批量落库。
builder.Services
    .AddMonitorModules(builder.Configuration)
    .AddHostedService<CollectorHostedService>()
    .AddHostedService<AggregationHostedService>()
    .AddHostedService<CleanupHostedService>()
    .AddMonitorWebApi(builder.Configuration);

var app = builder.Build();
var appConfiguration = app.Services.GetRequiredService<IAppConfigurationProvider>();
var currentSettings = appConfiguration.Current;

app.UseSerilogRequestLogging();

Log.ForContext("ImportantInfo", true).Information(
    "Monitor service listen configuration applied. ListenAddress={ListenAddress}, Port={Port}, PersistedSettingsLoaded={PersistedSettingsLoaded}",
    listenAddress,
    currentSettings.HttpPort,
    persistedSettings.Count > 0);

Log.ForContext("ImportantInfo", true).Information(
    "Monitor service runtime configuration applied. HardwareIntervalMs={HardwareIntervalMs}, AggregateIntervalSeconds={AggregateIntervalSeconds}, HistoryRetentionDays={HistoryRetentionDays}, TopNDefault={TopNDefault}, EtwBufferSizeMb={EtwBufferSizeMb}",
    currentSettings.HardwareSampleIntervalMs,
    currentSettings.AggregateIntervalSeconds,
    currentSettings.HistoryRetentionDays,
    currentSettings.TopNDefault,
    currentSettings.EtwBufferSizeMb);

// 非 loopback 监听会把监控接口暴露到局域网或公网网卡，启动时明确记录安全提示。
if (!IPAddress.TryParse(listenAddress, out var parsedListenAddress) || !IPAddress.IsLoopback(parsedListenAddress))
{
    app.Logger.LogWarning(
        "Monitor service is listening on a non-loopback address ({ListenAddress}). Ensure the host firewall and reverse proxy rules are configured appropriately.",
        listenAddress);
}

app.MapMonitorWebApi();

app.Run();
return 0;
