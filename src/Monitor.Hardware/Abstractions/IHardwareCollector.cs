using Monitor.Hardware.Models;

namespace Monitor.Hardware.Abstractions;

public interface IHardwareCollector
{
    Task<HardwareSnapshot> GetCurrentSnapshotAsync(CancellationToken cancellationToken = default);
}
