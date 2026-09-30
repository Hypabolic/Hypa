using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Sidebar;

namespace Hypa.Cli.Attach.Chrome;

public enum MobileSwitcherTarget
{
    NewWorkspace = 0,
    Workspace,
    WorkspaceClose,
    NewTab,
    Tab,
    TabClose,
    Agent,
    Cube,
    Menu,
    Close,
}

public sealed record MobileSwitcherRow(
    MobileSwitcherTarget Target,
    string Label,
    CellRect Rect,
    string? Id = null,
    string? WorkspaceId = null,
    string? TabId = null,
    string? PaneId = null,
    string? PlacementId = null,
    int? MenuIndex = null,
    int TabIndex = 0,
    CellRect CloseRect = default,
    bool Hit = false,
    bool Selected = false);

public sealed record MobileSwitcherModel(
    bool Open,
    CellRect Close,
    CellRect Viewport,
    int Scroll,
    int MaxScroll,
    IReadOnlyList<MobileSwitcherRow> Rows,
    IReadOnlyList<MobileSwitcherRow> Visible,
    int SelectedIndex = 0)
{
    public const int CloseGlyphCols = 1;

    public static MobileSwitcherModel Hidden { get; } =
        new(false, default, default, 0, 0, [], [], 0);

    public static MobileSwitcherModel Build(
        int cols,
        int rows,
        IReadOnlyList<SidebarStubRow>? sidebarRows,
        IReadOnlyList<(string Id, string Label, bool Active)>? tabs,
        int scroll = 0,
        string? focusedWorkspaceId = null,
        string? focusedTabId = null,
        string? focusedPaneId = null,
        int reservedBottom = 0,
        SidebarFrame? sidebarFrame = null)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        reservedBottom = Math.Max(0, reservedBottom);
        var headerH = NarrowLayout.HeaderRowsFor(rows);
        var closeW = NarrowLayout.SwitchWidth(cols);
        var close = rows <= 0
            ? default
            : new CellRect(cols - closeW, 0, closeW, headerH);

        // Keep one viewport row and the toast before the header separator.
        var separator = 1;
        if (rows - headerH - separator - reservedBottom < 1)
            separator = 0;
        if (rows - headerH - separator - reservedBottom < 1)
            reservedBottom = 0;

        var viewportTop = Math.Min(rows, headerH + separator);
        var viewportRows = Math.Max(0, rows - viewportTop - reservedBottom);
        var viewport = new CellRect(0, viewportTop, cols, viewportRows);

        var tabList = tabs ?? [];
        var doc = sidebarFrame is not null
            ? ComposeRows(
                sidebarFrame,
                tabList,
                focusedWorkspaceId,
                focusedTabId,
                focusedPaneId,
                cols)
            : ComposeRows(
                sidebarRows ?? [],
                tabList,
                focusedWorkspaceId,
                focusedTabId,
                focusedPaneId,
                cols);
        var maxScroll = Math.Max(0, doc.Count - viewport.Rows);
        var clamped = Math.Clamp(scroll, 0, maxScroll);
        var visible = SliceVisible(doc, viewport, clamped);
        return new MobileSwitcherModel(true, close, viewport, clamped, maxScroll, doc, visible)
            .WithSelectedIndex(-1);
    }

    public MobileSwitcherRow? Hit(int col, int row)
    {
        if (Close.Cols > 0 && Close.Contains(col, row))
        {
            return new MobileSwitcherRow(
                MobileSwitcherTarget.Close,
                "close",
                Close,
                Hit: true);
        }

        foreach (var item in Visible)
        {
            if (item.CloseRect.Cols > 0 && item.CloseRect.Contains(col, row))
            {
                var target = item.Target is MobileSwitcherTarget.Tab
                    ? MobileSwitcherTarget.TabClose
                    : MobileSwitcherTarget.WorkspaceClose;
                return item with { Target = target, Hit = true };
            }
        }

        foreach (var item in Visible)
        {
            if (item.Hit && item.Rect.Contains(col, row))
                return item;
        }

        return null;
    }

    public MobileSwitcherRow? SelectedRow()
    {
        foreach (var row in Rows)
        {
            if (row.Selected && row.Hit)
                return row;
        }

        foreach (var row in Rows)
        {
            if (row.Hit)
                return row;
        }

        return null;
    }

    public MobileSwitcherModel WithSelectedIndex(int selected)
    {
        var hits = new List<int>();
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].Hit)
                hits.Add(i);
        }

        if (hits.Count == 0)
            return this with { SelectedIndex = 0 };

        int index;
        if (selected < 0)
        {
            index = 0;
            for (var i = 0; i < hits.Count; i++)
            {
                if (Rows[hits[i]].Selected)
                {
                    index = i;
                    break;
                }
            }
        }
        else
        {
            index = selected % hits.Count;
            if (index < 0)
                index += hits.Count;
        }

        var chosen = hits[index];
        var scroll = Scroll;
        if (Viewport.Rows > 0)
        {
            if (chosen < scroll)
                scroll = chosen;
            else if (chosen >= scroll + Viewport.Rows)
                scroll = chosen - Viewport.Rows + 1;
            scroll = Math.Clamp(scroll, 0, MaxScroll);
        }

        var rows = new MobileSwitcherRow[Rows.Count];
        for (var i = 0; i < Rows.Count; i++)
            rows[i] = Rows[i] with { Selected = i == chosen };

        return this with
        {
            Rows = rows,
            Visible = SliceVisible(rows, Viewport, scroll),
            SelectedIndex = index,
            Scroll = scroll,
        };
    }

    public int DropTabIndex(int row)
    {
        var tabs = new List<MobileSwitcherRow>();
        foreach (var item in Rows)
        {
            if (item.Target is MobileSwitcherTarget.Tab)
                tabs.Add(item);
        }

        if (tabs.Count == 0)
            return 0;

        var visibleTabs = new List<MobileSwitcherRow>();
        foreach (var item in Visible)
        {
            if (item.Target is MobileSwitcherTarget.Tab)
                visibleTabs.Add(item);
        }

        if (visibleTabs.Count == 0)
            return Math.Clamp(tabs[0].TabIndex, 0, tabs.Count - 1);

        var pick = 0;
        for (var i = 0; i < visibleTabs.Count; i++)
        {
            if (row >= visibleTabs[i].Rect.Row)
                pick = i;
        }

        return Math.Clamp(visibleTabs[pick].TabIndex, 0, tabs.Count - 1);
    }

    private static List<MobileSwitcherRow> SliceVisible(
        IReadOnlyList<MobileSwitcherRow> doc,
        CellRect viewport,
        int scroll)
    {
        var visible = new List<MobileSwitcherRow>();
        for (var i = 0; i < viewport.Rows && scroll + i < doc.Count; i++)
        {
            var row = doc[scroll + i];
            var rect = new CellRect(viewport.Col, viewport.Row + i, viewport.Cols, 1);
            var closeable = row.Target is MobileSwitcherTarget.Tab or MobileSwitcherTarget.Workspace
                && viewport.Cols > CloseGlyphCols;
            var closeRect = closeable
                ? new CellRect(rect.EndCol - CloseGlyphCols, rect.Row, CloseGlyphCols, 1)
                : default;
            visible.Add(row with { Rect = rect, CloseRect = closeRect });
        }

        return visible;
    }

    private static List<MobileSwitcherRow> ComposeRows(
        SidebarFrame frame,
        IReadOnlyList<(string Id, string Label, bool Active)> tabs,
        string? focusedWorkspaceId,
        string? focusedTabId,
        string? focusedPaneId,
        int cols)
    {
        var rows = new List<MobileSwitcherRow>();
        var compact = frame.Display is SidebarCollapseDisplay.Compact;
        var agents = frame.Pane(SidebarPaneSlot.Agents);
        var spaces = frame.Pane(SidebarPaneSlot.Spaces);
        var cubes = frame.Pane(SidebarPaneSlot.Cubes);
        var agentCards = Cards(agents, compact);
        var spaceCards = Cards(spaces, compact);
        var cubeCards = Cards(cubes, compact);

        if (agentCards.Count > 0
            || (agents is { Collapsed: false, Rows.Count: 0 }
                && !string.IsNullOrEmpty(agents.EmptyText)))
        {
            rows.Add(Title("agents"));
            if (agentCards.Count == 0 && agents is not null)
                rows.Add(Plain(agents.EmptyText, cols));
            foreach (var row in agentCards)
            {
                rows.Add(new MobileSwitcherRow(
                    MobileSwitcherTarget.Agent,
                    Clip(LabelOf(row, compact), LabelCols(cols, closeable: false)),
                    default,
                    row.Id,
                    PaneId: row.PaneId ?? row.Id,
                    Hit: true,
                    Selected: row.Selected
                        || (!string.IsNullOrWhiteSpace(focusedPaneId)
                            && string.Equals(row.Id, focusedPaneId, StringComparison.Ordinal))));
            }
        }

        rows.Add(Title("spaces"));
        var hasNew = spaces is not null
            && (HasAction(spaces, "new") || spaceCards.Count > 0 || frame.Panes.Count == 0);
        if (hasNew)
        {
            rows.Add(new MobileSwitcherRow(
                MobileSwitcherTarget.NewWorkspace,
                "new workspace",
                default,
                Hit: true));
        }

        if (spaceCards.Count == 0
            && spaces is { Collapsed: false, Rows.Count: 0 }
            && !string.IsNullOrEmpty(spaces.EmptyText))
        {
            rows.Add(Plain(spaces.EmptyText, cols));
        }

        foreach (var row in spaceCards)
        {
            rows.Add(new MobileSwitcherRow(
                MobileSwitcherTarget.Workspace,
                Clip(LabelOf(row, compact), LabelCols(cols, closeable: true)),
                default,
                row.Id,
                WorkspaceId: row.WorkspaceId ?? row.Id,
                Hit: true,
                Selected: row.Selected
                    || (!string.IsNullOrWhiteSpace(focusedWorkspaceId)
                        && string.Equals(row.Id, focusedWorkspaceId, StringComparison.Ordinal))));
        }

        if (cubes is not null
            && (cubeCards.Count > 0
                || (cubes is { Collapsed: false, Rows.Count: 0 } && cubes.EmptyText.Length > 0)))
        {
            rows.Add(Title("Cubes"));
            if (cubeCards.Count == 0)
                rows.Add(Plain(cubes.EmptyText, cols));
            foreach (var row in cubeCards)
            {
                rows.Add(new MobileSwitcherRow(
                    MobileSwitcherTarget.Cube,
                    Clip(LabelOf(row, compact), LabelCols(cols, closeable: false)),
                    default,
                    row.Id,
                    PlacementId: row.PlacementId ?? row.Id,
                    Hit: true,
                    Selected: row.Selected));
            }
        }

        AppendTabsAndMenu(rows, tabs, focusedTabId, cols);
        return rows;
    }

    private static List<MobileSwitcherRow> ComposeRows(
        IReadOnlyList<SidebarStubRow> sidebarRows,
        IReadOnlyList<(string Id, string Label, bool Active)> tabs,
        string? focusedWorkspaceId,
        string? focusedTabId,
        string? focusedPaneId,
        int cols)
    {
        var rows = new List<MobileSwitcherRow>();
        var agents = new List<SidebarStubRow>();
        var spaces = new List<SidebarStubRow>();
        var cubes = new List<SidebarStubRow>();
        var hasNewWorkspace = false;
        var hasCubesSection = false;
        var section = "";
        foreach (var stub in sidebarRows)
        {
            if (stub.Kind is SidebarStubKind.Section)
            {
                section = stub.Id;
                if (string.Equals(stub.Id, SidebarTokenGrammar.CubesId, StringComparison.Ordinal))
                    hasCubesSection = true;
                continue;
            }

            if (stub.Kind is SidebarStubKind.New)
            {
                hasNewWorkspace = true;
                continue;
            }

            if (stub.Kind is SidebarStubKind.Menu or SidebarStubKind.Gap)
                continue;

            if (stub.Kind is SidebarStubKind.Agent)
                agents.Add(stub);
            else if (stub.Kind is SidebarStubKind.Workspace)
                spaces.Add(stub);
            else if (stub.Kind is SidebarStubKind.Cube)
                cubes.Add(stub);
            else if (stub.Kind is SidebarStubKind.Empty)
            {
                // Compact stubs omit section headers. Route empty rows by id.
                if (IsSectionEmpty(stub, section, SidebarTokenGrammar.AgentsId))
                    agents.Add(stub);
                else if (IsSectionEmpty(stub, section, SidebarTokenGrammar.SpacesId))
                    spaces.Add(stub);
                else if (IsSectionEmpty(stub, section, SidebarTokenGrammar.CubesId))
                    cubes.Add(stub);
            }
        }

        if (agents.Count > 0)
        {
            rows.Add(Title("agents"));
            foreach (var agent in agents)
            {
                if (agent.Kind is SidebarStubKind.Empty)
                {
                    rows.Add(Plain(agent.Label, cols));
                    continue;
                }

                rows.Add(new MobileSwitcherRow(
                    MobileSwitcherTarget.Agent,
                    Clip(agent.Label, LabelCols(cols, closeable: false)),
                    default,
                    agent.Id,
                    PaneId: agent.Id,
                    Hit: true,
                    Selected: agent.Selected
                        || (!string.IsNullOrWhiteSpace(focusedPaneId)
                            && string.Equals(agent.Id, focusedPaneId, StringComparison.Ordinal))));
            }
        }

        rows.Add(Title("spaces"));
        if (hasNewWorkspace || spaces.Count > 0 || sidebarRows.Count == 0)
        {
            rows.Add(new MobileSwitcherRow(
                MobileSwitcherTarget.NewWorkspace,
                "new workspace",
                default,
                Hit: true));
        }

        foreach (var space in spaces)
        {
            if (space.Kind is SidebarStubKind.Empty)
            {
                rows.Add(Plain(space.Label, cols));
                continue;
            }

            rows.Add(new MobileSwitcherRow(
                MobileSwitcherTarget.Workspace,
                Clip(space.Label, LabelCols(cols, closeable: true)),
                default,
                space.Id,
                WorkspaceId: space.Id,
                Hit: true,
                Selected: space.Selected
                    || (!string.IsNullOrWhiteSpace(focusedWorkspaceId)
                        && string.Equals(space.Id, focusedWorkspaceId, StringComparison.Ordinal))));
        }

        if (hasCubesSection || cubes.Count > 0)
        {
            rows.Add(Title("Cubes"));
            if (cubes.Count == 0)
                rows.Add(Plain(CubesEmptyLabel(sidebarRows), cols));
            foreach (var cube in cubes)
            {
                if (cube.Kind is SidebarStubKind.Empty)
                {
                    rows.Add(Plain(cube.Label, cols));
                    continue;
                }

                rows.Add(new MobileSwitcherRow(
                    MobileSwitcherTarget.Cube,
                    Clip(cube.Label, LabelCols(cols, closeable: false)),
                    default,
                    cube.Id,
                    PlacementId: cube.Id,
                    Hit: true,
                    Selected: cube.Selected));
            }
        }

        AppendTabsAndMenu(rows, tabs, focusedTabId, cols);
        return rows;
    }

    private static void AppendTabsAndMenu(
        List<MobileSwitcherRow> rows,
        IReadOnlyList<(string Id, string Label, bool Active)> tabs,
        string? focusedTabId,
        int cols)
    {

        rows.Add(Title("tabs"));
        rows.Add(new MobileSwitcherRow(
            MobileSwitcherTarget.NewTab,
            "new tab",
            default,
            Hit: true));
        for (var i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            var selected = tab.Active
                || (!string.IsNullOrWhiteSpace(focusedTabId)
                    && string.Equals(tab.Id, focusedTabId, StringComparison.Ordinal));
            rows.Add(new MobileSwitcherRow(
                MobileSwitcherTarget.Tab,
                Clip(string.IsNullOrWhiteSpace(tab.Label) ? tab.Id : tab.Label, LabelCols(cols, closeable: true)),
                default,
                tab.Id,
                TabId: tab.Id,
                TabIndex: i,
                Hit: true,
                Selected: selected));
        }

        rows.Add(Title("menu"));
        var menu = GlobalMenuModel.Items();
        for (var i = 0; i < menu.Count; i++)
        {
            rows.Add(new MobileSwitcherRow(
                MobileSwitcherTarget.Menu,
                Clip(menu[i].Label, LabelCols(cols, closeable: false)),
                default,
                menu[i].Id,
                MenuIndex: i,
                Hit: true));
        }
    }

    private static IReadOnlyList<SidebarPaintedRow> Cards(SidebarPaneView? pane, bool compact)
    {
        if (pane is null || pane.Collapsed)
            return [];

        var cards = new List<SidebarPaintedRow>();
        foreach (var row in pane.Rows)
        {
            if (row.CardRowIndex != 0)
                continue;
            if (!compact && row.Label.Length == 0 && !row.Selected)
                continue;
            cards.Add(row);
        }

        return cards;
    }

    private static string LabelOf(SidebarPaintedRow row, bool compact) =>
        compact && row.CompactLabel.Length > 0 ? row.CompactLabel : row.Label;

    private static bool HasAction(SidebarPaneView pane, string id)
    {
        foreach (var action in pane.Actions)
        {
            if (string.Equals(action.Id, id, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsSectionEmpty(SidebarStubRow stub, string section, string sectionId) =>
        string.Equals(section, sectionId, StringComparison.Ordinal)
        || string.Equals(stub.Id, sectionId + ":empty", StringComparison.Ordinal);

    private static string CubesEmptyLabel(IReadOnlyList<SidebarStubRow> sidebarRows)
    {
        foreach (var stub in sidebarRows)
        {
            if (stub.Kind is SidebarStubKind.Empty
                && string.Equals(stub.Id, SidebarTokenGrammar.CubesId + ":empty", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(stub.Label))
                return stub.Label;
        }

        return SidebarTokenGrammar.CubesEmptyText;
    }

    private static MobileSwitcherRow Title(string label) =>
        new(MobileSwitcherTarget.Close, label, default, Hit: false);

    private static MobileSwitcherRow Plain(string label, int cols) =>
        new(MobileSwitcherTarget.Close, Clip(label, cols), default, Hit: false);

    private static int LabelCols(int cols, bool closeable)
    {
        var reserve = 1;
        if (closeable)
            reserve += 1 + CloseGlyphCols;
        return Math.Max(1, cols - reserve);
    }

    private static string Clip(string? text, int cols) =>
        SafeDisplayText.Clip(text, Math.Max(1, cols));
}
