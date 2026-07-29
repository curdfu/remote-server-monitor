using System.Collections.Concurrent;
using Monitor.Network.Abstractions;
using Monitor.Network.Models;

namespace Monitor.Network.Services;

// AppRegistryService 是内存态应用注册表，把短生命周期 PID 映射到稳定 appKey。
// 聚合器只需要 appKey 和应用元数据快照，真正落库由 NetworkTrafficRepository 在保存 bucket 时完成。
public sealed class AppRegistryService(IProcessResolver processResolver) : IAppRegistry
{
    private static readonly TimeSpan PidCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PidTouchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, AppRegistryState> _entries = new(StringComparer.OrdinalIgnoreCase);
    // PID 缓存只做短期加速，避免同一个进程的高频网络事件反复解析进程信息。
    private readonly ConcurrentDictionary<int, PidCacheItem> _pidCache = new();
    private long _nextPruneAtUtcTicks;

    public AppRegistryEntry GetOrAdd(int pid)
    {
        var now = DateTimeOffset.UtcNow;
        if (_pidCache.TryGetValue(pid, out var pidCache) && now - pidCache.LastSeenAt <= PidCacheLifetime)
        {
            // LastSeen 只用于应用元数据，不参与字节统计；每 PID 每秒更新一次即可，
            // 避免高包率进程在每个 ETW 事件上进入状态锁。
            if (pidCache.TryTouch(now))
            {
                pidCache.State.Touch(now);
            }

            PruneIfNeeded(now);
            return pidCache.Entry;
        }

        var resolved = processResolver.Resolve(pid);
        // appKey 相同代表同一个应用身份；后续解析到更完整的显示名/路径时更新已有状态。
        var state = _entries.AddOrUpdate(
            resolved.AppKey,
            _ => AppRegistryState.Create(resolved),
            (_, existing) =>
            {
                existing.Update(resolved);
                return existing;
            });

        state.Touch(resolved.ResolvedAt);
        var snapshot = state.CreateSnapshot();
        _pidCache[pid] = new PidCacheItem(snapshot, state, resolved.ResolvedAt);
        PruneIfNeeded(now);
        return snapshot;
    }

    public AppRegistryEntry? GetByAppKey(string appKey)
    {
        return _entries.TryGetValue(appKey, out var entry)
            ? entry.CreateSnapshot()
            : null;
    }

    public IReadOnlyList<AppRegistryEntry> GetAll()
    {
        return _entries.Values
            .Select(static entry => entry.CreateSnapshot())
            .OrderByDescending(static entry => entry.LastSeenAt)
            .ToArray();
    }

    private void PruneIfNeeded(DateTimeOffset now)
    {
        // 注册表是运行期缓存，不无限增长；按时间触发清理，避免事件率越高全表扫描越频繁。
        var nextPruneAtUtcTicks = Volatile.Read(ref _nextPruneAtUtcTicks);
        if (now.UtcTicks < nextPruneAtUtcTicks)
        {
            return;
        }

        var updatedNextPruneAtUtcTicks = now.Add(PruneInterval).UtcTicks;
        if (Interlocked.CompareExchange(
                ref _nextPruneAtUtcTicks,
                updatedNextPruneAtUtcTicks,
                nextPruneAtUtcTicks) != nextPruneAtUtcTicks)
        {
            return;
        }

        foreach (var pair in _pidCache)
        {
            if (now - pair.Value.LastSeenAt > PidCacheLifetime)
            {
                _pidCache.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in _entries)
        {
            if (now - pair.Value.LastSeenAt > EntryLifetime)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class PidCacheItem(AppRegistryEntry entry, AppRegistryState state, DateTimeOffset lastSeenAt)
    {
        private long _lastSeenAtUtcTicks = lastSeenAt.UtcTicks;

        public AppRegistryEntry Entry { get; } = entry;
        public AppRegistryState State { get; } = state;
        public DateTimeOffset LastSeenAt =>
            new(Volatile.Read(ref _lastSeenAtUtcTicks), TimeSpan.Zero);

        public bool TryTouch(DateTimeOffset timestamp)
        {
            var currentTicks = Volatile.Read(ref _lastSeenAtUtcTicks);
            if (timestamp.UtcTicks - currentTicks < PidTouchInterval.Ticks)
            {
                return false;
            }

            return Interlocked.CompareExchange(
                       ref _lastSeenAtUtcTicks,
                       timestamp.UtcTicks,
                       currentTicks) == currentTicks;
        }
    }

    private sealed class AppRegistryState
    {
        private readonly object _syncRoot = new();

        private AppRegistryState(
            string appKey,
            string processName,
            string? displayName,
            string? executablePath,
            DateTimeOffset firstSeenAt,
            DateTimeOffset lastSeenAt)
        {
            AppKey = appKey;
            ProcessName = processName;
            DisplayName = displayName;
            ExecutablePath = executablePath;
            FirstSeenAt = firstSeenAt;
            LastSeenAt = lastSeenAt;
        }

        public string AppKey { get; }
        public string ProcessName { get; private set; }
        public string? DisplayName { get; private set; }
        public string? ExecutablePath { get; private set; }
        public DateTimeOffset FirstSeenAt { get; }
        public DateTimeOffset LastSeenAt { get; private set; }

        public static AppRegistryState Create(ResolvedProcessInfo resolved)
        {
            return new AppRegistryState(
                resolved.AppKey,
                resolved.ProcessName,
                resolved.DisplayName,
                resolved.ExecutablePath,
                resolved.ResolvedAt,
                resolved.ResolvedAt);
        }

        public void Update(ResolvedProcessInfo resolved)
        {
            // 更新只覆盖非空字段，避免一次权限受限的解析把已有显示名或可执行路径清掉。
            lock (_syncRoot)
            {
                if (!string.IsNullOrWhiteSpace(resolved.ProcessName))
                {
                    ProcessName = resolved.ProcessName;
                }

                if (!string.IsNullOrWhiteSpace(resolved.DisplayName))
                {
                    DisplayName = resolved.DisplayName;
                }

                if (!string.IsNullOrWhiteSpace(resolved.ExecutablePath))
                {
                    ExecutablePath = resolved.ExecutablePath;
                }

                if (resolved.ResolvedAt > LastSeenAt)
                {
                    LastSeenAt = resolved.ResolvedAt;
                }
            }
        }

        public void Touch(DateTimeOffset timestamp)
        {
            lock (_syncRoot)
            {
                if (timestamp > LastSeenAt)
                {
                    LastSeenAt = timestamp;
                }
            }
        }

        public AppRegistryEntry CreateSnapshot()
        {
            lock (_syncRoot)
            {
                return new AppRegistryEntry
                {
                    AppKey = AppKey,
                    ProcessName = ProcessName,
                    DisplayName = DisplayName,
                    ExecutablePath = ExecutablePath,
                    FirstSeenAt = FirstSeenAt,
                    LastSeenAt = LastSeenAt
                };
            }
        }
    }
}
