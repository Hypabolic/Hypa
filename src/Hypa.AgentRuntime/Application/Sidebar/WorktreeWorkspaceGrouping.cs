namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
// / Orders workspace rows by worktree key.
/// <c>src/client/shell/sidebar.rs:448-535</c>.
/// </summary>
public static class WorktreeWorkspaceGrouping
{
    public const int ChildIndentCols = 3;

    public const string WorktreeBranchPrefix = "worktree/";

    public static IReadOnlyList<WorktreeWorkspaceEntry> Order(
        IReadOnlyList<SidebarWorkspaceItem> workspaces,
        IReadOnlySet<string>? collapsedGroups = null,
        string? focusedWorkspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        collapsedGroups ??= new HashSet<string>(StringComparer.Ordinal);
        var members = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < workspaces.Count; i++)
        {
            var key = workspaces[i].WorktreeKey;
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (!members.TryGetValue(key, out var list))
            {
                list = [];
                members[key] = list;
            }

            list.Add(i);
        }

        var grouped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, indices) in members)
        {
            if (indices.Count < 2)
                continue;
            if (indices.Exists(index => !workspaces[index].IsLinkedWorktree))
                grouped.Add(key);
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<WorktreeWorkspaceEntry>(workspaces.Count);
        for (var index = 0; index < workspaces.Count; index++)
        {
            var workspace = workspaces[index];
            var key = workspace.WorktreeKey;
            if (string.IsNullOrWhiteSpace(key) || !grouped.Contains(key))
            {
                entries.Add(new WorktreeWorkspaceEntry(index, Indented: false, LastChild: false, GroupKey: null));
                continue;
            }

            if (!emitted.Add(key))
                continue;

            if (!members.TryGetValue(key, out var groupMembers))
                continue;

            var parent = groupMembers.FindIndex(member => !workspaces[member].IsLinkedWorktree);
            if (parent < 0)
                parent = index;
            else
                parent = groupMembers[parent];

            var parentGroupKey = ParentGroupKey(workspaces, parent);
            entries.Add(new WorktreeWorkspaceEntry(parent, Indented: false, LastChild: false, parentGroupKey));
            if (collapsedGroups.Contains(key))
            {
                var focusedChild = groupMembers.FindIndex(member =>
                    member != parent
                    && string.Equals(workspaces[member].Id, focusedWorkspaceId, StringComparison.Ordinal));
                if (focusedChild >= 0)
                {
                    entries.Add(new WorktreeWorkspaceEntry(
                        groupMembers[focusedChild],
                        Indented: true,
                        LastChild: true,
                        parentGroupKey));
                }

                continue;
            }

            var children = groupMembers.Where(member => member != parent).ToArray();
            for (var childIndex = 0; childIndex < children.Length; childIndex++)
            {
                entries.Add(new WorktreeWorkspaceEntry(
                    children[childIndex],
                    Indented: true,
                    LastChild: childIndex + 1 == children.Length,
                    parentGroupKey));
            }
        }

        return entries;
    }

    public static string? ParentGroupKey(IReadOnlyList<SidebarWorkspaceItem> workspaces, int index)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        if (index < 0 || index >= workspaces.Count)
            return null;
        var workspace = workspaces[index];
        if (workspace.IsLinkedWorktree || string.IsNullOrWhiteSpace(workspace.WorktreeKey))
            return null;
        var count = 0;
        foreach (var candidate in workspaces)
        {
            if (string.Equals(candidate.WorktreeKey, workspace.WorktreeKey, StringComparison.Ordinal))
                count++;
        }

        return count >= 2 ? workspace.WorktreeKey : null;
    }

    public static string DisplayName(SidebarWorkspaceItem workspace, bool indented, string? branch)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (indented && !workspace.CustomLabel)
        {
            var source = !string.IsNullOrWhiteSpace(branch) ? branch : workspace.Branch;
            if (!string.IsNullOrWhiteSpace(source))
            {
                return source.StartsWith(WorktreeBranchPrefix, StringComparison.Ordinal)
                    ? source[WorktreeBranchPrefix.Length..]
                    : source;
            }
        }

        return SidebarSectionComposer.DisplayName(workspace.Label, workspace.Id);
    }

    public static string AggregateHiddenAttention(
        IReadOnlyList<SidebarWorkspaceItem> workspaces,
        IReadOnlyDictionary<string, SidebarPaneItem[]> panesByWorkspace,
        int parentIndex,
        IReadOnlySet<string>? collapsedGroups)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(panesByWorkspace);
        if (parentIndex < 0 || parentIndex >= workspaces.Count)
            return SidebarTokenGrammar.Unknown;
        var parent = workspaces[parentIndex];
        var parentOccupants = panesByWorkspace.GetValueOrDefault(parent.Id) ?? [];
        var parentState = SidebarSectionComposer.AggregateState(parentOccupants);
        if (parent.IsLinkedWorktree
            || string.IsNullOrWhiteSpace(parent.WorktreeKey)
            || collapsedGroups is null
            || !collapsedGroups.Contains(parent.WorktreeKey))
        {
            return parentState;
        }

        var best = parentState;
        var rank = SidebarTokenGrammar.PriorityRank(best);
        foreach (var candidate in workspaces)
        {
            if (!string.Equals(candidate.WorktreeKey, parent.WorktreeKey, StringComparison.Ordinal))
                continue;
            var occupants = panesByWorkspace.GetValueOrDefault(candidate.Id) ?? [];
            var state = SidebarSectionComposer.AggregateState(occupants);
            var next = SidebarTokenGrammar.PriorityRank(state);
            if (next >= rank)
                continue;
            rank = next;
            best = state;
        }

        return best;
    }

    public static SidebarPrefixRole PrefixRole(bool indented, bool lastChild, int cardRowIndex)
    {
        if (!indented)
            return SidebarPrefixRole.None;
        if (cardRowIndex == 0)
            return lastChild ? SidebarPrefixRole.TreeLast : SidebarPrefixRole.TreeBranch;
        return lastChild ? SidebarPrefixRole.None : SidebarPrefixRole.TreeBar;
    }

    /// <summary>
    /// parent toggle on the first card row only.
    /// </summary>
    public static bool IsParentToggleRow(SidebarPaintedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.CardRowIndex == 0
            && row.PrefixRole is SidebarPrefixRole.None
            && row.IndentCols < ChildIndentCols
            && !string.IsNullOrWhiteSpace(row.GroupKey);
    }

    /// <summary>
    /// <c>Rect::new(rect.right().saturating_sub(1), rect.y, 1, 1)</c>
    /// on the visible parent row after scroll.
    /// </summary>
    public static CellRect? ToggleRect(CellRect body, int visibleIndex, SidebarPaintedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!IsParentToggleRow(row) || body.Cols < 1 || visibleIndex < 0)
            return null;
        return new CellRect(body.EndCol - 1, body.Row + visibleIndex, 1, 1);
    }
}

public sealed record WorktreeWorkspaceEntry(
    int Index,
    bool Indented,
    bool LastChild,
    string? GroupKey);
