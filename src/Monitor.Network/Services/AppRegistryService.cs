using System.Collections.Concurrent;
using Monitor.Network.Abstractions;
using Monitor.Network.Models;

namespace Monitor.Network.Services;

public sealed class AppRegistryService(IProcessResolver processResolver) : IAppRegistry
{
    private readonly ConcurrentDictionary<string, AppRegistryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public AppRegistryEntry GetOrAdd(int pid)
    {
        var resolved = processResolver.Resolve(pid);

        return _entries.AddOrUpdate(
            resolved.AppKey,
            _ => new AppRegistryEntry
            {
                AppKey = resolved.AppKey,
                ProcessName = resolved.ProcessName,
                DisplayName = resolved.DisplayName,
                ExecutablePath = resolved.ExecutablePath,
                FirstSeenAt = resolved.ResolvedAt,
                LastSeenAt = resolved.ResolvedAt
            },
            (_, existing) => new AppRegistryEntry
            {
                AppKey = existing.AppKey,
                ProcessName = string.IsNullOrWhiteSpace(resolved.ProcessName) ? existing.ProcessName : resolved.ProcessName,
                DisplayName = resolved.DisplayName ?? existing.DisplayName,
                ExecutablePath = resolved.ExecutablePath ?? existing.ExecutablePath,
                FirstSeenAt = existing.FirstSeenAt,
                LastSeenAt = resolved.ResolvedAt
            });
    }

    public AppRegistryEntry? GetByAppKey(string appKey)
    {
        return _entries.TryGetValue(appKey, out var entry) ? entry : null;
    }

    public IReadOnlyList<AppRegistryEntry> GetAll()
    {
        return _entries.Values
            .OrderByDescending(x => x.LastSeenAt)
            .ToArray();
    }
}
