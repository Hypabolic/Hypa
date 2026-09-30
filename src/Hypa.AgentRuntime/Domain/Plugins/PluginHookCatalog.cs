namespace Hypa.AgentRuntime.Domain.Plugins;

/// <summary>
/// Manifest hook names accepted at link time. Unknown names fail closed.
/// Never accept <c>terminal.output</c> or <c>terminal.render</c>.
/// </summary>
public static class PluginHookCatalog
{
    public const string Startup = "startup";
    public const string PaneCreated = "pane.created";
    public const string PaneClosed = "pane.closed";
    public const string PaneFocused = "pane.focused";
    public const string PaneExited = "pane.exited";
    public const string WorkspaceCreated = "workspace.created";
    public const string WorkspaceClosed = "workspace.closed";
    public const string WorkspaceFocused = "workspace.focused";
    public const string TabCreated = "tab.created";
    public const string TabClosed = "tab.closed";
    public const string TabFocused = "tab.focused";
    public const string AgentStatusChanged = "agent.status_changed";
    public const string OccupantChanged = "occupant.changed";
    public const string WorktreeCreated = "worktree.created";
    public const string WorktreeOpened = "worktree.opened";
    public const string WorktreeRemoved = "worktree.removed";
    public const string ConfigReloaded = "config.reloaded";
    public const string PopupOpened = "popup.opened";
    public const string PopupClosed = "popup.closed";
    public const string PanePlacementChanged = "pane.placement_changed";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        Startup,
        PaneCreated,
        PaneClosed,
        PaneFocused,
        PaneExited,
        WorkspaceCreated,
        WorkspaceClosed,
        WorkspaceFocused,
        TabCreated,
        TabClosed,
        TabFocused,
        AgentStatusChanged,
        OccupantChanged,
        WorktreeCreated,
        WorktreeOpened,
        WorktreeRemoved,
        ConfigReloaded,
        PopupOpened,
        PopupClosed,
        PanePlacementChanged,
    };

    public static bool IsForbiddenOutputHook(string name) =>
        string.Equals(name, "terminal.output", StringComparison.Ordinal)
        || string.Equals(name, "terminal.render", StringComparison.Ordinal);

    public static bool IsWorktreeHook(string name) =>
        name is WorktreeCreated or WorktreeOpened or WorktreeRemoved;

    public static bool IsAllowed(string name) =>
        !IsForbiddenOutputHook(name) && Allowed.Contains(name);
}
