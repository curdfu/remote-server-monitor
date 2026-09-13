using Monitor.Hardware.Models;

namespace Monitor.Hardware.Abstractions;

public interface IProcessCpuSnapshotBuffer
{
    ProcessCpuSnapshot? GetLatest();
    void Set(ProcessCpuSnapshot snapshot);
}
