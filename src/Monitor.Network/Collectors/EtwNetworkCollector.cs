using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;
using Monitor.Network.Models;

namespace Monitor.Network.Collectors;

public sealed class EtwNetworkCollector(
    ILogger<EtwNetworkCollector> logger,
    IMonitorSettingsProvider settingsMonitor) : INetworkCollector, INetworkCollectorDiagnostics, IDisposable
{
    private static readonly int[] AdaptiveBufferSizesMb = [4, 8, 16];
    private const int InsufficientResourcesHResult = unchecked((int)0x800705AA);
    private const string SessionNamePrefix = "Monitor.Network.Etw.";
    private const int TargetProcessEventLogLimit = 500;
    private static readonly string[] TargetProcessNames = ["chrome", "verge-mihomo", "telegram"];

    private readonly object _syncRoot = new();
    private readonly ConcurrentDictionary<int, string?> _targetProcessNameCache = new();
    private readonly Dictionary<string, int> _targetProcessLogCounts = TargetProcessNames.ToDictionary(
        static name => name,
        static _ => 0,
        StringComparer.OrdinalIgnoreCase);
    private TraceEventSession? _session;
    private Task? _processingTask;
    private Task? _adaptiveRestartTask;
    private Task? _sessionMonitorTask;
    private CancellationTokenSource? _sessionMonitorCancellation;
    private string? _sessionName;
    private DateTimeOffset? _startedAt;
    private int _bufferSizeMb;
    private int _adaptiveRestartCount;
    private long _sessionPublishedEvents;
    private long _sessionLostEvents;
    private long _totalPublishedEvents;
    private long _totalLostEvents;
    private long _lastLoggedLostEvents;
    private bool _enableTargetProcessEventLogging;
    private string _targetProcessEventLoggingProtocolFilter = "all";
    private bool _adaptiveRestartScheduled;
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

            TryCleanupStaleMonitorSessions();
            _sessionName = BuildSessionName();

            try
            {
                StartSessionCore(GetInitialAdaptiveBufferSize());
            }
            catch (COMException exception) when (exception.HResult == InsufficientResourcesHResult)
            {
                var sessionName = _sessionName;
                var requestedBufferSizeMb = GetInitialAdaptiveBufferSize();
                CleanupFailedStart(clearTotals: false);

                var cleanedSessionCount = TryCleanupStaleMonitorSessions();
                if (cleanedSessionCount > 0)
                {
                    logger.LogWarning(
                        exception,
                        "ETW network collector start failed due to insufficient resources. Cleaned {CleanedSessionCount} stale monitor ETW sessions and will retry once. SessionName: {SessionName}, BufferSizeMB: {BufferSizeMB}",
                        cleanedSessionCount,
                        sessionName,
                        requestedBufferSizeMb);

                    _sessionName = BuildSessionName();
                    try
                    {
                        StartSessionCore(requestedBufferSizeMb);
                        return Task.CompletedTask;
                    }
                    catch (COMException retryException) when (retryException.HResult == InsufficientResourcesHResult)
                    {
                        sessionName = _sessionName;
                        CleanupFailedStart(clearTotals: false);
                        logger.LogError(
                            retryException,
                            "Failed to start ETW network collector because ETW resources are insufficient even after cleanup retry. SessionName: {SessionName}, BufferSizeMB: {BufferSizeMB}. Consider releasing other ETW sessions or reducing load.",
                            sessionName,
                            requestedBufferSizeMb);
                        return Task.CompletedTask;
                    }
                }

                logger.LogError(
                    exception,
                    "Failed to start ETW network collector because ETW resources are insufficient. SessionName: {SessionName}, BufferSizeMB: {BufferSizeMB}. Consider releasing other ETW sessions or reducing load.",
                    sessionName,
                    requestedBufferSizeMb);
                return Task.CompletedTask;
            }
            catch (UnauthorizedAccessException exception)
            {
                var sessionName = _sessionName;
                CleanupFailedStart(clearTotals: false);
                logger.LogError(
                    exception,
                    "Failed to start ETW network collector due to insufficient privileges. SessionName: {SessionName}. Administrator rights are required to start the ETW kernel session.",
                    sessionName);
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                var sessionName = _sessionName;
                CleanupFailedStart(clearTotals: false);
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
        Task? adaptiveRestartTask;
        Task? sessionMonitorTask;
        string? sessionName;

        lock (_syncRoot)
        {
            if (!IsRunning && !_adaptiveRestartScheduled)
            {
                return;
            }

            RefreshLostEventsCore(logIfIncreased: true);
            processingTask = _processingTask;
            adaptiveRestartTask = _adaptiveRestartTask;
            sessionMonitorTask = _sessionMonitorTask;
            sessionName = _sessionName;
            _adaptiveRestartScheduled = false;
            StopSessionCore();
            _adaptiveRestartTask = null;
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

        if (sessionMonitorTask is not null)
        {
            try
            {
                await sessionMonitorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "ETW session monitor task ended with exception during stop.");
            }
        }

        if (adaptiveRestartTask is not null)
        {
            try
            {
                await adaptiveRestartTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "ETW adaptive restart task ended with exception during stop.");
            }
        }

        logger.LogInformation("ETW network collector stopped. SessionName: {SessionName}", sessionName);
    }

    public NetworkCollectorDiagnosticsSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            RefreshLostEventsCore(logIfIncreased: false);
            return new NetworkCollectorDiagnosticsSnapshot
            {
                IsRunning = IsRunning,
                SessionName = _sessionName,
                BufferSizeMb = _bufferSizeMb,
                StartedAt = _startedAt,
                PublishedEvents = Interlocked.Read(ref _sessionPublishedEvents),
                LostEvents = Interlocked.Read(ref _sessionLostEvents),
                TotalPublishedEvents = Interlocked.Read(ref _totalPublishedEvents),
                TotalLostEvents = Interlocked.Read(ref _totalLostEvents),
                AdaptiveRestartCount = _adaptiveRestartCount
            };
        }
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
                RefreshLostEventsCore(logIfIncreased: true);
                _adaptiveRestartScheduled = false;
                _sessionMonitorCancellation?.Cancel();
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
                _adaptiveRestartTask = null;
                _sessionMonitorTask = null;
                _sessionMonitorCancellation = null;
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

    private async Task MonitorSessionAsync(TraceEventSession session, CancellationTokenSource cancellationTokenSource)
    {
        using (cancellationTokenSource)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

            try
            {
                while (await timer.WaitForNextTickAsync(cancellationTokenSource.Token))
                {
                    lock (_syncRoot)
                    {
                        if (_disposed || !IsRunning || !ReferenceEquals(session, _session))
                        {
                            return;
                        }

                        RefreshLostEventsCore(session, logIfIncreased: true);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "ETW session monitor loop exited with exception.");
            }
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
        // NOTE:
        // For TcpIpRecv/TcpIpRecvIPV6, raw ETW field names (saddr/daddr, sport/dport)
        // suggest the classic packet-level meaning:
        //   saddr/sport = source/remote endpoint
        //   daddr/dport = destination/local endpoint
        //
        // However, during runtime verification on this machine, using that direct mapping
        // produced impossible socket tuples in logs, for example:
        //   - inbound chrome traffic showing Local=<public-ip>:443, Remote=<local-ip>:ephemeral
        //   - inbound verge-mihomo traffic showing Local=<client-ephemeral>, Remote=<local-listen-port>
        //
        // Swapping the mapping below restored socket tuples that match observed process
        // behavior (Local=local endpoint, Remote=peer endpoint) and fixed WAN/LAN download
        // classification. Because of that, this mapping is intentionally based on empirical
        // verification of TraceEvent's runtime behavior in this pipeline, not just on ETW
        // field-name intuition.
        Publish(new NetworkTraceEvent
        {
            Timestamp = new DateTimeOffset(data.TimeStamp),
            ProcessId = data.ProcessID,
            Direction = TrafficDirection.Inbound,
            ProtocolType = ProtocolType.Tcp,
            Bytes = data.size,
            LocalAddress = data.saddr?.ToString(),
            LocalPort = data.sport,
            RemoteAddress = data.daddr?.ToString(),
            RemotePort = data.dport,
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
        // See the note in PublishTcpRecv(...): although the raw ETW field names look like
        // packet source/destination fields, runtime validation showed that using them
        // directly produced reversed local/remote socket tuples for inbound TCP events in
        // this collector pipeline. Keep IPv6 behavior aligned with the empirically verified
        // IPv4 mapping.
        Publish(new NetworkTraceEvent
        {
            Timestamp = new DateTimeOffset(data.TimeStamp),
            ProcessId = data.ProcessID,
            Direction = TrafficDirection.Inbound,
            ProtocolType = ProtocolType.Tcp,
            Bytes = data.size,
            LocalAddress = data.saddr?.ToString(),
            LocalPort = data.sport,
            RemoteAddress = data.daddr?.ToString(),
            RemotePort = data.dport,
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

        Interlocked.Increment(ref _sessionPublishedEvents);
        Interlocked.Increment(ref _totalPublishedEvents);
        LogTargetProcessEvent(traceEvent);
        EventReceived?.Invoke(traceEvent);
    }

    private void LogTargetProcessEvent(NetworkTraceEvent traceEvent)
    {
        if (!_enableTargetProcessEventLogging)
        {
            return;
        }

        if (!ShouldLogProtocol(traceEvent.ProtocolType))
        {
            return;
        }

        var processName = GetCachedProcessName(traceEvent.ProcessId);
        if (string.IsNullOrWhiteSpace(processName) ||
            !Array.Exists(TargetProcessNames, target => string.Equals(target, processName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        int sequence;
        int remaining;
        bool completedAllTargets;
        lock (_syncRoot)
        {
            if (!_targetProcessLogCounts.TryGetValue(processName, out var currentCount) ||
                currentCount >= TargetProcessEventLogLimit)
            {
                return;
            }

            sequence = currentCount + 1;
            _targetProcessLogCounts[processName] = sequence;
            remaining = TargetProcessEventLogLimit - sequence;
            completedAllTargets = _targetProcessLogCounts.Values.All(count => count >= TargetProcessEventLogLimit);
        }

        logger.LogInformation(
            "TEMP ETW target event. Process={ProcessName}, Sequence={Sequence}/{TargetLimit}, Remaining={Remaining}, Timestamp={Timestamp:o}, PID={ProcessId}, Direction={Direction}, Protocol={ProtocolType}, Bytes={Bytes}, Local={LocalAddress}:{LocalPort}, Remote={RemoteAddress}:{RemotePort}, IPv6={IsIPv6}",
            processName,
            sequence,
            TargetProcessEventLogLimit,
            remaining,
            traceEvent.Timestamp,
            traceEvent.ProcessId,
            traceEvent.Direction,
            traceEvent.ProtocolType,
            traceEvent.Bytes,
            traceEvent.LocalAddress,
            traceEvent.LocalPort,
            traceEvent.RemoteAddress,
            traceEvent.RemotePort,
            traceEvent.IsIPv6);

        if (completedAllTargets)
        {
            logger.LogInformation(
                "TEMP ETW target logging completed. Captured {TargetLimit} events for each target process: {TargetProcesses}",
                TargetProcessEventLogLimit,
                string.Join(", ", TargetProcessNames));
        }
    }

    private string? GetCachedProcessName(int processId)
    {
        if (_targetProcessNameCache.TryGetValue(processId, out var cachedProcessName))
        {
            return cachedProcessName;
        }

        string? resolvedProcessName;
        try
        {
            using var process = Process.GetProcessById(processId);
            resolvedProcessName = process.ProcessName;
        }
        catch
        {
            resolvedProcessName = null;
        }

        _targetProcessNameCache[processId] = resolvedProcessName;
        return resolvedProcessName;
    }

    private bool ShouldLogProtocol(ProtocolType protocolType)
    {
        return _targetProcessEventLoggingProtocolFilter switch
        {
            "tcp" => protocolType == ProtocolType.Tcp,
            "udp" => protocolType == ProtocolType.Udp,
            _ => true
        };
    }

    private void RefreshLostEventsCore(bool logIfIncreased)
    {
        if (_session is not null)
        {
            RefreshLostEventsCore(_session, logIfIncreased);
        }
    }

    private void RefreshLostEventsCore(TraceEventSession session, bool logIfIncreased)
    {
        if (!ReferenceEquals(session, _session))
        {
            return;
        }

        long currentLostEvents;
        try
        {
            currentLostEvents = session.EventsLost;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to read ETW lost-events counter.");
            return;
        }

        var previousLostEvents = Interlocked.Read(ref _sessionLostEvents);
        if (currentLostEvents <= previousLostEvents)
        {
            return;
        }

        var delta = currentLostEvents - previousLostEvents;
        Interlocked.Exchange(ref _sessionLostEvents, currentLostEvents);
        Interlocked.Add(ref _totalLostEvents, delta);

        if (logIfIncreased)
        {
            var lastLoggedLostEvents = Interlocked.Read(ref _lastLoggedLostEvents);
            if (currentLostEvents > lastLoggedLostEvents)
            {
                Interlocked.Exchange(ref _lastLoggedLostEvents, currentLostEvents);
                var publishedEvents = Interlocked.Read(ref _sessionPublishedEvents);
                var totalObserved = publishedEvents + currentLostEvents;
                var lossRate = totalObserved > 0
                    ? currentLostEvents * 100d / totalObserved
                    : 0d;

                logger.LogWarning(
                    "ETW network collector lost events detected. SessionName: {SessionName}, BufferSizeMB: {BufferSizeMB}, LostEvents: {LostEvents}, PublishedEvents: {PublishedEvents}, LossRate: {LossRate:F4}%",
                    _sessionName,
                    _bufferSizeMb,
                    currentLostEvents,
                    publishedEvents,
                    lossRate);
            }
        }

        ScheduleAdaptiveBufferIncreaseCore();
    }

    private void ScheduleAdaptiveBufferIncreaseCore()
    {
        if (_adaptiveRestartScheduled || !IsRunning || _session is null)
        {
            return;
        }

        var nextBufferSizeMb = GetNextAdaptiveBufferSize(_bufferSizeMb);
        if (nextBufferSizeMb <= _bufferSizeMb)
        {
            return;
        }

        _adaptiveRestartScheduled = true;
        logger.LogWarning(
            "ETW network collector will increase buffer size from {CurrentBufferSizeMB}MB to {NextBufferSizeMB}MB because lost events were detected.",
            _bufferSizeMb,
            nextBufferSizeMb);

        _adaptiveRestartTask = Task.Run(() => IncreaseBufferAsync(nextBufferSizeMb));
    }

    private async Task IncreaseBufferAsync(int targetBufferSizeMb)
    {
        Task? previousProcessingTask = null;
        Task? previousSessionMonitorTask = null;
        string? previousSessionName = null;

        try
        {
            lock (_syncRoot)
            {
                if (_disposed || !IsRunning || _session is null || targetBufferSizeMb <= _bufferSizeMb)
                {
                    _adaptiveRestartScheduled = false;
                    _adaptiveRestartTask = null;
                    return;
                }

                previousProcessingTask = _processingTask;
                previousSessionMonitorTask = _sessionMonitorTask;
                previousSessionName = _sessionName;
                StopSessionCore();
                _sessionName = BuildSessionName();
                StartSessionCore(targetBufferSizeMb);
                _adaptiveRestartCount++;
                _adaptiveRestartScheduled = false;
                _adaptiveRestartTask = null;
            }

            if (previousProcessingTask is not null)
            {
                try
                {
                    await previousProcessingTask;
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Previous ETW processing task ended with exception after adaptive buffer restart.");
                }
            }

            if (previousSessionMonitorTask is not null)
            {
                try
                {
                    await previousSessionMonitorTask;
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Previous ETW session monitor task ended with exception after adaptive buffer restart.");
                }
            }

            logger.LogInformation(
                "ETW network collector buffer increased adaptively from previous session {PreviousSessionName} to {CurrentBufferSizeMB}MB.",
                previousSessionName,
                targetBufferSizeMb);
        }
        catch (Exception exception)
        {
            lock (_syncRoot)
            {
                CleanupFailedStart(clearTotals: false);
            }

            logger.LogError(
                exception,
                "Failed to adaptively restart ETW network collector with buffer size {TargetBufferSizeMB}MB.",
                targetBufferSizeMb);
        }
    }

    private void StopSessionCore()
    {
        _sessionMonitorCancellation?.Cancel();
        _sessionMonitorCancellation = null;

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
        _sessionMonitorTask = null;
        _sessionName = null;
        _startedAt = null;
        _bufferSizeMb = 0;
        IsRunning = false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void CleanupFailedStart(bool clearTotals)
    {
        _sessionMonitorCancellation?.Cancel();
        _sessionMonitorCancellation = null;
        _session?.Dispose();
        _session = null;
        _processingTask = null;
        _sessionMonitorTask = null;
        _sessionName = null;
        _startedAt = null;
        _bufferSizeMb = 0;
        _adaptiveRestartScheduled = false;
        _adaptiveRestartTask = null;
        Interlocked.Exchange(ref _sessionPublishedEvents, 0);
        Interlocked.Exchange(ref _sessionLostEvents, 0);
        Interlocked.Exchange(ref _lastLoggedLostEvents, 0);

        if (clearTotals)
        {
            Interlocked.Exchange(ref _totalPublishedEvents, 0);
            Interlocked.Exchange(ref _totalLostEvents, 0);
            _adaptiveRestartCount = 0;
        }
    }

    private void StartSessionCore(int bufferSizeMb)
    {
        var sessionOptions = TraceEventSessionOptions.Create |
                             TraceEventSessionOptions.NoPerProcessorBuffering;

        var session = new TraceEventSession(_sessionName, sessionOptions);
        session.StopOnDispose = true;
        session.BufferSizeMB = bufferSizeMb;
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        RegisterHandlers(session.Source.Kernel);

        var sessionMonitorCancellation = new CancellationTokenSource();

        _session = session;
        _startedAt = DateTimeOffset.UtcNow;
        _bufferSizeMb = bufferSizeMb;
        _sessionMonitorCancellation = sessionMonitorCancellation;
        var currentSettings = settingsMonitor.Current;
        _enableTargetProcessEventLogging = currentSettings.EnableEtwTargetEventLogging;
        _targetProcessEventLoggingProtocolFilter = currentSettings.EtwTargetEventLoggingProtocolFilter?.Trim().ToLowerInvariant() switch
        {
            "tcp" => "tcp",
            "udp" => "udp",
            _ => "all"
        };
        Interlocked.Exchange(ref _sessionPublishedEvents, 0);
        Interlocked.Exchange(ref _sessionLostEvents, 0);
        Interlocked.Exchange(ref _lastLoggedLostEvents, 0);
        if (_enableTargetProcessEventLogging && Interlocked.Read(ref _totalPublishedEvents) == 0)
        {
            _targetProcessNameCache.Clear();
            lock (_syncRoot)
            {
                foreach (var processName in TargetProcessNames)
                {
                    _targetProcessLogCounts[processName] = 0;
                }
            }
        }

        _processingTask = Task.Factory.StartNew(
            () => ProcessSession(session, logger),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _sessionMonitorTask = Task.Run(() => MonitorSessionAsync(session, sessionMonitorCancellation));

        IsRunning = true;
        logger.LogInformation(
            "ETW network collector started. SessionName: {SessionName}, BufferSizeMB: {BufferSizeMB}, SessionOptions: {SessionOptions}, EnableEtwTargetEventLogging: {EnableEtwTargetEventLogging}, EtwTargetEventLoggingProtocolFilter: {EtwTargetEventLoggingProtocolFilter}",
            _sessionName,
            bufferSizeMb,
            sessionOptions,
            _enableTargetProcessEventLogging,
            _targetProcessEventLoggingProtocolFilter);
    }

    private int GetInitialAdaptiveBufferSize()
    {
        var configuredBufferSizeMb = settingsMonitor.Current.EtwBufferSizeMb;
        foreach (var candidate in AdaptiveBufferSizesMb)
        {
            if (configuredBufferSizeMb <= candidate)
            {
                return candidate;
            }
        }

        return AdaptiveBufferSizesMb[^1];
    }

    private static int GetNextAdaptiveBufferSize(int currentBufferSizeMb)
    {
        foreach (var candidate in AdaptiveBufferSizesMb)
        {
            if (candidate > currentBufferSizeMb)
            {
                return candidate;
            }
        }

        return currentBufferSizeMb;
    }

    private static string BuildSessionName()
    {
        return $"{SessionNamePrefix}{Environment.ProcessId}.{Guid.NewGuid():N}";
    }

    private int TryCleanupStaleMonitorSessions()
    {
        try
        {
            var cleanedCount = 0;
            foreach (var sessionName in TraceEventSession.GetActiveSessionNames()
                         .Where(name => name.StartsWith(SessionNamePrefix, StringComparison.OrdinalIgnoreCase))
                         .Where(name => !string.Equals(name, _sessionName, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var activeSession = TraceEventSession.GetActiveSession(sessionName);
                    if (activeSession is null)
                    {
                        continue;
                    }

                    activeSession.Stop();
                    cleanedCount++;
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Failed to cleanup stale ETW session {SessionName}.", sessionName);
                }
            }

            if (cleanedCount > 0)
            {
                logger.LogInformation("Cleaned {CleanedSessionCount} stale monitor ETW sessions before start.", cleanedCount);
            }

            return cleanedCount;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed while enumerating active ETW sessions for cleanup.");
            return 0;
        }
    }
}
