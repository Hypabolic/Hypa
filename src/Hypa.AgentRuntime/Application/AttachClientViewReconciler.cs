namespace Hypa.AgentRuntime.Application;

/// <summary>
// Pane fallbacks follow
/// snapshot layout traversal when the stored pane is absent.
/// </summary>
public static class AttachClientViewReconciler
{
    public static AttachClientView Reconcile(AttachClientView view, AttachClientTopology topology)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(topology);

        if (topology.WorkspaceOrder.Count == 0)
        {
            return view with
            {
                FocusedWorkspaceId = null,
                ActiveTabIds = new Dictionary<string, string>(StringComparer.Ordinal),
                FocusedPaneIds = new Dictionary<string, string>(StringComparer.Ordinal),
            };
        }

        var activeTabs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (workspaceId, tabId) in view.ActiveTabIds)
        {
            if (topology.ActiveTabIds.ContainsKey(workspaceId)
                && topology.TabWorkspaceIds.TryGetValue(tabId, out var owner)
                && string.Equals(owner, workspaceId, StringComparison.Ordinal))
            {
                activeTabs[workspaceId] = tabId;
            }
        }

        foreach (var workspaceId in topology.WorkspaceOrder)
        {
            if (activeTabs.ContainsKey(workspaceId))
                continue;
            if (topology.ActiveTabIds.TryGetValue(workspaceId, out var topologyTab))
                activeTabs[workspaceId] = topologyTab;
            else if (topology.TabOrderByWorkspace.TryGetValue(workspaceId, out var tabs)
                && tabs.Count > 0)
            {
                activeTabs[workspaceId] = tabs[0];
            }
        }

        var focusedWorkspace = view.FocusedWorkspaceId;
        if (focusedWorkspace is null || !activeTabs.ContainsKey(focusedWorkspace))
        {
            focusedWorkspace = topology.FocusedWorkspaceId is { } topologyFocused
                && activeTabs.ContainsKey(topologyFocused)
                    ? topologyFocused
                    : topology.FallbackWorkspaceId;
            if (focusedWorkspace is not null && !activeTabs.ContainsKey(focusedWorkspace))
                focusedWorkspace = null;
        }

        var selectedTabs = new HashSet<string>(activeTabs.Values, StringComparer.Ordinal);
        var focusedPanes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (tabId, paneId) in view.FocusedPaneIds)
        {
            if (!selectedTabs.Contains(tabId))
                continue;
            if (topology.PaneTabIds.TryGetValue(paneId, out var owner)
                && string.Equals(owner, tabId, StringComparison.Ordinal))
            {
                focusedPanes[tabId] = paneId;
            }
        }

        foreach (var tabId in selectedTabs)
        {
            if (focusedPanes.ContainsKey(tabId))
                continue;
            if (topology.FocusedPaneIds.TryGetValue(tabId, out var topologyPane))
                focusedPanes[tabId] = topologyPane;
            else if (topology.FallbackPaneIds.TryGetValue(tabId, out var fallbackPane))
                focusedPanes[tabId] = fallbackPane;
            else if (topology.PaneOrderByTab.TryGetValue(tabId, out var panes)
                && panes.Count > 0)
            {
                focusedPanes[tabId] = panes[0];
            }
        }

        return view with
        {
            FocusedWorkspaceId = focusedWorkspace,
            ActiveTabIds = activeTabs,
            FocusedPaneIds = focusedPanes,
        };
    }

    public static AttachClientView ApplyFocus(
        AttachClientView view,
        AttachClientTopology topology,
        string? workspaceId,
        string? tabId,
        string? paneId)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(topology);

        var nextWorkspace = EmptyToNull(workspaceId);
        var nextTab = EmptyToNull(tabId);
        var nextPane = EmptyToNull(paneId);

        if (nextPane is not null
            && topology.PaneTabIds.TryGetValue(nextPane, out var paneTab))
        {
            nextTab ??= paneTab;
        }

        if (nextTab is not null
            && topology.TabWorkspaceIds.TryGetValue(nextTab, out var tabWorkspace))
        {
            nextWorkspace ??= tabWorkspace;
        }

        var activeTabs = new Dictionary<string, string>(view.ActiveTabIds, StringComparer.Ordinal);
        var focusedPanes = new Dictionary<string, string>(view.FocusedPaneIds, StringComparer.Ordinal);
        var focusedWorkspace = view.FocusedWorkspaceId;

        if (nextWorkspace is not null)
            focusedWorkspace = nextWorkspace;

        if (nextWorkspace is not null && nextTab is not null)
            activeTabs[nextWorkspace] = nextTab;

        if (nextTab is not null && nextPane is not null)
            focusedPanes[nextTab] = nextPane;

        var next = view with
        {
            FocusedWorkspaceId = focusedWorkspace,
            ActiveTabIds = activeTabs,
            FocusedPaneIds = focusedPanes,
        };
        return Reconcile(next, topology);
    }

    public static AttachClientView Seed(AttachClientView view, AttachClientTopology topology) =>
        Reconcile(view, topology);

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
