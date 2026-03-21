using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;
using Monitor.Network.Models;
using System.Runtime.InteropServices;

namespace Monitor.Network.Collectors;

public sealed class EtwNetworkCollector(
    ILogger<EtwNetworkCollector> logger,
    IOptionsMonitor<MonitorSettings> settingsMonitor) : INetworkCollector, IDisposable
{
    private const int InsufficientResourcesHResult = unchecked((int)0x800705AA);

    private readonly object _syncRoot = new();
    private TraceEventSession? _session;
    private Task? _processingTask;
    private string? _sessionName;
    private bool _disposed;

    public event Action<NetworkTraceEvent>? EventReceived;

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            _sessionName = $"Monitor.Network.Etw.{Environment.ProcessId}.{Guid.NewGuid():N}";

            try
            {
                var bufferSizeMb = Math.Clamp(settingsMonitor.CurrentValue.EtwBufferSizeMb, 1, 128);
                var sessionOptions = TraceEventSessionOptions.Create |
                                     TraceEventSessionOptions.NoPerProcessorBuffering;

                _session = new TraceEventSession(_sessionName, sessionOptions);
                _session.StopOnDispose = true;
                _session.BufferSizeMB = bufferSizeMb;
                _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

                RegisterHandlers(_session.Source.Kernel);

                _processingTask = Task.Factory.StartNew(
                    () => ProcessSession(_session, logger),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                IsRunning = true;
                logger.LogInformation(
                    "ETW network collector started. SessionName: {SessionName}, BufferSizeMB: {BufferSizeMB}, SessionOptions: {SessionOptions}",
                    _sessionName,
                    bufferSizeMb,
                    sessionOptions);
            }
            catch (COMException exception) when (exception.HResult == InsufficientResourcesHResult)
            {
                var sessionName = _sessionName;
                var bufferSizeMb = settingsMonitor.CurrentValue.EtwBufferSizeMb;
                CleanupFailedStart();
                logger.LogError(
                    exception,
                    "Failed to start ETW network collector because ETW resources are insufficient. SessionName: {SessionName}, BufferSizeMB: {BufferSizeMB}. Consider reducing Monitor:EtwBufferSizeMb further or retrying after other ETW sessions are released.",
                    sessionName,
                    bufferSizeMb);
                return Task.CompletedTask;
            }
            catch (UnauthorizedAccessException exception)
            {
                var sessionName = _sessionName;
                CleanupFailedStart();
                logger.LogError(
                    exception,
                    "Failed to start ETW network collector due to insufficient privileges. SessionName: {SessionName}. Administrator rights are required to start the ETW kernel session.",
                    sessionName);
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                var sessionName = _sessionName;
                CleanupFailedStart();
                logger.LogError(
                    exception,
                    "Failed to start ETW network collector. SessionName: {SessionName}",
                    sessionName);
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? processingTask;
        string? sessionName;

        lock (_syncRoot)
        {
            if (!IsRunning)
            {
                return;
            }

            processingTask = _processingTask;
            sessionName = _sessionName;

            try
            {
                _session?.Stop();
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Ignoring ETW session stop exception.");
            }

            _session?.Dispose();
            _session = null;
            _processingTask = null;
            _sessionName = null;
            IsRunning = false;
        }

        if (processingTask is not null)
        {
            try
            {
                await processingTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "ETW processing task ended with exception during stop.");
            }
        }

        logger.LogInformation("ETW network collector stopped. SessionName: {SessionName}", sessionName);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _session?.Dispose();
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Failed to dispose ETW session cleanly.");
            }
            finally
            {
                _session = null;
                _processingTask = null;
                _sessionName = null;
                IsRunning = false;
                _disposed = true;
            }
        }
    }

    private void RegisterHandlers(KernelTraceEventParser parser)
    {
        parser.TcpIpSend += data => PublishTcpSend(data, false);
        parser.TcpIpRecv += data => PublishTcpRecv(data, false);
        parser.TcpIpSendIPV6 += data => PublishTcpSendV6(data);
        parser.TcpIpRecvIPV6 += data => PublishTcpRecvV6(data);
        parser.UdpIpSend += data => PublishUdp(data, TrafficDirection.Outbound, false);
        parser.UdpIpRecv += data => PublishUdp(data, TrafficDirection.Inbound, false);
        parser.UdpIpSendIPV6 += data => PublishUdpV6(data, TrafficDirection.Outbound);
        parser.UdpIpRecvIPV6 += data => PublishUdpV6(data, TrafficDirection.Inbound);
    }

    private static void ProcessSession(TraceEventSession session, ILogger logger)
    {
        try
        {
            session.Source.Process();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "ETW session processing loop exited with exception.");
        }
    }

    private void PublishTcpSend(TcpIpSendTraceData data, bool isIpv6)
    {
        Publish(new NetworkTraceEvent
        {
            Timestamp = new DateTimeOffset(data.TimeStamp),
            ProcessId = data.ProcessID,
            Direction = TrafficDirection.Outbound,
            ProtocolType = ProtocolType.Tcp,
            Bytes = data.size,
            LocalAddress = data.saddr?.ToString(),
            LocalPort = data.sport,
            RemoteAddress = data.daddr?.ToString(),
            RemotePort = data.dport,
            IsIPv6 = isIpv6
        });
    }

    private void PublishTcpRecv(TcpIpTraceData data, bool isIpv6)
    {
        Publish(new NetworkTraceEvent
        {
            Timestamp = new DateTimeOffset(data.TimeStamp),
            ProcessId = data.ProcessID,
            Direction = TrafficDirection.Inbound,
            ProtocolType = ProtocolType.Tcp,
            Bytes = data.size,
            LocalAddress = data.daddr?.ToString(),
            LocalPort = data.dport,
            RemoteAddress = data.saddr?.ToString(),
            RemotePort = data.sport,
            IsIPv6 = isIpv6
        });
    }

    private void PublishTcpSendV6(TcpIpV6SendTraceData data)
    {
        Publish(new NetworkTraceEvent
        {
            Timestamp = new DateTimeOffset(data.TimeStamp),
            ProcessId = data.ProcessID,
            Direction = TrafficDirection.Outbound,
            ProtocolType = ProtocolType.Tcp,
            Bytes = data.size,
            LocalAddress = data.saddr?.ToString(),
            LocalPort = data.sport,
            RemoteAddress = data.daddr?.ToString(),
            RemotePort = data.dport,
            IsIPv6 = true
        });
    }

    private void PublishTcpRecvV6(TcpIpV6TraceData data)
    {
        Publish(new NetworkTraceEvent
        {
            Timestamp = new DateTimeOffset(data.TimeStamp),
            ProcessId = data.ProcessID,
            Direction = TrafficDirection.Inbound,
            ProtocolType = ProtocolType.Tcp,
            Bytes = data.size,
            LocalAddress = data.daddr?.ToString(),
            LocalPort = data.dport,
            RemoteAddress = data.saddr?.ToString(),
            RemotePort = data.sport,
            IsIPv6 = true
        });
    }

    private void PublishUdp(UdpIpTraceData data, TrafficDirection direction, bool isIpv6)
    {
        Publish(new NetworkTraceEvent
        {
            Timestamp = new DateTimeOffset(data.TimeStamp),
            ProcessId = data.ProcessID,
            Direction = direction,
            ProtocolType = ProtocolType.Udp,
            Bytes = data.size,
            LocalAddress = direction == TrafficDirection.Outbound ? data.saddr?.ToString() : data.daddr?.ToString(),
            LocalPort = direction == TrafficDirection.Outbound ? data.sport : data.dport,
            RemoteAddress = direction == TrafficDirection.Outbound ? data.daddr?.ToString() : data.saddr?.ToString(),
            RemotePort = direction == TrafficDirection.Outbound ? data.dport : data.sport,
            IsIPv6 = isIpv6
        });
    }

    private void PublishUdpV6(UpdIpV6TraceData data, TrafficDirection direction)
    {
        Publish(new NetworkTraceEvent
        {
            Timestamp = new DateTimeOffset(data.TimeStamp),
            ProcessId = data.ProcessID,
            Direction = direction,
            ProtocolType = ProtocolType.Udp,
            Bytes = data.size,
            LocalAddress = direction == TrafficDirection.Outbound ? data.saddr?.ToString() : data.daddr?.ToString(),
            LocalPort = direction == TrafficDirection.Outbound ? data.sport : data.dport,
            RemoteAddress = direction == TrafficDirection.Outbound ? data.daddr?.ToString() : data.saddr?.ToString(),
            RemotePort = direction == TrafficDirection.Outbound ? data.dport : data.sport,
            IsIPv6 = true
        });
    }

    private void Publish(NetworkTraceEvent traceEvent)
    {
        if (traceEvent.ProcessId <= 0 || traceEvent.Bytes < 0)
        {
            return;
        }

        EventReceived?.Invoke(traceEvent);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void CleanupFailedStart()
    {
        _session?.Dispose();
        _session = null;
        _processingTask = null;
        _sessionName = null;
    }
}
