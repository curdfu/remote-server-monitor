using Microsoft.Extensions.Options;
using Monitor.Contracts.Options;

namespace Monitor.Service.Configuration;

public sealed class AppConfigurationProvider(IOptionsMonitor<MonitorSettings> settings) : IAppConfigurationProvider
{
    public MonitorSettings Current => settings.CurrentValue;
}
