using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;

namespace Hypa.Cli.Attach.Chrome;

public enum ChromeHitKind
{
    Tab,
    TabClose,
    TabNew,
    TabOverflowPrev,
    TabOverflowNext,
    SplitBorder,
    Zoom,
    PaneClose,
    Pane,
    Scrollbar,
    SidebarEdge,
    SidebarWorkspace,
    SidebarAgent,
    SidebarCube,
    SidebarMenu,
    SidebarNew,
    SidebarSection,
    SidebarSectionDivider,
    SidebarSort,
    SidebarTreeToggle,
    SidebarHiddenPane,
    SidebarWorkspaceClose,
    SidebarWorktreeGroupToggle,
    SidebarCollectionItem,
    SidebarAddCube,
    SidebarShareMux,
    Toast,
    ContextMenuItem,
    MobileSwitch,
    MobileSwitcherClose,
    MobileSwitcherMenu,
    Popup,
}

public sealed record ChromeHit(
    ChromeHitKind Kind,
    string? TabId = null,
    string? PaneId = null,
    IReadOnlyList<int>? Path = null,
    string? WorkspaceId = null,
    string? PlacementId = null,
    bool IsFrame = false,
    int? MenuIndex = null,
    string? GroupKey = null,
    SidebarCollectionActivation? CollectionActivation = null,
    bool SecondaryActivation = false);

public static class ChromeHitTest
{
    public static ChromeHit? Hit(
        LayoutChromeGeometry geometry,
        int col,
        int row,
        ContextMenuModel? menu = null,
        AttachClientMode? paintMode = null,
        bool popupOpen = false)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        // Session-modal even when WINCH hid PopupFrame (content below min 6×4).
        if (popupOpen || geometry.PopupFrame is not null)
            return new ChromeHit(ChromeHitKind.Popup, PaneId: "popup");

        if (menu is not null && menu.TryHit(col, row, out var menuIndex))
            return new ChromeHit(ChromeHitKind.ContextMenuItem, MenuIndex: menuIndex);

        for (var i = geometry.ToastHits.Count - 1; i >= 0; i--)
        {
            var toast = geometry.ToastHits[i];
            if (string.IsNullOrWhiteSpace(toast.PaneId))
                continue;
            if (toast.Rect.Contains(col, row))
                return new ChromeHit(ChromeHitKind.Toast, PaneId: toast.PaneId);
        }

        if (geometry.MobileSwitcher is { Open: true } switcher)
        {
            var target = switcher.Hit(col, row);
            if (target is not null)
                return FromSwitcher(target);
            return null;
        }

        if (geometry.MobileHeader is { } header && header.Switch.Contains(col, row))
            return new ChromeHit(ChromeHitKind.MobileSwitch);

        if (geometry.SidebarEdge is { } edge && edge.Contains(col, row))
            return new ChromeHit(ChromeHitKind.SidebarEdge);

        if (geometry.SectionDivider is { } sectionDivider && sectionDivider.Contains(col, row))
            return new ChromeHit(ChromeHitKind.SidebarSectionDivider);

        foreach (var side in geometry.SidebarRows)
        {
            if (side.GroupToggle is { } toggle && toggle.Contains(col, row))
            {
                return new ChromeHit(
                    ChromeHitKind.SidebarWorktreeGroupToggle,
                    WorkspaceId: side.Id,
                    GroupKey: side.GroupKey);
            }

            if (!side.Rect.Contains(col, row))
                continue;
            if (side.Expandable)
            {
                var toggleCols = Math.Max(2, side.IndentCols + 2);
                if (col < side.Rect.Col + toggleCols || side.Kind is SidebarStubKind.Group)
                {
                    return new ChromeHit(ChromeHitKind.SidebarTreeToggle, PaneId: side.Id);
                }
            }

            return side.Kind switch
            {
                SidebarStubKind.Workspace => new ChromeHit(
                    ChromeHitKind.SidebarWorkspace,
                    WorkspaceId: side.Id),
                SidebarStubKind.Agent => new ChromeHit(ChromeHitKind.SidebarAgent, PaneId: side.Id),
                SidebarStubKind.HiddenPane => new ChromeHit(
                    ChromeHitKind.SidebarHiddenPane,
                    PaneId: side.Id),
                SidebarStubKind.Group => new ChromeHit(ChromeHitKind.SidebarTreeToggle, PaneId: side.Id),
                SidebarStubKind.Cube => new ChromeHit(
                    ChromeHitKind.SidebarCube,
                    PlacementId: side.Id),
                SidebarStubKind.CollectionItem => new ChromeHit(
                    ChromeHitKind.SidebarCollectionItem,
                    PaneId: side.Id,
                    WorkspaceId: side.PaneSlot.ToString(),
                    CollectionActivation: side.CollectionActivation),
                SidebarStubKind.New => new ChromeHit(ChromeHitKind.SidebarNew),
                SidebarStubKind.AddCube => new ChromeHit(ChromeHitKind.SidebarAddCube),
                SidebarStubKind.ShareMux => new ChromeHit(ChromeHitKind.SidebarShareMux),
                SidebarStubKind.Section => new ChromeHit(ChromeHitKind.SidebarSection, WorkspaceId: side.Id),
                SidebarStubKind.Menu => new ChromeHit(ChromeHitKind.SidebarMenu),
                SidebarStubKind.Sort => new ChromeHit(ChromeHitKind.SidebarSort),
                _ => null,
            };
        }

        var bar = geometry.TabBar;
        var skipTabRow = ModeBarModel.ReplacesTabRow(
                geometry.PaintMode,
                geometry.TabSlot,
                geometry.HasEndpointError)
            || (paintMode is { } liveMode
                && ModeBarModel.ReplacesTabRow(liveMode, geometry.TabSlot, geometry.HasEndpointError));
        if (bar.Visible && !skipTabRow)
        {
            if (Contains(bar.NewTab, col, row))
                return new ChromeHit(ChromeHitKind.TabNew);
            if (Contains(bar.OverflowPrev, col, row))
                return new ChromeHit(ChromeHitKind.TabOverflowPrev);
            if (Contains(bar.OverflowNext, col, row))
                return new ChromeHit(ChromeHitKind.TabOverflowNext);
            foreach (var tab in bar.Tabs)
            {
                if (Contains(tab.Close, col, row))
                    return new ChromeHit(ChromeHitKind.TabClose, TabId: tab.TabId);
                if (tab.Rect.Contains(col, row))
                    return new ChromeHit(ChromeHitKind.Tab, TabId: tab.TabId);
            }

            if (Contains(bar.Zoom, col, row))
                return new ChromeHit(ChromeHitKind.Zoom);
        }

        foreach (var split in geometry.SplitBorders)
        {
            if (split.Rect.Contains(col, row))
                return new ChromeHit(ChromeHitKind.SplitBorder, Path: split.Path);
        }

        foreach (var pane in geometry.Panes)
        {
            if (Contains(pane.Close, col, row))
                return new ChromeHit(ChromeHitKind.PaneClose, PaneId: pane.PaneId);
            if (Contains(pane.Scrollbar, col, row))
                return new ChromeHit(ChromeHitKind.Scrollbar, PaneId: pane.PaneId);
            if (pane.Content.Contains(col, row))
                return new ChromeHit(ChromeHitKind.Pane, PaneId: pane.PaneId, IsFrame: false);
            if (pane.Frame.Contains(col, row))
                return new ChromeHit(ChromeHitKind.Pane, PaneId: pane.PaneId, IsFrame: true);
        }

        return null;
    }

    public static ChromeHit FromSwitcher(MobileSwitcherRow row) =>
        row.Target switch
        {
            MobileSwitcherTarget.Close => new ChromeHit(ChromeHitKind.MobileSwitcherClose),
            MobileSwitcherTarget.NewWorkspace => new ChromeHit(ChromeHitKind.SidebarNew),
            MobileSwitcherTarget.Workspace => new ChromeHit(
                ChromeHitKind.SidebarWorkspace,
                WorkspaceId: row.WorkspaceId ?? row.Id),
            MobileSwitcherTarget.NewTab => new ChromeHit(ChromeHitKind.TabNew),
            MobileSwitcherTarget.Tab => new ChromeHit(ChromeHitKind.Tab, TabId: row.TabId ?? row.Id),
            MobileSwitcherTarget.TabClose => new ChromeHit(
                ChromeHitKind.TabClose,
                TabId: row.TabId ?? row.Id),
            MobileSwitcherTarget.WorkspaceClose => new ChromeHit(
                ChromeHitKind.SidebarWorkspaceClose,
                WorkspaceId: row.WorkspaceId ?? row.Id),
            MobileSwitcherTarget.Agent => new ChromeHit(
                ChromeHitKind.SidebarAgent,
                PaneId: row.PaneId ?? row.Id),
            MobileSwitcherTarget.Cube => new ChromeHit(
                ChromeHitKind.SidebarCube,
                PlacementId: row.PlacementId ?? row.Id),
            MobileSwitcherTarget.Menu => new ChromeHit(
                ChromeHitKind.MobileSwitcherMenu,
                MenuIndex: row.MenuIndex),
            _ => new ChromeHit(ChromeHitKind.MobileSwitcherClose),
        };

    private static bool Contains(CellRect? rect, int col, int row) =>
        rect is { } r && r.Contains(col, row);
}
