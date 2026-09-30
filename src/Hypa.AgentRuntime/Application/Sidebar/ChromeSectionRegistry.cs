namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// Built-in pane order is always spaces then agents. Section
/// <c>order</c> is kept on config but is inert for those two panes.
/// </summary>
public sealed class ChromeSectionRegistry
{
    private readonly List<IChromeSectionStrategy> _builtIns;
    private readonly List<IChromeSectionStrategy> _extras = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    private ChromeSectionRegistry(IReadOnlyList<IChromeSectionStrategy> builtIns)
    {
        _builtIns = [.. builtIns];
        foreach (var strategy in _builtIns)
            _ids.Add(strategy.Id);
    }

    public static ChromeSectionRegistry Core() =>
        new([
            new SpacesChromeSectionStrategy(),
            new AgentsChromeSectionStrategy(),
            new CubesChromeSectionStrategy(),
        ]);

    public static ChromeSectionRegistry MuxRelease() =>
        new([
            new SpacesChromeSectionStrategy(),
            new AgentsChromeSectionStrategy(),
            new CubesChromeSectionStrategy(),
        ]);

    public bool TryRegister(IChromeSectionStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        if (string.IsNullOrWhiteSpace(strategy.Id))
            return false;
        if (string.Equals(strategy.Id, SidebarTokenGrammar.AgentsId, StringComparison.Ordinal)
            || string.Equals(strategy.Id, SidebarTokenGrammar.SpacesId, StringComparison.Ordinal)
            || string.Equals(strategy.Id, SidebarTokenGrammar.CubesId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!_ids.Add(strategy.Id))
            return false;
        _extras.Add(strategy);
        return true;
    }

    /// <summary>Spaces, then agents, then extras. Resource extras stay unbound.</summary>
    public IReadOnlyList<IChromeSectionStrategy> PaneOrder()
    {
        if (_extras.Count == 0)
            return _builtIns;

        var list = new List<IChromeSectionStrategy>(_builtIns.Count + _extras.Count);
        list.AddRange(_builtIns);
        list.AddRange(_extras);
        return list;
    }
}
