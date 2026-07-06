using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Monitor.Network.Abstractions;
using Monitor.Network.Models;

namespace Monitor.Network.Services;

// ProcessResolver 把 ETW 里的 PID 转成稳定的应用身份。
// 进程可能快速退出或权限不足导致路径不可读，所以所有进程访问都必须可失败，并提供 pid-* 兜底名称。
public sealed class ProcessResolver(ILogger<ProcessResolver> logger) : IProcessResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisplayNameCacheLifetime = TimeSpan.FromHours(6);
    private const int PruneFrequency = 256;

    // PID 会被系统复用，所以 PID 缓存只保留很短时间；显示名来自文件版本信息，变化少，可以缓存更久。
    private readonly ConcurrentDictionary<int, CacheItem> _cache = new();
    private readonly ConcurrentDictionary<string, DisplayNameCacheItem> _displayNameCache = new(StringComparer.OrdinalIgnoreCase);
    private int _resolveCount;

    public ResolvedProcessInfo Resolve(int pid)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(pid, out var cached) && now - cached.StoredAt <= CacheLifetime)
        {
            return cached.Value;
        }

        var resolved = ResolveCore(pid, now);
        _cache[pid] = new CacheItem(resolved, now);
        PruneIfNeeded(now);
        return resolved;
    }

    private ResolvedProcessInfo ResolveCore(int pid, DateTimeOffset resolvedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var processName = process.ProcessName;
            var executablePath = TryGetExecutablePath(process);
            var displayName = TryGetDisplayName(executablePath, resolvedAt);

            return new ResolvedProcessInfo
            {
                ProcessId = pid,
                AppKey = BuildAppKey(processName, executablePath),
                ProcessName = processName,
                DisplayName = displayName,
                ExecutablePath = executablePath,
                ResolvedAt = resolvedAt
            };
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to resolve process metadata for pid {Pid}.", pid);

            var fallbackName = $"pid-{pid}";
            return new ResolvedProcessInfo
            {
                ProcessId = pid,
                AppKey = BuildAppKey(fallbackName, null),
                ProcessName = fallbackName,
                ResolvedAt = resolvedAt
            };
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        // 访问 MainModule 可能因权限、架构或进程退出失败；路径缺失时 appKey 会退回到进程名。
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private string? TryGetDisplayName(string? executablePath, DateTimeOffset resolvedAt)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        if (_displayNameCache.TryGetValue(executablePath, out var cached) &&
            resolvedAt - cached.StoredAt <= DisplayNameCacheLifetime)
        {
            return cached.Value;
        }

        string? displayName;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            displayName = string.IsNullOrWhiteSpace(info.FileDescription) ? null : info.FileDescription;
        }
        catch
        {
            displayName = null;
        }

        _displayNameCache[executablePath] = new DisplayNameCacheItem(displayName, resolvedAt);
        return displayName;
    }

    private void PruneIfNeeded(DateTimeOffset now)
    {
        // 高频 ETW 事件不能每次都扫描缓存，按访问次数分摊清理成本。
        if (Interlocked.Increment(ref _resolveCount) % PruneFrequency != 0)
        {
            return;
        }

        foreach (var pair in _cache)
        {
            if (now - pair.Value.StoredAt > CacheLifetime)
            {
                _cache.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in _displayNameCache)
        {
            if (now - pair.Value.StoredAt > DisplayNameCacheLifetime)
            {
                _displayNameCache.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string BuildAppKey(string processName, string? executablePath)
    {
        // 优先用可执行文件绝对路径构建 appKey，同名不同路径的程序会被区分；路径不可用时才退回进程名。
        var normalizedPath = string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : Path.GetFullPath(executablePath).Trim().ToLowerInvariant();

        var raw = string.IsNullOrWhiteSpace(normalizedPath)
            ? processName.Trim().ToLowerInvariant()
            : normalizedPath;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    private sealed record CacheItem(ResolvedProcessInfo Value, DateTimeOffset StoredAt);
    private sealed record DisplayNameCacheItem(string? Value, DateTimeOffset StoredAt);
}

