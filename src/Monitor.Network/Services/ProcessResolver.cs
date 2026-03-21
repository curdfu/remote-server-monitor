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
    private readonly ConcurrentDictionary<int, CacheItem> _cache = new();

    public ResolvedProcessInfo Resolve(int pid)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(pid, out var cached) && now - cached.StoredAt <= CacheLifetime)
        {
            return cached.Value;
        }

        var resolved = ResolveCore(pid, now);
        _cache[pid] = new CacheItem(resolved, now);
        return resolved;
    }

    private ResolvedProcessInfo ResolveCore(int pid, DateTimeOffset resolvedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var processName = process.ProcessName;
            var executablePath = TryGetExecutablePath(process);
            var displayName = TryGetDisplayName(executablePath);

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

    private static string? TryGetDisplayName(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return string.IsNullOrWhiteSpace(info.FileDescription) ? null : info.FileDescription;
        }
        catch
        {
            return null;
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
}
