using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;

namespace Hypa.Cli.Attach.Chrome;

public sealed record ChromePaneFrame(
    string PaneId,
    string Label,
    CellRect Frame,
    CellRect Content,
    CellRect? Scrollbar,
    CellRect? Close,
    PaneChromeBorders Borders = PaneChromeBorders.None,
    bool Focused = false,
    CellRect? Gutter = null,
    PaneChromeScrollState Scroll = default,
    bool HideChrome = false);

public sealed record ChromeSplitHit(
    IReadOnlyList<int> Path,
    CellRect Rect,
    string Direction,
    CellRect Parent = default,
    double Ratio = 0.5);

public sealed record PopupChromeFrame(CellRect Outer, CellRect Inner);

public sealed record LayoutChromeGeometry(
    int Cols,
    int Rows,
    CellRect Client,
    CellRect Content,
    ModeBarSlot TabSlot,
    bool TabBarVisible,
    CellRect? TabRow,
    CellRect? StatusRow,
    IReadOnlyList<CellRect> OuterBorder,
    IReadOnlyList<ChromePaneFrame> Panes,
    IReadOnlyList<ChromeSplitHit> SplitBorders,
    IReadOnlyList<CellRect> Gaps,
    bool Zoomed,
    string? FocusedPaneId,
    TabBarModel TabBar,
    bool SidebarOpen = false,
    int SidebarWidth = 0,
    CellRect? Sidebar = null,
    CellRect? SidebarEdge = null,
    IReadOnlyList<SidebarRowHit>? SidebarHits = null,
    IReadOnlyList<ToastHit>? Toasts = null,
    int UiMinSidebarWidth = SidebarHitModel.DefaultMinWidth,
    int UiMaxSidebarWidth = SidebarHitModel.DefaultMaxWidth,
    bool SidebarCollapsed = false,
    bool SidebarCompact = false,
    bool IsNarrow = false,
    bool WindowTooSmall = false,
    MobileHeaderModel? MobileHeader = null,
    MobileSwitcherModel? MobileSwitcher = null,
    string? SessionName = null,
    string? WorkspaceLabel = null,
    string? AgentName = null,
    string? AgentState = null,
    AttachClientMode PaintMode = AttachClientMode.Terminal,
    PopupChromeFrame? PopupFrame = null,
    ChromeSurfaces? Surfaces = null,
    SidebarFrame? SidebarFrame = null,
    CellRect? SpacesPane = null,
    CellRect? AgentsPane = null,
    CellRect? ResourcePane = null,
    int? DividerRow = null,
    int SpacesScroll = 0,
    int AgentsScroll = 0,
    float SidebarSectionSplit = SidebarTwoPaneLayoutPolicy.DefaultSplitRatio,
    CellRect? SpacesBody = null,
    CellRect? AgentsBody = null,
    CellRect? ResourceBody = null,
    CellRect? SectionDivider = null,
    IReadOnlyList<CellRect>? PaneDividers = null,
    IReadOnlyList<SidebarResourcePaneLayout>? ResourceSectionLayouts = null,
    IReadOnlyDictionary<string, int>? ResourceScrolls = null,
    bool HasEndpointError = false)
{
    public IReadOnlyList<SidebarResourcePaneLayout> ResourceSectionBodies =>
        ResourceSectionLayouts ?? [];

    public IReadOnlyList<CellRect> PaneDividerRects => PaneDividers ?? [];

    /// <summary>
    /// Same offset as <c>SidebarHitModel</c>: cubes and plugin
    /// resource panes use <see cref="ResourceScrolls"/>, not
    /// <see cref="SpacesScroll"/>.
    /// </summary>
    public int ScrollFor(SidebarPaneSlot slot, string paneId) =>
        slot switch
        {
            SidebarPaneSlot.Agents => AgentsScroll,
            SidebarPaneSlot.Spaces => SpacesScroll,
            SidebarPaneSlot.Resource or SidebarPaneSlot.Cubes =>
                ResourceScrolls is not null
                && ResourceScrolls.TryGetValue(paneId, out var value)
                    ? value
                    : 0,
            _ => 0,
        };

    public bool HasLeadingDivider(int row)
    {
        foreach (var divider in PaneDividerRects)
        {
            if (divider.Row == row)
                return true;
        }

        return false;
    }

    public ChromeSurfaces NamedSurfaces => Surfaces ?? ChromeSurfaces.Unpainted(Cols, Rows);

    public IReadOnlyList<SidebarRowHit> SidebarRows => SidebarHits ?? [];

    public IReadOnlyList<ToastHit> ToastHits => Toasts ?? [];

    /// <summary>
    // / Anchor the global menu under the spaces <c>menu</c> hit.
    /// <c>src/client/shell/sidebar.rs:368-391</c> puts that hit on the
    /// last spaces-pane row.
    /// </summary>
    public bool TryGlobalMenuAnchor(out int col, out int row)
    {
        foreach (var hit in SidebarRows)
        {
            if (hit.Kind is not SidebarStubKind.Menu)
                continue;
            col = hit.Rect.Col;
            row = hit.Rect.Row + 1;
            return true;
        }

        col = 0;
        row = 0;
        return false;
    }

    public MobileSwitcherModel? Switcher => MobileSwitcher;

    public CellRect? FocusedContent
    {
        get
        {
            if (FocusedPaneId is { } id)
            {
                foreach (var pane in Panes)
                {
                    if (pane.PaneId == id)
                        return pane.Content;
                }
            }

            return Panes.Count > 0 ? Panes[0].Content : Content;
        }
    }

    public static LayoutChromeGeometry Compute(
        int cols,
        int rows,
        LayoutNode? root,
        bool zoomed,
        string? zoomedPaneId,
        string? focusedPaneId,
        AttachUiConfig ui,
        int tabCount,
        AttachClientMode mode,
        IReadOnlyList<(string Id, string Label, bool Active)>? tabs = null,
        int overflowOffset = 0,
        TimeProvider? time = null,
        string? hostname = null,
        string? commandCache = null,
        IReadOnlyDictionary<string, string>? commandOutputs = null,
        bool sidebarOpen = false,
        int sidebarWidth = 0,
        IReadOnlyList<SidebarStubRow>? sidebarRows = null,
        IReadOnlyList<ToastHit>? toasts = null,
        bool sidebarCollapsed = false,
        bool switcherOpen = false,
        int switcherScroll = 0,
        string? workspaceLabel = null,
        string? tabLabel = null,
        int activeTabIndex = 0,
        string? agentSummary = null,
        string? focusedWorkspaceId = null,
        string? focusedTabId = null,
        string? sessionName = null,
        string? agentState = null,
        SidebarFrame? sidebarFrame = null,
        int spacesScroll = 0,
        int agentsScroll = 0,
        IReadOnlyDictionary<string, int>? resourceScrolls = null,
        float sidebarSectionSplit = SidebarTwoPaneLayoutPolicy.DefaultSplitRatio,
        bool revealFocused = false,
        int? lastTabBarWidth = null,
        IReadOnlyList<TabBarTabSpec>? tabSpecs = null,
        bool singlePaneFrame = false)
    {
        ArgumentNullException.ThrowIfNull(ui);
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var resolvedTabs = tabSpecs ?? TabBarTabSpec.From(tabs);
        var client = new CellRect(0, 0, cols, rows);
        var isNarrow = NarrowLayout.IsNarrow(cols, ui.MobileWidthThreshold);
        if (isNarrow)
        {
            return ComputeNarrow(
                cols,
                rows,
                client,
                root,
                zoomed,
                zoomedPaneId,
                focusedPaneId,
                ui,
                tabCount,
                mode,
                resolvedTabs,
                time,
                hostname,
                commandCache,
                commandOutputs,
                sidebarOpen,
                sidebarRows,
                toasts,
                sidebarCollapsed,
                switcherOpen,
                switcherScroll,
                workspaceLabel,
                tabLabel,
                activeTabIndex,
                agentSummary,
                focusedWorkspaceId,
                focusedTabId,
                sessionName,
                agentState,
                sidebarFrame);
        }
        var tabSlot = ui.TabBarPosition is TabBarPosition.Bottom
            ? ModeBarSlot.Bottom
            : ModeBarSlot.Top;
        var chromeMode = ModeBarModel.IsChromeMode(mode);
        var hideSingle = ui.HideTabBarWhenSingleTab
            && tabCount <= 1
            && mode is AttachClientMode.Terminal;
        var tabVisible = !hideSingle || chromeMode;

        CellRect? sidebar = null;
        CellRect? sidebarEdge = null;
        CellRect? spacesPane = null;
        CellRect? agentsPane = null;
        CellRect? resourcePane = null;
        CellRect? spacesBody = null;
        CellRect? agentsBody = null;
        CellRect? resourceBody = null;
        CellRect? sectionDivider = null;
        IReadOnlyList<CellRect> paneDividers = [];
        int? dividerRow = null;
        var clampedWidth = 0;
        var compact = false;
        IReadOnlyList<SidebarRowHit> sidebarHits = [];
        IReadOnlyList<SidebarResourcePaneLayout> resourceSectionBodies = [];
        var showExpanded = sidebarOpen && !sidebarCollapsed;
        var showCompact = sidebarCollapsed && ui.SidebarCollapsedMode is SidebarCollapsedMode.Compact;
        if (showExpanded || showCompact)
        {
            compact = showCompact && !showExpanded;
            clampedWidth = compact
                ? 4
                : SidebarHitModel.ClampWidth(
                    sidebarWidth > 0 ? sidebarWidth : ui.SidebarWidth,
                    ui.SidebarMinWidth,
                    ui.SidebarMaxWidth);
            if (clampedWidth >= cols)
                clampedWidth = Math.Max(1, cols - 1);
            if (clampedWidth > 0)
            {
                sidebar = new CellRect(0, 0, clampedWidth, rows);
                sidebarEdge = new CellRect(clampedWidth - 1, 0, 1, rows);
                var resourceSectionIds = VisibleResourceSectionIds(sidebarFrame);
                var resourceWanted = SidebarTwoPaneLayoutPolicy.WantedResourceRows(
                    sidebarFrame?.Panes,
                    compact);
                var layout = compact
                    ? SidebarTwoPaneLayoutPolicy.Compact(sidebar.Value, resourceSectionIds, resourceWanted)
                    : SidebarTwoPaneLayoutPolicy.Expanded(
                        sidebar.Value,
                        sidebarSectionSplit,
                        resourceSectionIds,
                        resourceWanted);
                spacesPane = layout.Spaces;
                agentsPane = layout.Agents;
                resourcePane = layout.Resource;
                spacesBody = layout.BodyRect(SidebarPaneSlot.Spaces, compact);
                agentsBody = layout.BodyRect(SidebarPaneSlot.Agents, compact);
                resourceBody = layout.ResourceSections.Count > 0
                    ? layout.ResourceSections[0].Body
                    : layout.BodyRect(SidebarPaneSlot.Resource, compact);
                dividerRow = layout.DividerRow;
                resourceSectionBodies = layout.ResourceSections;
                if (dividerRow is { } splitRow && layout.Spaces.Cols > 0)
                    sectionDivider = new CellRect(layout.Spaces.Col, splitRow, layout.Spaces.Cols, 1);
                paneDividers = LeadingDividerRects(layout);
                sidebarHits = sidebarFrame is not null
                    ? SidebarHitModel.Hits(
                        sidebar.Value,
                        layout,
                        sidebarFrame,
                        spacesScroll,
                        agentsScroll,
                        resourceScrolls)
                    : SidebarHitModel.Hits(sidebar.Value, sidebarRows);
            }
        }

        var mainCol = clampedWidth;
        var mainCols = Math.Max(1, cols - clampedWidth);
        var main = new CellRect(mainCol, 0, mainCols, rows);

        // main. There is no idle desktop status row.
        var tabRowIndex = tabSlot is ModeBarSlot.Top ? 0 : rows - 1;
        CellRect? tabRow = tabVisible ? new CellRect(mainCol, tabRowIndex, mainCols, 1) : null;
        CellRect? statusRow = null;

        var topPad = 0;
        var bottomPad = 0;
        if (tabVisible && tabSlot is ModeBarSlot.Top)
            topPad++;
        if (tabVisible && tabSlot is ModeBarSlot.Bottom)
            bottomPad++;

        var paneRows = rows - topPad - bottomPad;
        if (mainCols < 1)
            mainCols = 1;
        if (paneRows < 1)
            paneRows = 1;
        var content = new CellRect(mainCol, topPad, mainCols, paneRows);
        var emptyBar = new CellRect(mainCol, 0, mainCols, 0);
        var unpainted = new CellRect(0, 0, cols, 0);
        var surfaces = new ChromeSurfaces(
            sidebar ?? new CellRect(0, 0, 0, rows),
            main,
            tabRow ?? emptyBar,
            statusRow ?? emptyBar,
            content,
            unpainted,
            unpainted,
            unpainted);

        var focusedId = focusedPaneId ?? zoomedPaneId;
        var layoutPaneCount = LayoutTreeOperations.Leaves(root).Count;
        var panes = new List<ChromePaneFrame>();
        var splits = new List<ChromeSplitHit>();
        var gaps = new List<CellRect>();
        if (zoomed)
        {
            var id = zoomedPaneId
                ?? focusedPaneId
                ?? LayoutTreeOperations.Leaves(root).FirstOrDefault()?.PaneId?.Value
                ?? "";
            if (id.Length > 0)
            {
                var borders = PaneLineCells.ZoomedBorders(
                    layoutPaneCount,
                    ui.PaneBorders,
                    ui.PaneOuterBorders,
                    singlePaneFrame);
                panes.Add(FramePane(
                    id,
                    LabelOf(root, id),
                    content,
                    ui,
                    zoomed: true,
                    borders: borders,
                    focused: true));
            }
        }
        else if (root is not null)
            AllocatePaneChrome(root, content, ui, focusedId, panes, splits, gaps, singlePaneFrame);

        if (panes.Count == 0
            && root is not null
            && content.Cols > 0
            && content.Rows > 0
            && focusedId is { Length: > 0 })
        {
            // A non-null root that allocated no panes still frames the focused
            // terminal on the main content box.
            panes.Add(FramePane(
                focusedId,
                LabelOf(root, focusedId),
                content,
                ui,
                zoomed: false,
                focused: true));
        }

        var tabList = resolvedTabs.Count > 0 ? resolvedTabs : InferTabs(root);
        var tabBarWidth = tabVisible ? mainCols : 0;
        var reveal = revealFocused
            || (lastTabBarWidth is int previousWidth && previousWidth != tabBarWidth);
        var tabModel = tabVisible
            ? TabBarModel.Build(
                tabList,
                mainCols,
                tabRowIndex,
                tabSlot,
                overflowOffset,
                ui,
                time,
                hostname,
                commandCache,
                commandOutputs,
                originCol: mainCol,
                revealFocused: reveal)
            : TabBarModel.Hidden(tabSlot, mainCols);

        return new LayoutChromeGeometry(
            cols,
            rows,
            client,
            content,
            tabSlot,
            tabVisible,
            tabRow,
            statusRow,
            [],
            panes,
            splits,
            gaps,
            zoomed,
            focusedId,
            tabModel,
            sidebarOpen,
            clampedWidth,
            sidebar,
            sidebarEdge,
            sidebarHits,
            toasts ?? [],
            ui.SidebarMinWidth > 0 ? ui.SidebarMinWidth : SidebarHitModel.DefaultMinWidth,
            ui.SidebarMaxWidth > 0 ? ui.SidebarMaxWidth : SidebarHitModel.DefaultMaxWidth,
            sidebarCollapsed,
            compact,
            SessionName: sessionName,
            WorkspaceLabel: workspaceLabel,
            AgentName: OccupantName(agentSummary),
            AgentState: agentState,
            PaintMode: mode,
            Surfaces: surfaces,
            SidebarFrame: sidebarFrame,
            SpacesPane: spacesPane,
            AgentsPane: agentsPane,
            ResourcePane: resourcePane,
            DividerRow: dividerRow,
            SpacesScroll: spacesScroll,
            AgentsScroll: agentsScroll,
            SidebarSectionSplit: SidebarTwoPaneLayoutPolicy.ClampSplitRatio(sidebarSectionSplit),
            SpacesBody: spacesBody,
            AgentsBody: agentsBody,
            ResourceBody: resourceBody,
            SectionDivider: sectionDivider,
            PaneDividers: paneDividers,
            ResourceSectionLayouts: resourceSectionBodies,
            ResourceScrolls: resourceScrolls,
            HasEndpointError: false);
    }

    internal static IReadOnlyList<string> VisibleResourceSectionIds(SidebarFrame? frame)
    {
        if (frame is null)
            return [];
        var ids = new List<string>();
        foreach (var pane in frame.Panes)
        {
            if (!pane.Visible)
                continue;
            if (pane.Slot is SidebarPaneSlot.Resource or SidebarPaneSlot.Cubes)
                ids.Add(pane.Id);
        }

        return ids;
    }

    private static IReadOnlyList<CellRect> LeadingDividerRects(SidebarTwoPaneLayout layout)
    {
        var dividers = new List<CellRect>();
        foreach (var pane in layout.Stack)
        {
            if (!pane.LeadingDivider || pane.Pane.Cols <= 0)
                continue;
            dividers.Add(new CellRect(pane.Pane.Col, pane.Pane.Row, pane.Pane.Cols, 1));
        }

        return dividers;
    }

    public static LayoutChromeGeometry Compute(
        int cols,
        int rows,
        LayoutNodeDto? root,
        bool zoomed,
        string? zoomedPaneId,
        string? focusedPaneId,
        AttachUiConfig ui,
        int tabCount,
        AttachClientMode mode,
        IReadOnlyList<(string Id, string Label, bool Active)>? tabs = null,
        int overflowOffset = 0,
        TimeProvider? time = null,
        string? hostname = null,
        string? commandCache = null,
        IReadOnlyDictionary<string, string>? commandOutputs = null,
        bool sidebarOpen = false,
        int sidebarWidth = 0,
        IReadOnlyList<SidebarStubRow>? sidebarRows = null,
        IReadOnlyList<ToastHit>? toasts = null,
        bool sidebarCollapsed = false,
        bool switcherOpen = false,
        int switcherScroll = 0,
        string? workspaceLabel = null,
        string? tabLabel = null,
        int activeTabIndex = 0,
        string? agentSummary = null,
        string? focusedWorkspaceId = null,
        string? focusedTabId = null,
        string? sessionName = null,
        string? agentState = null,
        SidebarFrame? sidebarFrame = null,
        int spacesScroll = 0,
        int agentsScroll = 0,
        IReadOnlyDictionary<string, int>? resourceScrolls = null,
        float sidebarSectionSplit = SidebarTwoPaneLayoutPolicy.DefaultSplitRatio,
        bool revealFocused = false,
        int? lastTabBarWidth = null,
        IReadOnlyList<TabBarTabSpec>? tabSpecs = null,
        bool singlePaneFrame = false) =>
        Compute(
            cols,
            rows,
            LayoutTreeOperations.FromDto(root, keepPaneId: true),
            zoomed,
            zoomedPaneId,
            focusedPaneId,
            ui,
            tabCount,
            mode,
            tabs,
            overflowOffset,
            time,
            hostname,
            commandCache,
            commandOutputs,
            sidebarOpen,
            sidebarWidth,
            sidebarRows,
            toasts,
            sidebarCollapsed,
            switcherOpen,
            switcherScroll,
            workspaceLabel,
            tabLabel,
            activeTabIndex,
            agentSummary,
            focusedWorkspaceId,
            focusedTabId,
            sessionName,
            agentState,
            sidebarFrame,
            spacesScroll,
            agentsScroll,
            resourceScrolls,
            sidebarSectionSplit,
            revealFocused,
            lastTabBarWidth,
            tabSpecs,
            singlePaneFrame);

    private static LayoutChromeGeometry ComputeNarrow(
        int cols,
        int rows,
        CellRect client,
        LayoutNode? root,
        bool zoomed,
        string? zoomedPaneId,
        string? focusedPaneId,
        AttachUiConfig ui,
        int tabCount,
        AttachClientMode mode,
        IReadOnlyList<TabBarTabSpec>? tabs,
        TimeProvider? time,
        string? hostname,
        string? commandCache,
        IReadOnlyDictionary<string, string>? commandOutputs,
        bool sidebarOpen,
        IReadOnlyList<SidebarStubRow>? sidebarRows,
        IReadOnlyList<ToastHit>? toasts,
        bool sidebarCollapsed,
        bool switcherOpen,
        int switcherScroll,
        string? workspaceLabel,
        string? tabLabel,
        int activeTabIndex,
        string? agentSummary,
        string? focusedWorkspaceId,
        string? focusedTabId,
        string? sessionName,
        string? agentState,
        SidebarFrame? sidebarFrame = null)
    {
        _ = time;
        _ = hostname;
        _ = commandCache;
        _ = commandOutputs;
        var tabList = tabs is { Count: > 0 } ? tabs : InferTabs(root);
        var resolvedTab = ResolveTab(tabList, tabLabel, activeTabIndex);
        var resolvedWorkspace = workspaceLabel
            ?? FirstWorkspaceLabel(sidebarFrame)
            ?? FirstWorkspaceLabel(sidebarRows);
        var resolvedAgent = OccupantName(agentSummary);
        var paneId = focusedPaneId ?? zoomedPaneId;
        var paneLabel = string.IsNullOrWhiteSpace(paneId) ? null : LabelOf(root, paneId);
        var header = MobileHeaderModel.Build(
            cols,
            rows,
            resolvedWorkspace,
            resolvedTab.Label,
            resolvedTab.Index,
            tabList.Count > 0 ? tabList.Count : Math.Max(1, tabCount),
            resolvedAgent,
            sessionName,
            paneLabel,
            paneId,
            agentState,
            ui.StatusIndicators);
        var tooSmall = NarrowLayout.IsTooSmall(cols, rows);
        var hasBanner = toasts is { Count: > 0 };
        MobileSwitcherModel? switcher = null;
        if (switcherOpen && !tooSmall)
        {
            switcher = MobileSwitcherModel.Build(
                cols,
                rows,
                sidebarRows,
                TabBarTabSpec.ToTuples(tabList),
                switcherScroll,
                focusedWorkspaceId,
                focusedTabId ?? resolvedTab.Id,
                focusedPaneId,
                reservedBottom: hasBanner ? 1 : 0,
                sidebarFrame);
        }

        if (tooSmall)
        {
            return new LayoutChromeGeometry(
                cols,
                rows,
                client,
                client,
                ModeBarSlot.Top,
                false,
                null,
                null,
                [],
                [],
                [],
                [],
                zoomed,
                focusedPaneId ?? zoomedPaneId,
                TabBarModel.Hidden(ModeBarSlot.Top, cols),
                sidebarOpen,
                0,
                null,
                null,
                [],
                [],
                ui.SidebarMinWidth > 0 ? ui.SidebarMinWidth : SidebarHitModel.DefaultMinWidth,
                ui.SidebarMaxWidth > 0 ? ui.SidebarMaxWidth : SidebarHitModel.DefaultMaxWidth,
                sidebarCollapsed,
                false,
                true,
                true,
                header,
                switcher,
                sessionName,
                resolvedWorkspace,
                resolvedAgent,
                agentState,
                mode,
                Surfaces: ChromeSurfaces.Unpainted(cols, rows));
        }

        var headerRows = header.Rect.Rows;
        var modeBand = ChromeModeBandRows(rows, headerRows, mode);
        CellRect? tabRow = modeBand == 1
            ? new CellRect(0, headerRows, cols, 1)
            : null;
        var content = new CellRect(
            0,
            headerRows + modeBand,
            cols,
            Math.Max(0, rows - headerRows - modeBand));
        var panes = new List<ChromePaneFrame>();
        if (content.Cols >= 1 && content.Rows >= 1)
        {
            var id = zoomedPaneId
                ?? focusedPaneId
                ?? LayoutTreeOperations.Leaves(root).FirstOrDefault()?.PaneId?.Value
                ?? "";
            if (id.Length == 0 && tabList.Count > 0)
                id = tabList[0].Id;
            if (id.Length > 0)
            {
                panes.Add(FramePane(
                    id,
                    LabelOf(root, id),
                    content,
                    ui,
                    zoomed: true,
                    hideChrome: content.Cols < NarrowLayout.MinContentCols + 1));
            }
        }

        var bannerToasts = PlaceNarrowToasts(toasts, content, client, switcher);
        return new LayoutChromeGeometry(
            cols,
            rows,
            client,
            content,
            ModeBarSlot.Top,
            false,
            tabRow,
            null,
            [],
            panes,
            [],
            [],
            zoomed,
            focusedPaneId ?? zoomedPaneId,
            TabBarModel.Hidden(ModeBarSlot.Top, cols),
            sidebarOpen,
            0,
            null,
            null,
            [],
            bannerToasts,
            ui.SidebarMinWidth > 0 ? ui.SidebarMinWidth : SidebarHitModel.DefaultMinWidth,
            ui.SidebarMaxWidth > 0 ? ui.SidebarMaxWidth : SidebarHitModel.DefaultMaxWidth,
            sidebarCollapsed,
            false,
            true,
            false,
            header,
            switcher,
            sessionName,
            resolvedWorkspace,
            resolvedAgent,
            agentState,
            mode,
            Surfaces: new ChromeSurfaces(
                new CellRect(0, 0, 0, rows),
                client,
                tabRow ?? new CellRect(0, 0, cols, 0),
                new CellRect(0, 0, cols, 0),
                content,
                new CellRect(0, 0, cols, 0),
                new CellRect(0, 0, cols, 0),
                new CellRect(0, 0, cols, 0)));
    }

    private static int ChromeModeBandRows(int rows, int headerRows, AttachClientMode mode)
    {
        if (!ModeBarModel.IsChromeMode(mode))
            return 0;
        return rows > headerRows ? 1 : 0;
    }

    private static IReadOnlyList<ToastHit> PlaceNarrowToasts(
        IReadOnlyList<ToastHit>? toasts,
        CellRect content,
        CellRect client,
        MobileSwitcherModel? switcher)
    {
        if (toasts is null || toasts.Count == 0)
            return [];

        var last = toasts[^1];
        CellRect banner;
        if (switcher is { Open: true } && client.Cols >= 1 && client.Rows >= 1)
        {
            var row = switcher.Viewport.EndRow;
            if (row < 0 || row >= client.EndRow || switcher.Viewport.Rows < 1)
                return [];
            banner = new CellRect(client.Col, row, client.Cols, 1);
        }
        else if (content.Cols >= 1 && content.Rows >= 1)
            banner = ToastHit.PlaceBanner(content);
        else
            return [];

        return [last with { Rect = banner }];
    }

    private static (string Id, string Label, int Index) ResolveTab(
        IReadOnlyList<TabBarTabSpec> tabs,
        string? tabLabel,
        int activeTabIndex)
    {
        if (tabs.Count == 0)
            return ("", tabLabel ?? "1", Math.Max(0, activeTabIndex));

        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Active)
                return (tabs[i].Id, string.IsNullOrWhiteSpace(tabLabel) ? tabs[i].Label : tabLabel, i);
        }

        var index = Math.Clamp(activeTabIndex, 0, tabs.Count - 1);
        var tab = tabs[index];
        return (tab.Id, string.IsNullOrWhiteSpace(tabLabel) ? tab.Label : tabLabel, index);
    }

    private static string? FirstWorkspaceLabel(SidebarFrame? frame)
    {
        var spaces = frame?.Pane(SidebarPaneSlot.Spaces);
        if (spaces is null)
            return null;
        foreach (var row in spaces.Rows)
        {
            if (row.Kind is SidebarRowKind.Workspace
                && row.CardRowIndex == 0
                && !string.IsNullOrWhiteSpace(row.Label))
            {
                return row.Label;
            }
        }

        return null;
    }

    private static string? FirstWorkspaceLabel(IReadOnlyList<SidebarStubRow>? rows)
    {
        if (rows is null)
            return null;
        foreach (var row in rows)
        {
            if (row.Kind is SidebarStubKind.Workspace && !string.IsNullOrWhiteSpace(row.Label))
                return row.Label;
        }

        foreach (var row in rows)
        {
            if (row.Kind is SidebarStubKind.Workspace)
                return row.Label;
        }

        return null;
    }

    private static string? OccupantName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    public static AttachUiConfig BareInsets { get; } = new()
    {
        PaneBorders = false,
        PaneOuterBorders = false,
        PaneGaps = false,
        PaneScrollbars = false,
        HideTabBarWhenSingleTab = false,
        TabBarPosition = TabBarPosition.Top,
        TabBarRight = [],
        MobileWidthThreshold = 0,
    };

    private static void AllocatePaneChrome(
        LayoutNode root,
        CellRect content,
        AttachUiConfig ui,
        string? focusedId,
        List<ChromePaneFrame> panes,
        List<ChromeSplitHit> splits,
        List<CellRect> gaps,
        bool singlePaneFrame)
    {
        var allocated = LayoutRectAllocator.Allocate(root, content.Cols, content.Rows);
        var specs = new List<PaneChromeSpec>(allocated.Panes.Count);
        foreach (var pane in allocated.Panes)
        {
            specs.Add(new PaneChromeSpec(
                pane.PaneId,
                LabelOf(root, pane.PaneId),
                Translate(pane.Rect, content.Col, content.Row),
                PaneChromeBorders.None,
                pane.PaneId == focusedId));
        }

        var applied = PaneLineCells.Apply(
            specs,
            ui.PaneBorders,
            ui.PaneGaps,
            ui.PaneOuterBorders,
            singlePaneFrame);
        if (ui.PaneGaps && !ui.PaneBorders)
            gaps.AddRange(PaneLineCells.GapsAfterShrink(specs, applied));

        foreach (var spec in applied)
        {
            panes.Add(FramePane(
                spec.PaneId,
                spec.Label,
                spec.Rect,
                ui,
                zoomed: false,
                borders: spec.Borders,
                focused: spec.Focused));
        }

        foreach (var border in allocated.Borders)
        {
            splits.Add(new ChromeSplitHit(
                border.Path,
                Translate(border.Rect, content.Col, content.Row),
                border.Direction,
                Translate(
                    border.Parent.Cols > 0 || border.Parent.Rows > 0 ? border.Parent : content,
                    content.Col,
                    content.Row),
                border.Ratio));
        }
    }

    private static CellRect Translate(CellRect rect, int col, int row) =>
        new(rect.Col + col, rect.Row + row, rect.Cols, rect.Rows);

    private static ChromePaneFrame FramePane(
        string id,
        string label,
        CellRect frame,
        AttachUiConfig ui,
        bool zoomed,
        bool hideChrome = false,
        PaneChromeBorders borders = PaneChromeBorders.None,
        bool focused = false)
    {
        _ = zoomed;
        var inner = PaneLineCells.InnerRect(frame, borders);
        return PaneChromeGutter.Frame(
            id,
            label,
            frame,
            inner,
            ui,
            hideChrome,
            borders,
            focused,
            PaneChromeScrollState.Unknown);
    }

    private static string LabelOf(LayoutNode? root, string paneId)
    {
        foreach (var leaf in LayoutTreeOperations.Leaves(root))
        {
            if (leaf.PaneId?.Value == paneId)
                return leaf.Label;
        }

        return "";
    }

    private static IReadOnlyList<TabBarTabSpec> InferTabs(
        LayoutNode? root)
    {
        var leaves = LayoutTreeOperations.Leaves(root);
        if (leaves.Count == 0)
            return [];
        var first = leaves[0];
        var id = first.PaneId?.Value ?? "tab";
        return [new TabBarTabSpec(id, string.IsNullOrEmpty(first.Label) ? "1" : first.Label, true)];
    }
}
