using Monitor.Hardware.Abstractions;

namespace Monitor.Hardware.Services;

public sealed class HardwareMonitoringDemand : IHardwareMonitoringDemand
{
    private readonly object _syncRoot = new();
    private readonly HashSet<string> _hardwareSubscriberIds = new(StringComparer.Ordinal);
    private TaskCompletionSource _activationChanged = CreateActivationSource();
    private long _activationVersion;
    private int _hasHardwareSubscribers;

    public bool HasHardwareSubscribers => Volatile.Read(ref _hasHardwareSubscribers) != 0;
    public long ActivationVersion => Interlocked.Read(ref _activationVersion);

    public bool AddHardwareSubscriber(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        TaskCompletionSource? activationToSignal = null;
        lock (_syncRoot)
        {
            if (!_hardwareSubscriberIds.Add(connectionId))
            {
                return false;
            }

            if (_hardwareSubscriberIds.Count == 1)
            {
                Volatile.Write(ref _hasHardwareSubscribers, 1);
                Interlocked.Increment(ref _activationVersion);
                activationToSignal = _activationChanged;
                _activationChanged = CreateActivationSource();
            }
        }

        activationToSignal?.TrySetResult();
        return true;
    }

    public bool RemoveHardwareSubscriber(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_syncRoot)
        {
            if (!_hardwareSubscriberIds.Remove(connectionId))
            {
                return false;
            }

            if (_hardwareSubscriberIds.Count == 0)
            {
                Volatile.Write(ref _hasHardwareSubscribers, 0);
            }
        }

        return true;
    }

    public Task WaitForActivationAsync(long observedVersion, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_activationVersion != observedVersion)
            {
                return Task.CompletedTask;
            }

            return _activationChanged.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource CreateActivationSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
