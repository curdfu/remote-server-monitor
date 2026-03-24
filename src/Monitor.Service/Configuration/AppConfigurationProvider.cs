using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Microsoft.Extensions.Options;

namespace Monitor.Service.Configuration;

public sealed class AppConfigurationProvider : IAppConfigurationProvider, IMonitorSettingsProvider
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<int, Action<MonitorSettings>> _callbacks = new();
    private MonitorSettings _current;
    private int _nextCallbackId;

    public AppConfigurationProvider(IOptionsMonitor<MonitorSettings> settings)
    {
        _current = Clone(settings.CurrentValue);
    }

    public MonitorSettings Current
    {
        get
        {
            lock (_syncRoot)
            {
                return Clone(_current);
            }
        }
    }

    public MonitorSettings Update(AppSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        MonitorSettings updated;
        Action<MonitorSettings>[] callbacks;
        lock (_syncRoot)
        {
            updated = Clone(_current);
            updated.HardwareSampleIntervalMs = settings.HardwareSampleIntervalMs;
            updated.NetworkSampleIntervalMs = settings.NetworkSampleIntervalMs;
            updated.AggregateIntervalSeconds = settings.AggregateIntervalSeconds;
            updated.HistoryRetentionDays = settings.HistoryRetentionDays;
            updated.TopNDefault = settings.TopNDefault;
            _current = updated;
            callbacks = _callbacks.Values.ToArray();
        }

        var snapshot = Clone(updated);
        foreach (var callback in callbacks)
        {
            callback(snapshot);
        }

        return snapshot;
    }

    public IDisposable RegisterChangeCallback(Action<MonitorSettings> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var callbackId = Interlocked.Increment(ref _nextCallbackId);
        lock (_syncRoot)
        {
            _callbacks[callbackId] = callback;
        }

        return new CallbackRegistration(this, callbackId);
    }

    private void Unregister(int callbackId)
    {
        lock (_syncRoot)
        {
            _callbacks.Remove(callbackId);
        }
    }

    private static MonitorSettings Clone(MonitorSettings settings)
    {
        var addressClassification = settings.AddressClassification ?? new AddressClassificationSettings();

        return new MonitorSettings
        {
            HttpPort = settings.HttpPort,
            HardwareSampleIntervalMs = settings.HardwareSampleIntervalMs,
            NetworkSampleIntervalMs = settings.NetworkSampleIntervalMs,
            AggregateIntervalSeconds = settings.AggregateIntervalSeconds,
            HistoryRetentionDays = settings.HistoryRetentionDays,
            TopNDefault = settings.TopNDefault,
            EtwBufferSizeMb = settings.EtwBufferSizeMb,
            AddressClassification = new AddressClassificationSettings
            {
                TreatPrivateAddressesAsLan = addressClassification.TreatPrivateAddressesAsLan,
                TreatLocalSubnetsAsLan = addressClassification.TreatLocalSubnetsAsLan,
                TreatLoopbackAsLoopback = addressClassification.TreatLoopbackAsLoopback,
                AdditionalLanCidrs = addressClassification.AdditionalLanCidrs?.ToArray() ?? [],
                AdditionalWanCidrs = addressClassification.AdditionalWanCidrs?.ToArray() ?? []
            }
        };
    }

    private sealed class CallbackRegistration(AppConfigurationProvider owner, int callbackId) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            owner.Unregister(callbackId);
            _disposed = true;
        }
    }
}
