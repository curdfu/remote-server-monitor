namespace Monitor.Hardware.Abstractions;

// 记录是否存在真正消费硬件实时数据的页面。
// 采集器用它避免在无人查看时周期性访问存储 SMART，同时保留 CPU/内存采样和历史入库。
public interface IHardwareMonitoringDemand
{
    bool HasHardwareSubscribers { get; }
    long ActivationVersion { get; }

    bool AddHardwareSubscriber(string connectionId);
    bool RemoveHardwareSubscriber(string connectionId);
    Task WaitForActivationAsync(long observedVersion, CancellationToken cancellationToken);
}
