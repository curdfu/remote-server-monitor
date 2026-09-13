using Monitor.Hardware.Models;

namespace Monitor.Hardware.Abstractions;

public interface IProcessCpuCollector
{
    Task<ProcessCpuSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}
