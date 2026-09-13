using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Models;

namespace Monitor.Hardware.Services;

public sealed class ProcessCpuSnapshotBuffer : IProcessCpuSnapshotBuffer
{
    private ProcessCpuSnapshot? _latest;

    public ProcessCpuSnapshot? GetLatest()
    {
        return Volatile.Read(ref _latest);
    }

    public void Set(ProcessCpuSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _latest, snapshot);
    }
}
