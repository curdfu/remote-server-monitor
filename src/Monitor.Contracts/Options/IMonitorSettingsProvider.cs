using Monitor.Contracts.Dtos;

namespace Monitor.Contracts.Options;

public interface IMonitorSettingsProvider
{
    MonitorSettings Current { get; }
    MonitorSettings Update(AppSettingsDto settings);
    IDisposable RegisterChangeCallback(Action<MonitorSettings> callback);
}
