using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Monitor.Network.Collectors;

internal interface IEtwSession : IDisposable
{
    void RegisterHandlers(Action<KernelTraceEventParser> register);
    long EventsLost { get; }
    void Process();
    void Stop();
}

internal sealed class EtwSession : IEtwSession
{
    private readonly TraceEventSession _session;

    public EtwSession(string name, int bufferSizeMb, TraceEventSessionOptions options)
    {
        _session = new TraceEventSession(name, options);
        try
        {
            _session.StopOnDispose = true;
            _session.BufferSizeMB = bufferSizeMb;
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public void RegisterHandlers(Action<KernelTraceEventParser> register) => register(_session.Source.Kernel);
    public long EventsLost => _session.EventsLost;
    public void Process() => _session.Source.Process();
    public void Stop() => _session.Stop();
    public void Dispose() => _session.Dispose();
}
