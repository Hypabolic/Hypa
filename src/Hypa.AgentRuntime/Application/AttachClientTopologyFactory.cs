using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Build topology defaults from the session graph. Snapshot ordinal then
/// id order is authoritative.
/// </summary>
public static class AttachClientTopologyFactory
{
    public static AttachClientTopology FromState(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var workspaces = state.ListWorkspaces();
        if (workspaces.Count == 0)
            return AttachClientTopology.Empty;

        var workspaceOrder = new List<string>(workspaces.Count);
        var activeTabs = new Dictionary<string, string>(StringComparer.Ordinal);
        var tabWorkspaceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var tabOrderByWorkspace = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var focusedPanes = new Dictionary<string, string>(StringComparer.Ordinal);
        var fallbackPanes = new Dictionary<string, string>(StringComparer.Ordinal);
        var paneTabIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var paneOrderByTab = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var workspace in workspaces)
        {
            workspaceOrder.Add(workspace.Id.Value);
            var tabs = state.ListTabs(workspace.Id);
            var tabIds = new List<string>(tabs.Count);
            foreach (var tab in tabs)
            {
                tabIds.Add(tab.Id.Value);
                tabWorkspaceIds[tab.Id.Value] = workspace.Id.Value;
                var panes = PaneOrder(tab);
                paneOrderByTab[tab.Id.Value] = panes;
                foreach (var paneId in panes)
                    paneTabIds[paneId] = tab.Id.Value;

                if (tab.FocusedPaneId is { } focusedPane
                    && panes.Contains(focusedPane.Value, StringComparer.Ordinal))
                {
                    focusedPanes[tab.Id.Value] = focusedPane.Value;
                }
                else if (panes.Count > 0)
                {
                    focusedPanes[tab.Id.Value] = panes[0];
                }

                if (panes.Count > 0)
                    fallbackPanes[tab.Id.Value] = panes[0];
            }

            tabOrderByWorkspace[workspace.Id.Value] = tabIds;
            if (workspace.FocusedTabId is { } workspaceTab
                && tabIds.Contains(workspaceTab.Value, StringComparer.Ordinal))
            {
                activeTabs[workspace.Id.Value] = workspaceTab.Value;
            }
            else if (tabIds.Count > 0)
            {
                activeTabs[workspace.Id.Value] = tabIds[0];
            }
        }

        var snapshot = state.Snapshot();
        string? focusedWorkspace = null;
        if (snapshot.FocusedWorkspaceId is { } focused
            && activeTabs.ContainsKey(focused.Value))
        {
            focusedWorkspace = focused.Value;
        }

        return new AttachClientTopology
        {
            FocusedWorkspaceId = focusedWorkspace,
            FallbackWorkspaceId = workspaceOrder[0],
            ActiveTabIds = activeTabs,
            TabWorkspaceIds = tabWorkspaceIds,
            FocusedPaneIds = focusedPanes,
            FallbackPaneIds = fallbackPanes,
            PaneTabIds = paneTabIds,
            WorkspaceOrder = workspaceOrder,
            TabOrderByWorkspace = tabOrderByWorkspace,
            PaneOrderByTab = paneOrderByTab,
        };
    }

    private static IReadOnlyList<string> PaneOrder(TabState tab)
    {
        var leaves = LayoutTreeOperations.Leaves(tab.LayoutRoot)
            .Select(leaf => leaf.PaneId?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();
        if (leaves.Count > 0)
            return leaves;

        return tab.PaneIds
            .Select(id => id.Value)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }
}
