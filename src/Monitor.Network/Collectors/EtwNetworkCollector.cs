using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
public sealed class EtwNetworkCollector : INetworkCollector, INetworkCollectorDiagnostics, IDisposable
{
    private readonly ILogger<EtwNetworkCollector> logger;
    private readonly IMonitorSettingsProvider settingsMonitor;
    private readonly Func<string, int, TraceEventSessionOptions, IEtwSession> _sessionFactory;
    private readonly TimeSpan _initialRecoveryDelay;
    private readonly bool _cleanupSessions;
    private readonly HashSet<string> _ownedSessionNames = new(StringComparer.OrdinalIgnoreCase);

    public EtwNetworkCollector(ILogger<EtwNetworkCollector> logger, IMonitorSettingsProvider settingsMonitor)
        : this(logger, settingsMonitor, static (name, size, options) => new EtwSession(name, size, options), TimeSpan.FromSeconds(1), true)
    {
    }

    internal EtwNetworkCollector(ILogger<EtwNetworkCollector> logger, IMonitorSettingsProvider settingsMonitor,
        Func<string, int, TraceEventSessionOptions, IEtwSession> sessionFactory, TimeSpan initialRecoveryDelay, bool cleanupSessions)
    {
        this.logger = logger;
        this.settingsMonitor = settingsMonitor;
        _sessionFactory = sessionFactory;
        _initialRecoveryDelay = initialRecoveryDelay;
        _cleanupSessions = cleanupSessions;
    }
    private static readonly int[] AdaptiveBufferSizesMb = [4, 8, 16];
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
    private IEtwSession? _session;
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
    private bool _stopRequested;
    private bool _everStarted;
    private int _desiredBufferSizeMb;
    private int _consecutiveFailures;
    private DateTimeOffset? _lastFailureAt;
    private DateTimeOffset? _lastRecoveryAt;
    private CancellationTokenSource? _recoveryCancellation;
    private Task? _recoveryTask;
    private Task? _stopTask;
    private long _sessionGeneration;
    private long _processingExitedGeneration;
    private int _isRunning;

    public event Action<NetworkTraceEvent>? EventReceived;

    public bool IsRunning
    {
        get => Volatile.Read(ref _isRunning) != 0 &&
               Interlocked.Read(ref _processingExitedGeneration) != Interlocked.Read(ref _sessionGeneration);
        private set => Volatile.Write(ref _isRunning, value ? 1 : 0);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            // A stopped collector cannot be restarted accidentally during host shutdown.
            if (_stopRequested || _everStarted)
            {
                return Task.CompletedTask;
            }

            _everStarted = true;
            _desiredBufferSizeMb = GetInitialAdaptiveBufferSize();
            _recoveryCancellation = new CancellationTokenSource();
            TryCleanupStaleMonitorSessions();
            TryStartSessionCore();
        }

        return Task.CompletedTask;
    }

    private void TryStartSessionCore()
    {
        if (_stopRequested || _disposed)
        {
            return;
        }

        _sessionName = BuildSessionName();
        try
        {
            StartSessionCore(_desiredBufferSizeMb);
            if (_consecutiveFailures > 0)
            {
                _lastRecoveryAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception exception)
        {
            CleanupFailedStart(clearTotals: false);
            RecordFailureAndScheduleRecoveryCore(exception, "Failed to start ETW network collector.");
        }
    }

    private void RecordFailureAndScheduleRecoveryCore(Exception? exception, string message)
    {
        _consecutiveFailures++;
        _lastFailureAt = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromMilliseconds(Math.Min(60_000, _initialRecoveryDelay.TotalMilliseconds * (1 << Math.Min(_consecutiveFailures - 1, 6))));
        logger.LogWarning(exception, "{Message} Retrying in {Delay}; consecutive failures: {ConsecutiveFailures}.", message, delay, _consecutiveFailures);
        if (_stopRequested || _disposed || _recoveryCancellation is null)
        {
            return;
        }

        var token = _recoveryCancellation.Token;
        _recoveryTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token);
                lock (_syncRoot)
                {
                    if (!token.IsCancellationRequested && !_stopRequested && !_disposed && !IsRunning)
                    {
                        TryCleanupStaleMonitorSessions();
                        TryStartSessionCore();
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        });
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_syncRoot)
        {
            _stopTask ??= StopCoreAsync();
            stopTask = _stopTask;
        }

        return stopTask.WaitAsync(cancellationToken);
    }

    private async Task StopCoreAsync()
    {
        Task? processingTask;
        Task? adaptiveRestartTask;
        Task? sessionMonitorTask;
        Task? recoveryTask;
        string? sessionName;

        lock (_syncRoot)
        {
            // Mark the collector terminal before releasing the lock, including failed starts.
            _stopRequested = true;
            _recoveryCancellation?.Cancel();
            recoveryTask = _recoveryTask;
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
                await processingTask;
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
                await sessionMonitorTask;
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
                await adaptiveRestartTask;
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

        if (recoveryTask is not null)
        {
            await recoveryTask;
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
                AdaptiveRestartCount = _adaptiveRestartCount,
                IsRecovering = !_stopRequested && _everStarted && !IsRunning && _consecutiveFailures > 0,
                IsStopping = _stopRequested,
                ConsecutiveFailures = _consecutiveFailures,
                LastFailureAt = _lastFailureAt,
                LastRecoveryAt = _lastRecoveryAt
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
                _stopRequested = true;
                _recoveryCancellation?.Cancel();
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

    private void ProcessSession(IEtwSession session, long generation)
    {
        Exception? failure = null;
        try
        {
            session.Process();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            InterlockedExtensionsMax(ref _processingExitedGeneration, generation);
            lock (_syncRoot)
            {
                // Intentional stop/restart replaces or detaches the session before Process exits.
                if (ReferenceEquals(session, _session) && !_stopRequested && !_disposed)
                {
                    RefreshLostEventsCore(logIfIncreased: false);
                    StopSessionCore();
                    RecordFailureAndScheduleRecoveryCore(failure, "ETW processing loop exited unexpectedly.");
                }
            }
        }
    }

    private async Task MonitorSessionAsync(IEtwSession session, CancellationTokenSource cancellationTokenSource)
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
                        if (_startedAt is { } startedAt && DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromSeconds(30))
                        {
                            _consecutiveFailures = 0;
                        }
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
            LocalAddress = data.saddr,
            LocalPort = data.sport,
            RemoteAddress = data.daddr,
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
            LocalAddress = data.saddr,
            LocalPort = data.sport,
            RemoteAddress = data.daddr,
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
            LocalAddress = data.saddr,
            LocalPort = data.sport,
            RemoteAddress = data.daddr,
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
            LocalAddress = data.saddr,
            LocalPort = data.sport,
            RemoteAddress = data.daddr,
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
            LocalAddress = direction == TrafficDirection.Outbound ? data.saddr : data.daddr,
            LocalPort = direction == TrafficDirection.Outbound ? data.sport : data.dport,
            RemoteAddress = direction == TrafficDirection.Outbound ? data.daddr : data.saddr,
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
            LocalAddress = direction == TrafficDirection.Outbound ? data.saddr : data.daddr,
            LocalPort = direction == TrafficDirection.Outbound ? data.sport : data.dport,
            RemoteAddress = direction == TrafficDirection.Outbound ? data.daddr : data.saddr,
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

    private void RefreshLostEventsCore(IEtwSession session, bool logIfIncreased)
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
        if (_stopRequested || _adaptiveRestartScheduled || !IsRunning || _session is null)
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
        long restartGeneration = 0;

        try
        {
            lock (_syncRoot)
            {
                if (_disposed || _stopRequested || !IsRunning || _session is null || targetBufferSizeMb <= _bufferSizeMb)
                {
                    _adaptiveRestartScheduled = false;
                    _adaptiveRestartTask = null;
                    return;
                }

                previousProcessingTask = _processingTask;
                previousSessionMonitorTask = _sessionMonitorTask;
                previousSessionName = _sessionName;
                StopSessionCore();
                _desiredBufferSizeMb = targetBufferSizeMb;
                _sessionName = BuildSessionName();
                restartGeneration = _sessionGeneration + 1;
                StartSessionCore(targetBufferSizeMb);
                _adaptiveRestartCount++;
                _adaptiveRestartScheduled = false;
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
                // A later stop/recovery must not be cleaned up by this stale restart.
                if (!_stopRequested && !_disposed && restartGeneration == _sessionGeneration)
                {
                    CleanupFailedStart(clearTotals: false);
                    RecordFailureAndScheduleRecoveryCore(exception, "Adaptive ETW restart failed.");
                }
            }
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

        try
        {
            _session?.Dispose();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Ignoring ETW session dispose exception.");
        }
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
        try
        {
            _session?.Dispose();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Ignoring ETW session dispose exception.");
        }
        _session = null;
        _processingTask = null;
        _sessionMonitorTask = null;
        _sessionName = null;
        _startedAt = null;
        _bufferSizeMb = 0;
        IsRunning = false;
        _adaptiveRestartScheduled = false;
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
        Interlocked.Increment(ref _sessionGeneration);
        var sessionOptions = TraceEventSessionOptions.Create |
                             TraceEventSessionOptions.NoPerProcessorBuffering;

        _ownedSessionNames.Add(_sessionName!);
        var session = _sessionFactory(_sessionName!, bufferSizeMb, sessionOptions);
        // Publish ownership immediately so failed handler initialization is cleaned up.
        _session = session;
        session.RegisterHandlers(RegisterHandlers);

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

        var generation = _sessionGeneration;
        _processingTask = Task.Factory.StartNew(
            () => ProcessSession(session, generation),
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

    private static void InterlockedExtensionsMax(ref long location, long value)
    {
        var current = Interlocked.Read(ref location);
        while (current < value)
        {
            var previous = Interlocked.CompareExchange(ref location, value, current);
            if (previous == current) return;
            current = previous;
        }
    }

    private static string BuildSessionName()
    {
        return $"{SessionNamePrefix}{Environment.ProcessId}.{Guid.NewGuid():N}";
    }

    private int TryCleanupStaleMonitorSessions()
    {
        if (!_cleanupSessions) return 0;
        try
        {
            var activeSessionNames = TraceEventSession.GetActiveSessionNames().ToArray();
            _ownedSessionNames.RemoveWhere(name => !activeSessionNames.Contains(name, StringComparer.OrdinalIgnoreCase));
            var cleanedCount = CleanupStaleMonitorSessions(
                activeSessionNames, _sessionName, IsProcessAlive, sessionName =>
                {
                    using var activeSession = TraceEventSession.GetActiveSession(sessionName);
                    if (activeSession is null) return false;
                    activeSession.Stop();
                    return true;
                }, _ownedSessionNames);

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

    // null means that process state cannot be determined; in that case leave the session alone.
    internal static int CleanupStaleMonitorSessions(IEnumerable<string> sessionNames, string? currentSession,
        Func<int, bool?> processAlive, Func<string, bool> stopSession, IReadOnlySet<string>? ownedSessionNames = null)
    {
        var cleanedCount = 0;
        foreach (var sessionName in sessionNames)
        {
            if (string.Equals(sessionName, currentSession, StringComparison.OrdinalIgnoreCase) ||
                !TryGetSessionProcessId(sessionName, out var pid))
            {
                continue;
            }

            try
            {
                // A name created by this collector is safe to retry cleaning. Otherwise
                // any live PID (including a reused PID) is conservatively untouched.
                var owned = ownedSessionNames?.Contains(sessionName) == true;
                if ((owned || processAlive(pid) == false) && stopSession(sessionName))
                {
                    cleanedCount++;
                }
            }
            catch
            {
                // Access denied, inspection races or stop failures are not proof of death.
            }
        }
        return cleanedCount;
    }

    private static bool TryGetSessionProcessId(string sessionName, out int pid)
    {
        pid = 0;
        if (!sessionName.StartsWith(SessionNamePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = sessionName.AsSpan(SessionNamePrefix.Length);
        var dot = suffix.IndexOf('.');
        return dot > 0 && dot < suffix.Length - 1 &&
               int.TryParse(suffix[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0 &&
               Guid.TryParseExact(suffix[(dot + 1)..], "N", out _);
    }

    private static bool? IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById could not find this PID.
            return false;
        }
        catch
        {
            return null;
        }
    }
}
