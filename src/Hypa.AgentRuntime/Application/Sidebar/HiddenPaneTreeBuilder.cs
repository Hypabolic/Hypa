namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// Builds the parent and child sidebar tree. Parent ids come from
/// validated snapshot provenance only.
/// </summary>
public static class HiddenPaneTreeBuilder
{
    public static HiddenPaneTree Build(
        IReadOnlyList<HiddenPaneRecord> panes,
        IReadOnlyList<HiddenPaneRecord>? visibleAgents = null,
        IReadOnlySet<string>? collapsedIds = null)
    {
        ArgumentNullException.ThrowIfNull(panes);
        var byId = new Dictionary<string, HiddenPaneRecord>(StringComparer.Ordinal);
        foreach (var pane in panes)
        {
            if (string.IsNullOrWhiteSpace(pane.PaneId))
                continue;
            byId[pane.PaneId] = pane;
        }

        var catalog = new List<HiddenPaneRecord>();
        foreach (var pane in byId.Values)
        {
            if (pane.InCatalog)
                catalog.Add(pane);
        }

        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pane in catalog)
        {
            var parent = ResolveParent(pane, byId);
            if (parent is null)
                continue;
            if (!children.TryGetValue(parent, out var list))
            {
                list = [];
                children[parent] = list;
            }

            list.Add(pane.PaneId);
            parentOf[pane.PaneId] = parent;
        }

        foreach (var list in children.Values)
            list.Sort(StringComparer.Ordinal);

        var visible = visibleAgents ?? [];
        var catalogIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pane in catalog)
            catalogIds.Add(pane.PaneId);

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<HiddenPaneTreeRow>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (var agent in visible)
        {
            if (string.IsNullOrWhiteSpace(agent.PaneId) || emitted.Contains(agent.PaneId))
                continue;
            if (parentOf.ContainsKey(agent.PaneId))
                continue;
            if (catalogIds.Contains(agent.PaneId))
                continue;
            emitted.Add(agent.PaneId);
            EmitNode(agent, agent.PaneId, HiddenPaneRowKind.Parent, 0, rows, byId, children, collapsedIds, emitted, visiting, lastChild: false);
        }

        // workspace first, then emits children. Do the same for pane
        // parent topology: emit true roots only. Lexical pane ids and
        // snapshot order must not promote a child ahead of its ancestor.
        var remainingParents = new List<HiddenPaneRecord>();
        foreach (var pane in byId.Values)
        {
            if (emitted.Contains(pane.PaneId))
                continue;
            if (!IsTrueRoot(pane.PaneId, children, parentOf))
                continue;
            remainingParents.Add(pane);
        }

        remainingParents.Sort((a, b) =>
        {
            var ws = string.Compare(a.WorkspaceId, b.WorkspaceId, StringComparison.Ordinal);
            return ws != 0 ? ws : string.Compare(a.PaneId, b.PaneId, StringComparison.Ordinal);
        });

        foreach (var parent in remainingParents)
        {
            if (!emitted.Add(parent.PaneId))
                continue;
            EmitNode(parent, parent.PaneId, HiddenPaneRowKind.Parent, 0, rows, byId, children, collapsedIds, emitted, visiting, lastChild: false);
        }

        var leftover = new Dictionary<string, List<HiddenPaneRecord>>(StringComparer.Ordinal);
        foreach (var pane in catalog)
        {
            if (emitted.Contains(pane.PaneId))
                continue;
            var ws = pane.WorkspaceId.Length > 0 ? pane.WorkspaceId : "";
            if (!leftover.TryGetValue(ws, out var list))
            {
                list = [];
                leftover[ws] = list;
            }

            list.Add(pane);
        }

        foreach (var workspaceId in leftover.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var group = leftover[workspaceId];
            group.Sort((a, b) => string.Compare(a.PaneId, b.PaneId, StringComparison.Ordinal));
            var groupId = HiddenPaneIds.BackgroundGroup(workspaceId);
            var collapsed = collapsedIds is not null && collapsedIds.Contains(groupId);
            var state = collapsed && HasBlocked(group, byId, children, visiting: null)
                ? SidebarTokenGrammar.Blocked
                : SidebarTokenGrammar.Unknown;
            rows.Add(new HiddenPaneTreeRow
            {
                Id = groupId,
                WorkspaceId = workspaceId,
                Kind = HiddenPaneRowKind.Background,
                Depth = 0,
                Expandable = true,
                Collapsed = collapsed,
                State = state,
                Label = HiddenPaneIds.BackgroundLabel,
            });
            if (collapsed)
            {
                foreach (var pane in group)
                    emitted.Add(pane.PaneId);
                continue;
            }

            for (var i = 0; i < group.Count; i++)
            {
                var pane = group[i];
                if (!emitted.Add(pane.PaneId))
                    continue;
                EmitNode(
                    pane,
                    pane.PaneId,
                    pane.Hidden ? HiddenPaneRowKind.Child : HiddenPaneRowKind.Revealed,
                    1,
                    rows,
                    byId,
                    children,
                    collapsedIds,
                    emitted,
                    visiting,
                    lastChild: i == group.Count - 1);
            }
        }

        return new HiddenPaneTree
        {
            Rows = rows,
            Catalog = catalog,
        };
    }

    private static bool IsTrueRoot(
        string paneId,
        Dictionary<string, List<string>> children,
        Dictionary<string, string> parentOf) =>
        children.ContainsKey(paneId) && !parentOf.ContainsKey(paneId);

    internal static string? ResolveParent(
        HiddenPaneRecord pane,
        IReadOnlyDictionary<string, HiddenPaneRecord> byId)
    {
        var parent = pane.ParentPaneId;
        if (string.IsNullOrWhiteSpace(parent) || !byId.ContainsKey(parent))
            return null;
        if (WouldCycle(pane.PaneId, parent, byId))
            return null;
        return parent;
    }

    internal static bool WouldCycle(
        string paneId,
        string parentId,
        IReadOnlyDictionary<string, HiddenPaneRecord> byId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { paneId };
        var current = parentId;
        var guard = byId.Count + 2;
        while (!string.IsNullOrWhiteSpace(current) && guard-- > 0)
        {
            if (!seen.Add(current))
                return true;
            if (!byId.TryGetValue(current, out var node))
                return false;
            current = node.ParentPaneId ?? "";
        }

        return false;
    }

    private static void EmitNode(
        HiddenPaneRecord pane,
        string id,
        HiddenPaneRowKind kind,
        int depth,
        List<HiddenPaneTreeRow> rows,
        Dictionary<string, HiddenPaneRecord> byId,
        Dictionary<string, List<string>> children,
        IReadOnlySet<string>? collapsedIds,
        HashSet<string> emitted,
        HashSet<string> visiting,
        bool lastChild)
    {
        if (!visiting.Add(id))
            return;

        try
        {
            var childIds = children.GetValueOrDefault(id);
            var expandable = childIds is { Count: > 0 };
            var collapsed = expandable && collapsedIds is not null && collapsedIds.Contains(id);
            var state = pane.State;
            if (collapsed && childIds is not null && HasBlockedDescendant(childIds, byId, children, new HashSet<string>(StringComparer.Ordinal)))
                state = SidebarTokenGrammar.Blocked;

            rows.Add(new HiddenPaneTreeRow
            {
                Id = id,
                PaneId = pane.PaneId,
                WorkspaceId = pane.WorkspaceId,
                TabId = pane.TabId,
                Kind = kind,
                Depth = depth,
                Expandable = expandable,
                Collapsed = collapsed,
                Hidden = pane.Hidden,
                State = state,
                Label = DisplayLabel(pane),
                AgentKind = pane.AgentKind,
                ParentPaneId = pane.ParentPaneId,
                LastChild = lastChild,
            });

            if (!expandable || collapsed || childIds is null)
            {
                if (collapsed && childIds is not null)
                    MarkEmitted(childIds, emitted, children);
                return;
            }

            for (var i = 0; i < childIds.Count; i++)
            {
                var childId = childIds[i];
                if (!byId.TryGetValue(childId, out var child))
                    continue;
                if (!emitted.Add(childId))
                    continue;
                var childKind = child.Hidden ? HiddenPaneRowKind.Child : HiddenPaneRowKind.Revealed;
                EmitNode(
                    child,
                    childId,
                    childKind,
                    depth + 1,
                    rows,
                    byId,
                    children,
                    collapsedIds,
                    emitted,
                    visiting,
                    lastChild: i == childIds.Count - 1);
            }
        }
        finally
        {
            visiting.Remove(id);
        }
    }

    private static void MarkEmitted(
        IReadOnlyList<string> childIds,
        HashSet<string> emitted,
        Dictionary<string, List<string>> children)
    {
        foreach (var childId in childIds)
        {
            if (!emitted.Add(childId))
                continue;
            if (children.TryGetValue(childId, out var nested))
                MarkEmitted(nested, emitted, children);
        }
    }

    private static bool HasBlocked(
        IReadOnlyList<HiddenPaneRecord> group,
        Dictionary<string, HiddenPaneRecord> byId,
        Dictionary<string, List<string>> children,
        HashSet<string>? visiting)
    {
        visiting ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var pane in group)
        {
            if (IsBlocked(pane.State))
                return true;
            if (children.TryGetValue(pane.PaneId, out var childIds)
                && HasBlockedDescendant(childIds, byId, children, visiting))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasBlockedDescendant(
        IReadOnlyList<string> childIds,
        Dictionary<string, HiddenPaneRecord> byId,
        Dictionary<string, List<string>> children,
        HashSet<string> visiting)
    {
        foreach (var childId in childIds)
        {
            if (!visiting.Add(childId))
                continue;
            if (!byId.TryGetValue(childId, out var child))
                continue;
            if (IsBlocked(child.State))
                return true;
            if (children.TryGetValue(childId, out var nested)
                && HasBlockedDescendant(nested, byId, children, visiting))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBlocked(string? state) =>
        string.Equals(
            SidebarTokenGrammar.CanonicalState(state),
            SidebarTokenGrammar.Blocked,
            StringComparison.Ordinal);

    private static string DisplayLabel(HiddenPaneRecord pane) =>
        string.IsNullOrWhiteSpace(pane.Label) ? pane.PaneId : pane.Label;
}
