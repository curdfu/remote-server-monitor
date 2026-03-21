using Monitor.Contracts.Options;

namespace Monitor.Service.Configuration;

public interface IAppConfigurationProvider
{
    MonitorSettings Current { get; }
}
