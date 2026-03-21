using Monitor.Network.Models;

namespace Monitor.Network.Abstractions;

public interface IAppRegistry
{
    AppRegistryEntry GetOrAdd(int pid);
    AppRegistryEntry? GetByAppKey(string appKey);
    IReadOnlyList<AppRegistryEntry> GetAll();
}
