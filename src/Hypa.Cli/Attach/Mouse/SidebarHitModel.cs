using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;

namespace Hypa.Cli.Attach.Mouse;

public enum SidebarStubKind
{
    Workspace,
    Agent,
    Cube,
    Menu,
    New,
    Section,
    Empty,
    Gap,
    Sort,
    HiddenPane,
    Group,
    CollectionItem,
    AddCube,
    ShareMux,
}

public sealed record SidebarStubRow(
    SidebarStubKind Kind,
    string Id,
    string Label,
    int RowOffset,
    bool Selected = false);

public sealed record SidebarRowHit(
    SidebarStubKind Kind,
    string Id,
    CellRect Rect,
    string Label,
    bool Selected = false,
    SidebarPaneSlot PaneSlot = SidebarPaneSlot.Spaces,
    IReadOnlyList<SidebarRowToken>? Tokens = null,
    SidebarPrefixRole PrefixRole = SidebarPrefixRole.None,
    int NameColumn = 0,
    int IndentCols = 0,
    int CardRowIndex = 0,
    string? GroupKey = null,
    CellRect? GroupToggle = null,
    bool Expandable = false,
    bool TreeCollapsed = false,
    bool Hidden = false,
    SidebarCollectionActivation? CollectionActivation = null);

public static class SidebarHitModel
{
    public const int DefaultMinWidth = 18;

    public const int DefaultMaxWidth = 36;

    public static int ClampWidth(int width, int min = DefaultMinWidth, int max = DefaultMaxWidth)
    {
        if (min < 1)
            min = 1;
        if (max < min)
            max = min;
        return Math.Clamp(width, min, max);
    }

    public static IReadOnlyList<SidebarRowHit> Hits(CellRect sidebar, IReadOnlyList<SidebarStubRow>? rows)
    {
        var hits = new List<SidebarRowHit>();
        var labelCols = Math.Max(1, sidebar.Cols - 1);
        if (rows is null || sidebar.Rows <= 0)
            return hits;

        var footer = new List<SidebarStubRow>();
        var body = new List<SidebarStubRow>();
        foreach (var row in rows)
        {
            if (row.Kind is SidebarStubKind.New or SidebarStubKind.Menu)
                footer.Add(row);
            else
                body.Add(row);
        }

        var footerCount = Math.Min(footer.Count, sidebar.Rows);
        var bodyLimit = sidebar.Rows - footerCount;
        for (var i = 0; i < body.Count && i < bodyLimit; i++)
        {
            var row = body[i];
            hits.Add(new SidebarRowHit(
                row.Kind,
                row.Id,
                new CellRect(sidebar.Col, sidebar.Row + i, labelCols, 1),
                row.Label,
                row.Selected));
        }

        for (var i = 0; i < footerCount; i++)
        {
            var row = footer[i];
            hits.Add(new SidebarRowHit(
                row.Kind,
                row.Id,
                new CellRect(sidebar.Col, sidebar.EndRow - footerCount + i, labelCols, 1),
                row.Label,
                row.Selected));
        }

        return hits;
    }

    public static IReadOnlyList<SidebarRowHit> Hits(
        CellRect sidebar,
        SidebarTwoPaneLayout layout,
        SidebarFrame frame,
        int spacesScroll,
        int agentsScroll,
        IReadOnlyDictionary<string, int>? resourceScrolls = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var hits = new List<SidebarRowHit>();
        if (sidebar.Rows <= 0 || frame.Display is SidebarCollapseDisplay.Hidden)
            return hits;

        var compact = frame.Display is SidebarCollapseDisplay.Compact;
        foreach (var pane in frame.Panes)
        {
            if (!pane.Visible)
                continue;
            var rect = pane.Slot is SidebarPaneSlot.Resource or SidebarPaneSlot.Cubes
                ? layout.RectFor(pane.Slot, pane.Id)
                : layout.RectFor(pane.Slot);
            if (rect.Rows <= 0 || rect.Cols <= 0)
                continue;
            var scroll = pane.Slot switch
            {
                SidebarPaneSlot.Agents => agentsScroll,
                SidebarPaneSlot.Spaces => spacesScroll,
                SidebarPaneSlot.Resource or SidebarPaneSlot.Cubes => resourceScrolls is not null
                    && resourceScrolls.TryGetValue(pane.Id, out var value)
                    ? value
                    : 0,
                _ => 0,
            };
            AddPaneHits(hits, pane, layout, rect, scroll, compact);
        }

        return hits;
    }

    private static void AddPaneHits(
        List<SidebarRowHit> hits,
        SidebarPaneView pane,
        SidebarTwoPaneLayout layout,
        CellRect rect,
        int scroll,
        bool compact)
    {
        if (!compact)
        {
            var headerRow = SidebarTwoPaneLayoutPolicy.HeaderRow(
                rect,
                pane.Slot,
                compact,
                layout.HasLeadingDivider(pane.Slot, pane.Id));
            // on its own. Emit sort before the full-width section hit so
            // ChromeHitTest.Hit does not collapse the pane.
            if (pane.Slot is SidebarPaneSlot.Agents)
            {
                foreach (var action in pane.Actions)
                    hits.Add(ActionHit(action, rect, headerRow, pane.Slot));
            }

            if (headerRow >= rect.Row && headerRow < rect.EndRow)
            {
                var sectionCols = rect.Cols;
                if (pane.Slot is SidebarPaneSlot.Agents)
                {
                    var sortWidth = 0;
                    foreach (var action in pane.Actions)
                    {
                        if (action.Align is SidebarActionAlign.Right)
                            sortWidth = Math.Max(sortWidth, action.Width);
                    }

                    sectionCols = Math.Max(1, rect.Cols - sortWidth);
                }

                hits.Add(new SidebarRowHit(
                    SidebarStubKind.Section,
                    pane.Id,
                    new CellRect(rect.Col, headerRow, sectionCols, 1),
                    pane.Header,
                    PaneSlot: pane.Slot));
            }

            var footer = layout.FooterRect(pane.Slot, compact, pane.Id);
            if (pane.Slot is SidebarPaneSlot.Spaces or SidebarPaneSlot.Cubes && footer.Rows > 0)
            {
                foreach (var action in pane.Actions)
                    hits.Add(ActionHit(action, footer, footer.Row, pane.Slot));
            }
        }

        var body = pane.Slot is SidebarPaneSlot.Resource or SidebarPaneSlot.Cubes
            ? layout.BodyRectForSection(pane.Slot, compact, pane.Id)
            : layout.BodyRect(pane.Slot, compact);
        var visible = Math.Max(0, body.Rows);
        if (pane.Collapsed || visible <= 0)
            return;

        if (pane.Rows.Count == 0)
        {
            if (!compact && pane.EmptyText.Length > 0 && body.Rows > 0)
            {
                hits.Add(new SidebarRowHit(
                    SidebarStubKind.Empty,
                    pane.Id + ":empty",
                    new CellRect(body.Col, body.Row, body.Cols, 1),
                    pane.EmptyText,
                    PaneSlot: pane.Slot));
            }

            return;
        }

        var visibleRows = SidebarTwoPaneLayoutPolicy.VisibleCardRows(pane.Rows, scroll, visible);
        for (var i = 0; i < visibleRows.Count; i++)
        {
            var row = visibleRows[i];
            var kind = compact || row.Label.Length > 0 || row.Selected
                ? row.Kind switch
                {
                    SidebarRowKind.Workspace => SidebarStubKind.Workspace,
                    SidebarRowKind.Agent => SidebarStubKind.Agent,
                    SidebarRowKind.Cube => SidebarStubKind.Cube,
                    SidebarRowKind.HiddenPane => SidebarStubKind.HiddenPane,
                    SidebarRowKind.Group => SidebarStubKind.Group,
                    SidebarRowKind.CollectionItem => SidebarStubKind.CollectionItem,
                    _ => SidebarStubKind.Gap,
                }
                : SidebarStubKind.Gap;
            var label = compact ? row.CompactLabel : row.Label;
            hits.Add(new SidebarRowHit(
                kind,
                row.Id,
                new CellRect(body.Col, body.Row + i, body.Cols, 1),
                label,
                row.Selected,
                pane.Slot,
                row.Tokens,
                row.PrefixRole,
                row.NameColumn,
                row.IndentCols,
                row.CardRowIndex,
                row.GroupKey,
                compact ? null : WorktreeWorkspaceGrouping.ToggleRect(body, i, row),
                row.Expandable,
                row.TreeCollapsed,
                row.Hidden,
                row.CollectionActivation));
        }
    }

    private static SidebarRowHit ActionHit(
        SidebarActionHit action,
        CellRect pane,
        int row,
        SidebarPaneSlot slot)
    {
        var width = Math.Min(action.Width, Math.Max(1, pane.Cols));
        var col = action.Align is SidebarActionAlign.Right
            ? pane.Col + Math.Max(0, pane.Cols - width)
            : pane.Col;
        var kind = action.Id switch
        {
            "new" => SidebarStubKind.New,
            "menu" => SidebarStubKind.Menu,
            "sort" => SidebarStubKind.Sort,
            "add_cube" => SidebarStubKind.AddCube,
            "share_mux" => SidebarStubKind.ShareMux,
            _ => SidebarStubKind.Section,
        };
        return new SidebarRowHit(
            kind,
            action.Id,
            new CellRect(col, row, width, 1),
            action.Label.Trim(),
            PaneSlot: slot);
    }
}
