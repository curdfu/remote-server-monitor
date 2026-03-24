using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Monitor.Network.Abstractions;
using Monitor.Network.Models;

namespace Monitor.Network.Services;

public sealed class ProcessResolver(ILogger<ProcessResolver> logger) : IProcessResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisplayNameCacheLifetime = TimeSpan.FromHours(6);
    private const int PruneFrequency = 256;

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

