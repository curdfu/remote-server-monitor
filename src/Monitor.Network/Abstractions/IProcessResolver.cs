using Monitor.Network.Models;

namespace Monitor.Network.Abstractions;

public interface IProcessResolver
{
    ResolvedProcessInfo Resolve(int pid);
}
