using AttachClientViewCopy = Hypa.AgentRuntime.Application.AttachClientViewCopy;
using SafeDisplayText = Hypa.AgentRuntime.Application.SafeDisplayText;

namespace Hypa.AgentRuntime.Application.Sidebar;

public sealed class SpacesChromeSectionStrategy : IChromeSectionStrategy
{
    /// <summary>
    /// Hypa labels this pane workspaces.
    /// </summary>
    public const string HeaderText = " workspaces";

    public const string NewActionLabel = " new";

    public string Id => SidebarTokenGrammar.SpacesId;

    public bool IsBuiltIn => true;

    public SidebarPaneSlot Slot => SidebarPaneSlot.Spaces;

    public SidebarPaneView Compose(
        SidebarComposeInput input,
        ResolvedSidebarSection resolved,
        bool collapsed,
        bool visible,
        int width,
        SidebarCollapseDisplay display)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(resolved);
        var paneByWorkspace = input.Panes
            .GroupBy(p => p.WorkspaceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var rows = new List<SidebarPaintedRow>();
        string? selectedId = null;
        if (!collapsed)
        {
            var index = 1;
            var compact = display is SidebarCollapseDisplay.Compact;
            var probeGit = display is SidebarCollapseDisplay.Expanded;
            var ordered = WorktreeWorkspaceGrouping.Order(
                input.Workspaces,
                input.CollapsedWorktreeGroups,
                input.FocusedWorkspaceId);
            foreach (var entry in ordered)
            {
                var workspace = input.Workspaces[entry.Index];
                var state = WorktreeWorkspaceGrouping.AggregateHiddenAttention(
                    input.Workspaces,
                    paneByWorkspace,
                    entry.Index,
                    input.CollapsedWorktreeGroups);
                var git = probeGit && !entry.Indented
                    ? workspace.Git ?? SidebarSectionComposer.ResolveGit(input.Git, workspace.Cwd)
                    : new SidebarGitInfo();
                var selected = string.Equals(workspace.Id, input.FocusedWorkspaceId, StringComparison.Ordinal);
                if (selected)
                    selectedId = workspace.Id;
                var branch = git.Branch.Length > 0 ? git.Branch : workspace.Branch;
                var values = new SidebarTokenValues
                {
                    StateIcon = SidebarTokenGrammar.StateIcon(state, input.Ui.StatusIndicators),
                    StateText = SidebarTokenGrammar.StateText(state),
                    Workspace = WorktreeWorkspaceGrouping.DisplayName(workspace, entry.Indented, branch),
                    Branch = entry.Indented ? "" : git.Branch,
                    GitStatus = entry.Indented ? "" : git.Status,
                    Custom = workspace.Tokens,
                };
                var compactLabel = SidebarSectionComposer.CompactLabel(index, values.StateIcon);
                var prefix = WorktreeWorkspaceGrouping.PrefixRole(entry.Indented, entry.LastChild, 0);
                var groupCollapsed = entry.GroupKey is { Length: > 0 }
                    && !entry.Indented
                    && input.CollapsedWorktreeGroups is { } groups
                    && groups.Contains(entry.GroupKey);
                SidebarSectionComposer.AppendTokenRows(
                    rows,
                    resolved.Config.Rows,
                    values,
                    width,
                    compact,
                    resolved.Config.RowGap,
                    new SidebarPaintedRow
                    {
                        Id = workspace.Id,
                        Kind = SidebarRowKind.Workspace,
                        Label = compactLabel,
                        CompactLabel = compactLabel,
                        State = state,
                        WorkspaceId = workspace.Id,
                        Selected = selected,
                        PrefixRole = prefix,
                        IndentCols = entry.Indented ? WorktreeWorkspaceGrouping.ChildIndentCols : 0,
                        PaneSlot = SidebarPaneSlot.Spaces,
                        ScrollId = Id,
                        GroupKey = entry.GroupKey,
                        GroupCollapsed = groupCollapsed,
                    });
                index++;
            }
        }

        IReadOnlyList<SidebarActionHit> actions = [];
        if (display is SidebarCollapseDisplay.Expanded && input.MouseCapture)
        {
            var badge = input.GlobalMenuAttentionBadgeVisible;
            var menuWidth = SidebarTwoPaneLayoutPolicy.MenuHitWidth(badge);
            var newWidth = Math.Clamp(
                SafeDisplayText.Width(NewActionLabel),
                SidebarTwoPaneLayoutPolicy.NewButtonWidth,
                Math.Max(SidebarTwoPaneLayoutPolicy.NewButtonWidth, width - menuWidth));
            actions =
            [
                new SidebarActionHit("new", NewActionLabel, SidebarActionAlign.Left, newWidth),
                new SidebarActionHit(
                    "menu",
                    SidebarTwoPaneLayoutPolicy.MenuActionLabel(badge),
                    SidebarActionAlign.Right,
                    menuWidth,
                    AttentionBadgeVisible: badge),
            ];
        }

        return new SidebarPaneView
        {
            Id = resolved.Id,
            Slot = Slot,
            Header = HeaderText,
            Title = resolved.Title,
            Order = resolved.Order,
            Visible = visible,
            Collapsed = collapsed,
            SelectedId = selectedId,
            SelectedKind = selectedId is null ? null : SidebarFocusKind.Workspace,
            EmptyText = AttachClientViewCopy.EmptyTopology,
            ScrollId = Id,
            Actions = actions,
            Rows = rows,
        };
    }

}
