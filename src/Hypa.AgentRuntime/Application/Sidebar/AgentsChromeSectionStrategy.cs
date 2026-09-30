using Hypa.AgentRuntime.Domain.AttachConfig;
using SafeDisplayText = Hypa.AgentRuntime.Application.SafeDisplayText;

namespace Hypa.AgentRuntime.Application.Sidebar;

public sealed class AgentsChromeSectionStrategy : IChromeSectionStrategy
{
    public const string HeaderText = " agents";

    public string Id => SidebarTokenGrammar.AgentsId;

    public bool IsBuiltIn => true;

    public SidebarPaneSlot Slot => SidebarPaneSlot.Agents;

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
        var panes = SidebarTokenGrammar.SortAgents(
            input.Panes,
            input.Workspaces,
            input.Tabs,
            input.Ui.AgentPanelSort);
        var viewActive = !string.IsNullOrWhiteSpace(input.AgentViewLabel);
        if (viewActive)
        {
            panes = OrderAgentsByView(input.Panes, input.AgentOrder);
            if (input.AgentViewHasSort is false)
            {
                panes = SidebarTokenGrammar.SortAgents(
                    panes,
                    input.Workspaces,
                    input.Tabs,
                    input.Ui.AgentPanelSort);
            }
        }

        var records = HiddenPaneSnapshotMapper.FromPaneItems(input.Panes);
        var visibleRecords = new List<HiddenPaneRecord>(panes.Count);
        foreach (var pane in panes)
            visibleRecords.Add(HiddenPaneSnapshotMapper.FromPaneItem(pane));
        var tree = HiddenPaneTreeBuilder.Build(records, visibleRecords, input.CollapsedTreeIds);
        var byId = input.Panes.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var workspaces = input.Workspaces.ToDictionary(w => w.Id, StringComparer.Ordinal);
        var tabs = input.Tabs.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var rows = new List<SidebarPaintedRow>();
        string? selectedId = null;
        if (!collapsed)
        {
            var index = 1;
            var compact = display is SidebarCollapseDisplay.Compact;
            foreach (var node in tree.Rows)
            {
                var selected = node.PaneId is { } paneId
                    && (string.Equals(paneId, input.FocusedPaneId, StringComparison.Ordinal)
                        || string.Equals(paneId, input.NavigatedPaneId, StringComparison.Ordinal));
                if (selected)
                    selectedId = node.PaneId;
                var indent = node.Depth * 2;
                var prefix = node.Depth <= 0
                    ? SidebarPrefixRole.None
                    : node.LastChild
                        ? SidebarPrefixRole.TreeLast
                        : SidebarPrefixRole.TreeBranch;
                var kind = node.Kind switch
                {
                    HiddenPaneRowKind.Background => SidebarRowKind.Group,
                    HiddenPaneRowKind.Child or HiddenPaneRowKind.Revealed => SidebarRowKind.HiddenPane,
                    _ when node.Hidden || !string.IsNullOrWhiteSpace(node.ParentPaneId)
                        => SidebarRowKind.HiddenPane,
                    _ => SidebarRowKind.Agent,
                };
                if (node.Kind is HiddenPaneRowKind.Background || node.PaneId is null
                    || !byId.TryGetValue(node.PaneId, out var pane))
                {
                    var groupLabel = HiddenPaneLabelFit.Fit(
                        node.Label,
                        node.AgentKind,
                        node.State,
                        Math.Max(1, width - 1 - indent));
                    var icon = SidebarTokenGrammar.StateIcon(node.State, input.Ui.StatusIndicators);
                    rows.Add(new SidebarPaintedRow
                    {
                        Id = node.Id,
                        Kind = kind,
                        Label = compact
                            ? SidebarSectionComposer.CompactLabel(index, icon)
                            : groupLabel,
                        CompactLabel = SidebarSectionComposer.CompactLabel(index, icon),
                        State = node.State,
                        WorkspaceId = node.WorkspaceId,
                        TabId = node.TabId,
                        PaneId = node.PaneId,
                        Selected = selected,
                        PaneSlot = SidebarPaneSlot.Agents,
                        ScrollId = Id,
                        PrefixRole = prefix,
                        IndentCols = indent,
                        Expandable = node.Expandable,
                        TreeCollapsed = node.Collapsed,
                        Hidden = node.Hidden,
                    });
                    index++;
                    continue;
                }

                var workspace = workspaces.GetValueOrDefault(pane.WorkspaceId);
                var tab = tabs.GetValueOrDefault(pane.TabId);
                var values = SidebarSectionComposer.ValuesForPane(input, pane, workspace, tab);
                if (!string.Equals(node.State, pane.State, StringComparison.Ordinal)
                    && string.Equals(node.State, SidebarTokenGrammar.Blocked, StringComparison.Ordinal))
                {
                    values = values with
                    {
                        StateIcon = SidebarTokenGrammar.StateIcon(node.State, input.Ui.StatusIndicators),
                        StateText = SidebarTokenGrammar.StateText(node.State),
                    };
                }

                var compactLabel = SidebarSectionComposer.CompactLabel(
                    index,
                    values.StateIcon);
                var template = new SidebarPaintedRow
                {
                    Id = pane.Id,
                    Kind = kind,
                    Label = compactLabel,
                    CompactLabel = compactLabel,
                    State = SidebarTokenGrammar.CanonicalState(node.State),
                    WorkspaceId = pane.WorkspaceId,
                    TabId = pane.TabId,
                    PaneId = pane.Id,
                    Selected = selected,
                    PaneSlot = SidebarPaneSlot.Agents,
                    ScrollId = Id,
                    PrefixRole = prefix,
                    IndentCols = indent,
                    Expandable = node.Expandable,
                    TreeCollapsed = node.Collapsed,
                    Hidden = node.Hidden,
                };
                if (UsesPaneIdentityRows(kind, node))
                {
                    // Lines 821-834 paint status and attention beside it.
                    rows.Add(ComposeHiddenIdentityRow(
                        template,
                        node,
                        values,
                        compact,
                        Math.Max(1, width - 1 - indent)));
                    index++;
                    continue;
                }

                SidebarSectionComposer.AppendTokenRows(
                    rows,
                    SidebarTokenGrammar.RowsForAgent(resolved.Config, pane.Agent),
                    values,
                    width,
                    compact,
                    resolved.Config.RowGap,
                    template);
                index++;
            }
        }

        IReadOnlyList<SidebarActionHit> actions = [];
        if (display is SidebarCollapseDisplay.Expanded && !viewActive)
        {
            var label = SortLabel(input.Ui.AgentPanelSort);
            actions =
            [
                new SidebarActionHit("sort", label, SidebarActionAlign.Right, SafeDisplayText.Width(label)),
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
            SelectedKind = selectedId is null ? null : SidebarFocusKind.Agent,
            // leave an ordinary empty Agents body blank. A filtered view
            // may set a match message.
            EmptyText = viewActive ? "No matching agents" : "",
            ScrollId = Id,
            Actions = actions,
            Rows = rows,
        };
    }

    public static string SortLabel(AgentPanelSort sort) =>
        sort is AgentPanelSort.Priority ? "priority" : "grouped";

    internal static readonly IReadOnlyList<string> PaneIdentityTokens =
        ["state_icon", "pane", "agent", "state_text"];

    internal static SidebarPaintedRow ComposeHiddenIdentityRow(
        SidebarPaintedRow template,
        HiddenPaneTreeRow node,
        SidebarTokenValues values,
        bool compact,
        int maxCols)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(node);
        var agentKind = SidebarTokenGrammar.IsOrdinaryShellKind(node.AgentKind)
            ? ""
            : SafeDisplayText.Encode(node.AgentKind);
        var fitted = HiddenPaneLabelFit.Fit(
            node.Label,
            agentKind,
            node.State,
            maxCols);
        var identity = values with
        {
            Pane = string.IsNullOrWhiteSpace(node.Label) ? values.Pane : node.Label,
            Agent = agentKind,
            StateText = string.Equals(
                    SidebarTokenGrammar.CanonicalState(node.State),
                    SidebarTokenGrammar.Unknown,
                    StringComparison.Ordinal)
                ? ""
                : SidebarTokenGrammar.StateText(node.State),
        };
        return template with
        {
            Label = compact ? template.CompactLabel : fitted,
            Tokens = compact ? [] : SidebarTokenGrammar.TokensForRow(PaneIdentityTokens, identity),
        };
    }

    internal static bool UsesPaneIdentityRows(SidebarRowKind kind, HiddenPaneTreeRow node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (kind is SidebarRowKind.HiddenPane)
            return true;
        if (node.Hidden)
            return true;
        return node.Kind is HiddenPaneRowKind.Child or HiddenPaneRowKind.Revealed;
    }

    private static IReadOnlyList<SidebarPaneItem> OrderAgentsByView(
        IReadOnlyList<SidebarPaneItem> panes,
        IReadOnlyList<string> order)
    {
        var byId = panes.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var list = new List<SidebarPaneItem>();
        foreach (var id in order)
        {
            if (byId.TryGetValue(id, out var pane))
                list.Add(pane);
        }

        return list;
    }
}
