using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Hooks;

public sealed class HarnessRegistry(IEnumerable<IAgentHarnessAdapter> adapters) : IHarnessRegistry
{
    private readonly IReadOnlyList<IAgentHarnessAdapter> _all = adapters.ToList();
    private readonly Dictionary<string, IAgentHarnessAdapter> _byKey =
        adapters.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IAgentHarnessAdapter> All => _all;

    public IAgentHarnessAdapter? Find(string key) =>
        _byKey.TryGetValue(key, out var adapter) ? adapter : null;
}
