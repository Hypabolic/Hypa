namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>Last-known git tokens. Resolve never starts a process.</summary>
public interface ISidebarGitStatus
{
    SidebarGitInfo Resolve(string cwd);

    void RequestRefresh(string cwd)
    {
        _ = cwd;
    }

    bool TryConsumeRefresh() => false;
}

/// <summary>Test/opt-out stub: empty branch and status.</summary>
public sealed class EmptySidebarGitStatus : ISidebarGitStatus
{
    public static EmptySidebarGitStatus Instance { get; } = new();

    public SidebarGitInfo Resolve(string cwd)
    {
        _ = cwd;
        return new SidebarGitInfo();
    }
}

/// <summary>Fixed map for tests. Unknown cwd is empty.</summary>
public sealed class MapSidebarGitStatus : ISidebarGitStatus
{
    private readonly IReadOnlyDictionary<string, SidebarGitInfo> _map;

    public MapSidebarGitStatus(IReadOnlyDictionary<string, SidebarGitInfo> map)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
    }

    public SidebarGitInfo Resolve(string cwd) =>
        _map.TryGetValue(cwd ?? "", out var info) ? info : new SidebarGitInfo();
}
