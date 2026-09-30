using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Pane screen history for one mux.
/// <c>src/persist/snapshot.rs:32-37</c> <c>SessionHistorySnapshot</c>.
/// Separate from the structural session graph.
/// </summary>
public sealed record SessionHistorySnapshot
{
    /// <summary>
    /// Hypa history is a new file. This version is 1.
    /// </summary>
    public const int FormatVersion = 1;

    public int Version { get; init; } = FormatVersion;

    public List<WorkspaceHistorySnapshot> Workspaces { get; init; } = [];
}

public sealed record WorkspaceHistorySnapshot
{
    public List<TabHistorySnapshot> Tabs { get; init; } = [];
}

// Keys are pane ids.</summary>
public sealed record TabHistorySnapshot
{
    public Dictionary<string, PaneHistorySnapshot> Panes { get; init; } =
        new Dictionary<string, PaneHistorySnapshot>(StringComparer.Ordinal);
}

public sealed record PaneHistorySnapshot
{
    public required string Ansi { get; init; }

    public int Lines { get; init; }
}

/// <summary>
/// Capture pane screen history separately from the structural graph.
/// </summary>
public static class PaneHistorySnapshotter
{
    public static SessionHistorySnapshot Capture(
        AppState state,
        Func<PaneId, IPaneRuntime?> runtimeOf)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtimeOf);

        var workspaces = new List<WorkspaceHistorySnapshot>();
        foreach (var workspace in state.ListWorkspaces())
        {
            var tabs = new List<TabHistorySnapshot>();
            foreach (var tab in state.ListTabs(workspace.Id))
            {
                var panes = new Dictionary<string, PaneHistorySnapshot>(StringComparer.Ordinal);
                foreach (var paneId in tab.PaneIds.Concat(tab.HiddenPaneIds))
                {
                    if (!panes.ContainsKey(paneId.Value))
                    {
                        var history = CapturePane(runtimeOf(paneId));
                        if (history is not null)
                            panes[paneId.Value] = history;
                    }
                }

                tabs.Add(new TabHistorySnapshot { Panes = panes });
            }

            workspaces.Add(new WorkspaceHistorySnapshot { Tabs = tabs });
        }

        return new SessionHistorySnapshot
        {
            Version = SessionHistorySnapshot.FormatVersion,
            Workspaces = workspaces,
        };
    }

    /// <summary>
    /// index, tab index, then saved pane id.
    /// </summary>
    public static PaneHistorySnapshot? Lookup(
        SessionHistorySnapshot? history,
        int workspaceIndex,
        int tabIndex,
        string paneId)
    {
        if (history is null || string.IsNullOrEmpty(paneId))
            return null;
        if (workspaceIndex < 0 || workspaceIndex >= history.Workspaces.Count)
            return null;
        var tabs = history.Workspaces[workspaceIndex].Tabs;
        if (tabIndex < 0 || tabIndex >= tabs.Count)
            return null;
        return tabs[tabIndex].Panes.TryGetValue(paneId, out var pane) ? pane : null;
    }

    private static PaneHistorySnapshot? CapturePane(IPaneRuntime? runtime)
    {
        var ansi = runtime?.SnapshotHistory();
        if (string.IsNullOrWhiteSpace(ansi))
            return null;

        var lines = CountLines(ansi);
        return new PaneHistorySnapshot { Ansi = ansi, Lines = lines };
    }

    private static int CountLines(string ansi)
    {
        if (ansi.Length == 0)
            return 0;
        var lines = 1;
        foreach (var ch in ansi)
        {
            if (ch == '\n')
                lines++;
        }

        return lines;
    }
}
