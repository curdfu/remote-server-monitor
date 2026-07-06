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

// ETW 采集器直接管理 Windows 内核网络 TraceEvent 会话，是整条网络链路的最底层入口。
// 这里的目标是尽量可靠地把 TCP/UDP 事件转换成统一的 NetworkTraceEvent，后续进程解析、地址分类、聚合和落库都不在本类完成。
// 因为 ETW 资源是系统级共享资源，本类特别处理了启动失败、陈旧会话清理、丢事件监控和自适应 buffer 重启。
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
    // 目标进程事件日志是临时诊断通道，缓存进程名可避免在 ETW 高频回调路径反复打开进程句柄。
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

            // 进程异常退出后可能残留同前缀 ETW 会话；启动前先清理，减少系统 ETW 资源不足的概率。
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

                // 资源不足时只做一次清理重试，避免服务启动阶段进入无限重试并持续占用系统资源。
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

            // 停止前最后读取一次 EventsLost，保证诊断快照和日志包含本会话结束前的丢事件统计。
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
        // TCP/UDP、IPv4/IPv6 事件在这里统一归一化成 NetworkTraceEvent，供上层聚合器按同一模型消费。
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
        // Source.Process() 是阻塞式 ETW 消费循环，必须放到 LongRunning 后台任务中运行。
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
        // TraceEvent 不会主动推送 EventsLost 变化；额外轮询用于及时发现 buffer 不足并触发自适应重启。
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
        // TcpIpRecv/TcpIpRecvIPV6 的原始字段名看起来像经典包方向语义：
        // saddr/sport 表示源端，daddr/dport 表示目标端。
        // 但在本采集链路的运行验证中，直接按字段名映射会生成不可能的 socket 元组，
        // 例如入站 chrome 流量出现 Local=<public-ip>:443、Remote=<local-ip>:ephemeral。
        // 当前映射以 TraceEvent 在本管线里的实测行为为准，确保 Local 表示本机端点、Remote 表示对端，
        // 否则 WAN/LAN 下载方向分类会被反转。
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
        // 与 IPv4 入站 TCP 保持同一套实测映射规则，避免 IPv6 下载流量的本地/远端端点方向不一致。
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
        // ETW 偶尔会出现无进程或异常字节数事件；这里直接丢弃，避免污染应用维度统计。
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

        // 只要检测到丢事件，就尝试提升 buffer；实际是否重启由 ScheduleAdaptiveBufferIncreaseCore 去重和限档。
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
        // 自适应扩容通过新建 ETW 会话完成。旧任务在锁外等待，避免 Stop/Start 期间阻塞采集器状态锁。
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
        // 每次启动都使用唯一会话名，避免和旧进程或并行实例的内核会话互相覆盖。
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
        // 只清理本应用前缀的 ETW 会话，不能影响其他工具或系统组件正在使用的 TraceEvent 会话。
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
