using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>Workspace directory follows the stable identity pane in its first tab.</summary>
public static class WorkspaceDirectoryIdentity
{
    public static PaneState? Source(SessionState session, WorkspaceState workspace)
    {
        var tab = workspace.TabIds
            .Select(id => session.Tabs.GetValueOrDefault(id.Value))
            .Where(tab => tab is not null)
            .OrderBy(tab => tab!.Ordinal)
            .ThenBy(tab => tab!.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (tab is null)
            return null;
        var id = tab.IdentityPaneId ?? LayoutTreeOperations.Leaves(tab.LayoutRoot)
            .Select(leaf => leaf.PaneId).FirstOrDefault(id => id is not null);
        return id is { } root && session.Panes.TryGetValue(root.Value, out var pane)
            && pane.Placement == PanePlacement.Tiled && pane.TabId == tab.Id ? pane : null;
    }

    public static string FallbackLabel(string cwd, string? home = null)
    {
        if (string.Equals(cwd.TrimEnd(Path.DirectorySeparatorChar),
            home?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            return "~";
        var leaf = Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(leaf) ? cwd : leaf;
    }
}
