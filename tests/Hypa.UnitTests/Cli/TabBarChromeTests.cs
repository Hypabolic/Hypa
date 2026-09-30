using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Status;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class TabBarChromeTests
{
    [Fact]
    public void Active_and_inactive_tabs_use_herdr_span_styles()
    {
        var theme = ThemePalette.Catppuccin;
        var (geo, host) = PaintDesktop(
            [("t1", "main", true), ("t2", "docs", false)],
            sessionName: "default");

        Assert.Equal(2, geo.TabBar.Tabs.Count);
        Assert.DoesNotContain("1:main", geo.TabBar.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('*', geo.TabBar.Text);
        Assert.DoesNotContain(" x", geo.TabBar.Text, StringComparison.Ordinal);

        var active = geo.TabBar.Tabs[0];
        var inactive = geo.TabBar.Tabs[1];
        Assert.Contains("main", active.Display, StringComparison.Ordinal);
        Assert.Contains("docs", inactive.Display, StringComparison.Ordinal);
        Assert.True(active.Rect.Cols >= TabBarModel.MinTabWidth);
        Assert.True(inactive.Rect.Cols >= TabBarModel.MinTabWidth);
        Assert.True(inactive.Rect.Col > active.Rect.EndCol);

        var activeCell = GlyphCell(host, active.Rect, "m");
        Assert.Equal(Encode(theme.Accent), activeCell.Style.Bg);
        Assert.Equal(Encode(TabBarPainter.ContrastForeground(theme)), activeCell.Style.Fg);
        Assert.False(activeCell.Style.Dim);

        var inactiveCell = GlyphCell(host, inactive.Rect, "d");
        Assert.Equal(Encode(theme.Surface0), inactiveCell.Style.Bg);
        Assert.Equal(Encode(theme.Overlay0), inactiveCell.Style.Fg);
        Assert.True(inactiveCell.Style.Dim);

        Assert.NotNull(geo.TabBar.NewTab);
        var plus = geo.TabBar.NewTab!.Value;
        Assert.Equal(3, plus.Cols);
        var plusCell = GlyphCell(host, plus, "+");
        Assert.Equal(Encode(theme.Overlay1), plusCell.Style.Fg);
        Assert.Equal(Encode(theme.PanelBg), plusCell.Style.Bg);

        Assert.Equal(ChromeHitKind.Tab, ChromeHitTest.Hit(geo, active.Rect.Col + 2, active.Rect.Row)!.Kind);
        Assert.Equal("t1", ChromeHitTest.Hit(geo, active.Rect.Col + 2, active.Rect.Row)!.TabId);
        Assert.Equal(ChromeHitKind.Tab, ChromeHitTest.Hit(geo, inactive.Rect.Col + 2, inactive.Rect.Row)!.Kind);
        Assert.Equal(ChromeHitKind.TabNew, ChromeHitTest.Hit(geo, plus.Col + 1, plus.Row)!.Kind);
        Assert.Null(active.Close);
        Assert.Null(ChromeHitTest.Hit(geo, active.Rect.EndCol, active.Rect.Row));
    }

    [Fact]
    public void Desktop_idle_reclaims_the_bottom_content_row()
    {
        var ui = DesktopUi();
        var geo = Layout(80, 24, [("t1", "main", true)], ui, sessionName: "default");
        Assert.Null(geo.StatusRow);
        Assert.Equal(0, geo.NamedSurfaces.StatusRow.Rows);
        Assert.Equal(0, geo.TabRow!.Value.Row);
        Assert.Equal(1, geo.Content.Row);
        Assert.Equal(23, geo.Content.Rows);
        Assert.Equal(24, geo.Content.EndRow);
        Assert.Equal(geo.Content, geo.NamedSurfaces.PaneArea);

        var host = Stamp(geo, ui, sessionName: "default", agentState: "unknown");
        var footer = RowText(host, 23);
        Assert.DoesNotContain("unknown", footer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session", footer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("default", footer, StringComparison.Ordinal);
        Assert.DoesNotContain("shell", footer, StringComparison.OrdinalIgnoreCase);

        var chips = LayoutChromePainter.StatusRowText(
            geo,
            ui,
            80,
            statusText: null,
            chips: new StatusChipComposeInput
            {
                SessionName = "default",
                AgentState = "unknown",
                MaxCols = 80,
            });
        Assert.Contains("default", chips, StringComparison.Ordinal);
    }

    [Fact]
    public void Tab_bar_bottom_keeps_pane_rows_and_hides_status()
    {
        var ui = DesktopUi() with { TabBarPosition = TabBarPosition.Bottom };
        var geo = Layout(80, 24, [("t1", "main", true), ("t2", "docs", false)], ui);
        Assert.True(geo.TabBarVisible);
        Assert.Equal(23, geo.TabRow!.Value.Row);
        Assert.Equal(0, geo.Content.Row);
        Assert.Equal(23, geo.Content.Rows);
        Assert.Null(geo.StatusRow);
        Assert.Equal(ModeBarSlot.Bottom, geo.TabSlot);

        var host = Stamp(geo, ui);
        Assert.Contains("main", RowText(host, 23), StringComparison.Ordinal);
        Assert.Equal(Encode(ThemePalette.Catppuccin.Accent), GlyphCell(host, geo.TabBar.Tabs[0].Rect, "m").Style.Bg);
    }

    [Fact]
    public void Hide_single_tab_uses_the_full_main_area()
    {
        var ui = DesktopUi() with { HideTabBarWhenSingleTab = true };
        var geo = Layout(80, 24, [("t1", "main", true)], ui, tabCount: 1);
        Assert.False(geo.TabBarVisible);
        Assert.Null(geo.TabRow);
        Assert.Null(geo.StatusRow);
        Assert.Equal(0, geo.Content.Row);
        Assert.Equal(24, geo.Content.Rows);
    }

    [Fact]
    public void Hide_single_keeps_the_tab_row_for_prefix_mode()
    {
        var ui = DesktopUi() with { HideTabBarWhenSingleTab = true };
        var geo = Layout(
            80,
            24,
            [("t1", "main", true)],
            ui,
            tabCount: 1,
            mode: AttachClientMode.Prefix);
        Assert.True(geo.TabBarVisible);
        Assert.Equal(0, geo.TabRow!.Value.Row);
        Assert.Equal(23, geo.Content.Rows);
        Assert.Null(geo.StatusRow);
    }

    [Fact]
    public void Narrow_layout_keeps_the_mobile_header_and_no_desktop_status()
    {
        var ui = DesktopUi() with { MobileWidthThreshold = 64 };
        var geo = Layout(64, 24, [("t1", "main", true)], ui);
        Assert.True(geo.IsNarrow);
        Assert.False(geo.TabBarVisible);
        Assert.Null(geo.StatusRow);
        Assert.NotNull(geo.MobileHeader);
        Assert.True(geo.Content.Row >= geo.MobileHeader!.Rect.EndRow);
        Assert.Equal(64, geo.Content.Cols);
    }

    [Fact]
    public void Overflow_hits_match_scroll_and_plus_bounds()
    {
        var tabs = Enumerable.Range(1, 8)
            .Select(i => ("t" + i, "long-label-" + i, i == 3))
            .ToList();
        var ui = DesktopUi();
        var geo = Layout(40, 24, tabs, ui, overflowOffset: 2);
        Assert.True(geo.TabBar.HiddenBefore);
        Assert.True(geo.TabBar.HiddenAfter);
        Assert.Equal(2, geo.TabBar.OverflowOffset);
        Assert.NotNull(geo.TabBar.OverflowPrev);
        Assert.NotNull(geo.TabBar.OverflowNext);
        Assert.NotNull(geo.TabBar.NewTab);

        var prev = geo.TabBar.OverflowPrev!.Value;
        var next = geo.TabBar.OverflowNext!.Value;
        var plus = geo.TabBar.NewTab!.Value;
        Assert.Equal(3, prev.Cols);
        Assert.Equal(3, next.Cols);
        Assert.Equal(3, plus.Cols);
        Assert.Equal(0, prev.Col);
        Assert.True(next.Col > prev.EndCol);
        Assert.Equal(next.EndCol, plus.Col);
        Assert.True(plus.EndCol <= 40);

        foreach (var tab in geo.TabBar.Tabs)
        {
            Assert.True(tab.Rect.Col >= prev.EndCol);
            Assert.True(tab.Rect.EndCol <= next.Col);
            Assert.False(tab.Rect.Intersects(prev));
            Assert.False(tab.Rect.Intersects(next));
            Assert.False(tab.Rect.Intersects(plus));
        }

        Assert.Equal(ChromeHitKind.TabOverflowPrev, ChromeHitTest.Hit(geo, prev.Col + 1, prev.Row)!.Kind);
        Assert.Equal(ChromeHitKind.TabOverflowNext, ChromeHitTest.Hit(geo, next.Col + 1, next.Row)!.Kind);
        Assert.Equal(ChromeHitKind.TabNew, ChromeHitTest.Hit(geo, plus.Col + 1, plus.Row)!.Kind);
        Assert.Equal("t3", ChromeHitTest.Hit(geo, geo.TabBar.Tabs[0].Rect.Col + 2, 0)!.TabId);

        var host = Stamp(geo, ui);
        Assert.Contains('…', RowText(host, 0));
        Assert.Equal(Encode(ThemePalette.Catppuccin.Accent), GlyphCell(host, geo.TabBar.Tabs[0].Rect, "l").Style.Bg);
    }

    [Fact]
    public void Sidebar_origin_does_not_move_tabs_onto_column_zero()
    {
        var ui = DesktopUi();
        var geo = LayoutChromeGeometry.Compute(
            80,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "main" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            ui,
            tabCount: 2,
            AttachClientMode.Terminal,
            [("t1", "main", true), ("t2", "docs", false)],
            sidebarOpen: true,
            sidebarWidth: 26);
        Assert.Equal(26, geo.NamedSurfaces.Main.Col);
        Assert.Equal(26, geo.TabRow!.Value.Col);
        Assert.True(geo.TabBar.Tabs[0].Rect.Col >= 26);
        Assert.True(geo.TabBar.NewTab!.Value.Col >= 26);
        Assert.Equal(26, geo.Content.Col);
        Assert.Equal(23, geo.Content.Rows);
    }

    [Fact]
    public void Right_status_yields_when_the_tab_strip_is_narrow()
    {
        var ui = DesktopUi() with
        {
            TabBarRight = [AttachTabBarRightEntry.Zoom, AttachTabBarRightEntry.ForText("host")],
            TabBarRightSeparator = " · ",
        };
        var tabs = Enumerable.Range(1, 6)
            .Select(i => ("t" + i, i.ToString(), i == 1))
            .ToList();
        var geo = Layout(20, 24, tabs, ui);
        Assert.Empty(geo.TabBar.RightItems);
        Assert.Null(geo.TabBar.Zoom);
        Assert.NotNull(geo.TabBar.NewTab);
    }

    [Fact]
    public void Reveal_focus_brings_the_active_tab_into_the_overflow_window()
    {
        var tabs = OverflowTabs(focused: 8);
        var hidden = Layout(40, 24, AutoTuples(tabs), DesktopUi(), overflowOffset: 0);
        Assert.DoesNotContain(hidden.TabBar.Tabs, tab => tab.Active);
        Assert.Equal(0, hidden.TabBar.OverflowOffset);

        var revealed = LayoutSpecs(40, 24, tabs, overflowOffset: 0, revealFocused: true);
        Assert.Contains(revealed.TabBar.Tabs, tab => tab.Active && tab.TabId == "t8");
        Assert.True(revealed.TabBar.OverflowOffset > 0);
        Assert.Equal(
            "t8",
            ChromeHitTest.Hit(
                revealed,
                revealed.TabBar.Tabs.First(tab => tab.Active).Rect.Col + 2,
                0)!.TabId);
    }

    [Fact]
    public void Manual_overflow_scroll_is_not_reset_until_focus_or_width_changes()
    {
        var tabs = OverflowTabs(focused: 1);
        var start = LayoutSpecs(40, 24, tabs, overflowOffset: 0, revealFocused: false);
        Assert.Equal(0, start.TabBar.OverflowOffset);
        Assert.True(start.TabBar.MaxOverflowOffset > 0);
        Assert.Contains(start.TabBar.Tabs, tab => tab.TabId == "t1" && tab.Active);

        var plan = ChromeHitApply.Apply(
            new ChromeHit(ChromeHitKind.TabOverflowNext),
            start.TabBar.OverflowOffset,
            start.TabBar.MaxOverflowOffset);
        Assert.Equal(1, plan.TabOverflowOffset);

        var scrolled = LayoutSpecs(40, 24, tabs, overflowOffset: plan.TabOverflowOffset, revealFocused: false);
        Assert.Equal(1, scrolled.TabBar.OverflowOffset);
        Assert.True(scrolled.TabBar.HiddenBefore);

        var extra = ChromeHitApply.Apply(
            new ChromeHit(ChromeHitKind.TabOverflowNext),
            scrolled.TabBar.MaxOverflowOffset,
            scrolled.TabBar.MaxOverflowOffset);
        Assert.Equal(scrolled.TabBar.MaxOverflowOffset, extra.TabOverflowOffset);
        Assert.True(extra.TabOverflowOffset < tabs.Count - 1);

        var stillManual = LayoutSpecs(
            40,
            24,
            tabs,
            overflowOffset: extra.TabOverflowOffset,
            revealFocused: false);
        Assert.Equal(extra.TabOverflowOffset, stillManual.TabBar.OverflowOffset);

        var resized = LayoutSpecs(
            80,
            24,
            tabs,
            overflowOffset: stillManual.TabBar.OverflowOffset,
            revealFocused: false,
            lastTabBarWidth: 40);
        Assert.Contains(resized.TabBar.Tabs, tab => tab.TabId == "t1" && tab.Active);
    }

    [Fact]
    public void Live_focus_change_recenters_an_overflowing_strip()
    {
        var ui = DesktopUi();
        var port = MouseTestGeom.ApplyPort();
        var seed = LayoutSpecs(40, 24, OverflowTabs(focused: 1));
        var live = MouseTestGeom.ApplyLive(port, seed);
        live.Ui = ui;
        live.ChromeEnabled = true;
        live.RevealFocusedTab = false;
        live.TabOverflowOffset = 0;
        live.LastTabBarWidth = 40;
        live.TabHits = OverflowTabs(focused: 1);

        var first = AttachSession.ComputeLiveChrome(
            live,
            40,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "main" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1");
        Assert.Equal(0, first.TabBar.OverflowOffset);
        Assert.False(live.RevealFocusedTab);

        AttachSession.ReplaceTabHits(live, OverflowTabs(focused: 8));
        Assert.True(live.RevealFocusedTab);
        var focused = AttachSession.ComputeLiveChrome(
            live,
            40,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "main" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1");
        Assert.Contains(focused.TabBar.Tabs, tab => tab.TabId == "t8" && tab.Active);
        Assert.False(live.RevealFocusedTab);
        Assert.Equal(focused.TabBar.OverflowOffset, live.TabOverflowOffset);
    }

    [Fact]
    public void Live_label_change_recenters_an_overflowing_strip()
    {
        var ui = DesktopUi();
        var port = MouseTestGeom.ApplyPort();
        var seedTabs = OverflowTabs(focused: 1);
        var seed = LayoutSpecs(40, 24, seedTabs);
        var live = MouseTestGeom.ApplyLive(port, seed);
        live.Ui = ui;
        live.ChromeEnabled = true;
        live.RevealFocusedTab = false;
        live.TabOverflowOffset = 0;
        live.LastTabBarWidth = 40;
        live.TabHits = seedTabs;

        AttachSession.ComputeLiveChrome(
            live,
            40,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "main" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1");
        Assert.False(live.RevealFocusedTab);

        var renamed = seedTabs
            .Select(tab => tab.Active
                ? tab with { Label = "very-long-renamed-tab-that-overflows" }
                : tab)
            .ToList();
        AttachSession.ReplaceTabHits(live, renamed);
        Assert.True(live.RevealFocusedTab);
        var revealed = AttachSession.ComputeLiveChrome(
            live,
            40,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "main" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1");
        var renamedHit = Assert.Single(revealed.TabBar.Tabs, tab => tab.TabId == "t1" && tab.Active);
        Assert.Contains("very-long-renamed-tab-that-over", renamedHit.Display, StringComparison.Ordinal);
        Assert.False(live.RevealFocusedTab);
    }

    [Fact]
    public void Live_zoom_change_recenters_an_overflowing_strip()
    {
        var ui = DesktopUi();
        var port = MouseTestGeom.ApplyPort();
        var seedTabs = OverflowTabs(focused: 1);
        var seed = LayoutSpecs(40, 24, seedTabs);
        var live = MouseTestGeom.ApplyLive(port, seed);
        live.Ui = ui;
        live.ChromeEnabled = true;
        live.RevealFocusedTab = false;
        live.TabOverflowOffset = 0;
        live.LastTabBarWidth = 40;
        live.TabHits = seedTabs;

        AttachSession.ComputeLiveChrome(
            live,
            40,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "main" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1");
        Assert.False(live.RevealFocusedTab);

        var zoomed = seedTabs
            .Select(tab => tab.Active ? tab with { Zoomed = true } : tab)
            .ToList();
        AttachSession.ReplaceTabHits(live, zoomed);
        Assert.True(live.RevealFocusedTab);
        var revealed = AttachSession.ComputeLiveChrome(
            live,
            40,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "main" },
            zoomed: true,
            zoomedPaneId: "p1",
            focusedPaneId: "p1");
        var zoomedHit = Assert.Single(revealed.TabBar.Tabs, tab => tab.TabId == "t1" && tab.Active);
        Assert.Contains("Z", zoomedHit.Display, StringComparison.Ordinal);
        Assert.False(live.RevealFocusedTab);
    }

    [Fact]
    public void Zoomed_tab_keeps_the_herdr_suffix()
    {
        var tabs = new TabBarTabSpec[]
        {
            new("t1", "main", true, Zoomed: true),
            new("t2", "docs", false, Zoomed: false),
        };
        var (geo, host) = PaintDesktop(tabs);
        Assert.Contains("main Z", geo.TabBar.Tabs[0].Display, StringComparison.Ordinal);
        Assert.DoesNotContain("docs Z", geo.TabBar.Tabs[1].Display, StringComparison.Ordinal);
        Assert.Contains("Z", GlyphCell(host, geo.TabBar.Tabs[0].Rect, "Z").Text, StringComparison.Ordinal);
        Assert.True(geo.TabBar.Tabs[0].Rect.Cols >= TabBarModel.MinTabWidth);
    }

    [Fact]
    public void Named_and_auto_tabs_use_distinct_herdr_styles()
    {
        var theme = ThemePalette.Catppuccin;
        var tabs = new TabBarTabSpec[]
        {
            new("t1", "build", true, CustomLabel: true),
            new("t2", "main", false, CustomLabel: false),
        };
        var (geo, host) = PaintDesktop(tabs);
        var named = GlyphCell(host, geo.TabBar.Tabs[0].Rect, "b");
        Assert.Equal(Encode(theme.Accent), named.Style.Bg);
        Assert.True(named.Style.Bold);
        Assert.False(named.Style.Dim);

        var auto = GlyphCell(host, geo.TabBar.Tabs[1].Rect, "m");
        Assert.Equal(Encode(theme.Overlay0), auto.Style.Fg);
        Assert.True(auto.Style.Dim);

        var swapped = PaintDesktop(
        [
            new TabBarTabSpec("t1", "build", false, CustomLabel: true),
            new TabBarTabSpec("t2", "main", true, CustomLabel: false),
        ]).Host;
        var inactiveNamed = GlyphCell(swapped, geo.TabBar.Tabs[0].Rect, "b");
        Assert.Equal(Encode(theme.Overlay1), inactiveNamed.Style.Fg);
        Assert.Equal(Encode(theme.Surface0), inactiveNamed.Style.Bg);
        Assert.False(inactiveNamed.Style.Dim);
        Assert.False(inactiveNamed.Style.Bold);

        var focusedAuto = GlyphCell(swapped, geo.TabBar.Tabs[1].Rect, "m");
        Assert.Equal(Encode(theme.Accent), focusedAuto.Style.Bg);
        Assert.False(focusedAuto.Style.Bold);
    }

    [Fact]
    public void Wire_hits_carry_zoom_and_custom_label_without_guessing_names()
    {
        using var doc = JsonDocument.Parse(
            """
            [
              {"tab_id":"t1","label":"main","zoomed":true,"custom_label":false},
              {"tab_id":"t2","label":"main","zoomed":false,"custom_label":true}
            ]
            """);
        var hits = AttachSession.ReadTabHits(doc.RootElement, "t1");
        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].Zoomed);
        Assert.False(hits[0].CustomLabel);
        Assert.False(hits[1].Zoomed);
        Assert.True(hits[1].CustomLabel);
        Assert.Equal("main", hits[0].Label);
        Assert.Equal("main", hits[1].Label);
        Assert.Contains(" Z", TabBarModel.TabLabel(hits[0].Label, 0, hits[0].Zoomed), StringComparison.Ordinal);
        Assert.Equal("main", TabBarModel.TabLabel(hits[1].Label, 1, hits[1].Zoomed));
    }

    [Fact]
    public void Terminal_error_overlays_the_last_pane_row_then_idle_restores_it()
    {
        var ui = DesktopUi();
        var geo = Layout(80, 24, [("t1", "main", true)], ui);
        var theme = ThemePalette.Catppuccin;
        var host = new HostFrame();
        host.Resize(80, 24);
        var sink = new HostFrameCellSink(host);

        ComposeChromeAndPane(host, sink, geo, ui, paneGlyph: "P");
        var paneRow = geo.Content.EndRow - 1;
        Assert.Equal(23, paneRow);
        Assert.Equal("P", host.CellAt(geo.Content.Col, paneRow).Text);
        Assert.DoesNotContain("ERROR", RowText(host, paneRow), StringComparison.Ordinal);
        Assert.Contains("main", RowText(host, 0), StringComparison.Ordinal);

        var overlay = ModeBarModel.OverlayRow(geo, 80, 24);
        Assert.Equal(paneRow, overlay.Row);
        Assert.Equal(geo.Content.Col, overlay.Col);
        ModeBarPainter.Stamp(
            sink,
            AttachClientMode.Terminal,
            geo.TabSlot,
            overlay.Cols,
            24,
            row: overlay.Row,
            theme: theme,
            originCol: overlay.Col,
            endpointError: "backpressure");
        Assert.Contains("ERROR", RowText(host, paneRow), StringComparison.Ordinal);
        Assert.Contains("backpressure", RowText(host, paneRow), StringComparison.Ordinal);
        Assert.Contains("main", RowText(host, 0), StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR", RowText(host, 0), StringComparison.Ordinal);
        Assert.Equal(
            Encode(theme.Accent),
            host.CellAt(overlay.Col + 1, paneRow).Style.Bg);

        ComposeChromeAndPane(host, sink, geo, ui, paneGlyph: "P");
        ModeBarPainter.Stamp(
            sink,
            AttachClientMode.Terminal,
            geo.TabSlot,
            overlay.Cols,
            24,
            row: overlay.Row,
            theme: theme,
            originCol: overlay.Col);
        Assert.Equal("P", host.CellAt(geo.Content.Col, paneRow).Text);
        Assert.DoesNotContain("ERROR", RowText(host, paneRow), StringComparison.Ordinal);
        Assert.Equal(23, geo.Content.Rows);
        Assert.Null(geo.StatusRow);
    }

    [Fact]
    public void Bottom_tabs_put_the_mode_row_on_the_tab_strip()
    {
        var ui = DesktopUi() with { TabBarPosition = TabBarPosition.Bottom };
        var geo = Layout(
            80,
            24,
            [("t1", "main", true), ("t2", "docs", false)],
            ui,
            mode: AttachClientMode.Prefix);
        Assert.Equal(23, geo.TabRow!.Value.Row);
        var overlay = ModeBarModel.OverlayRow(geo, 80, 24);
        Assert.Equal(23, overlay.Row);
        Assert.True(ModeBarModel.ReplacesTabRow(AttachClientMode.Prefix, ModeBarSlot.Bottom));
        Assert.False(ModeBarModel.ReplacesTabRow(AttachClientMode.Prefix, ModeBarSlot.Top));

        var host = new HostFrame();
        host.Resize(80, 24);
        var sink = new HostFrameCellSink(host);
        ComposeChromeAndPane(host, sink, geo, ui, paneGlyph: "P");
        ModeBarPainter.Stamp(
            sink,
            AttachClientMode.Prefix,
            geo.TabSlot,
            overlay.Cols,
            24,
            row: overlay.Row,
            originCol: overlay.Col);
        Assert.Contains("PREFIX", RowText(host, 23), StringComparison.Ordinal);
        Assert.Equal("P", host.CellAt(geo.Content.Col, geo.Content.EndRow - 1).Text);

        var errorGeo = geo with { HasEndpointError = true, PaintMode = AttachClientMode.Terminal };
        Assert.Null(ChromeHitTest.Hit(errorGeo, errorGeo.TabBar.Tabs[0].Rect.Col + 2, 23));
    }

    [Fact]
    public void Top_chrome_mode_keeps_tab_hits_and_uses_the_last_pane_row()
    {
        var ui = DesktopUi();
        var geo = Layout(
            80,
            24,
            [("t1", "main", true), ("t2", "docs", false)],
            ui,
            mode: AttachClientMode.Prefix);
        var overlay = ModeBarModel.OverlayRow(geo, 80, 24);
        Assert.Equal(geo.Content.EndRow - 1, overlay.Row);
        Assert.Equal(0, geo.TabRow!.Value.Row);
        Assert.False(ModeBarModel.ReplacesTabRow(AttachClientMode.Prefix, ModeBarSlot.Top));
        Assert.Equal(
            ChromeHitKind.Tab,
            ChromeHitTest.Hit(geo, geo.TabBar.Tabs[0].Rect.Col + 2, 0, paintMode: AttachClientMode.Prefix)!.Kind);

        var host = new HostFrame();
        host.Resize(80, 24);
        var sink = new HostFrameCellSink(host);
        ComposeChromeAndPane(host, sink, geo, ui, paneGlyph: "P");
        ModeBarPainter.Stamp(
            sink,
            AttachClientMode.Prefix,
            geo.TabSlot,
            overlay.Cols,
            24,
            row: overlay.Row,
            originCol: overlay.Col);
        Assert.Contains("PREFIX", RowText(host, overlay.Row), StringComparison.Ordinal);
        Assert.Contains("main", RowText(host, 0), StringComparison.Ordinal);
        Assert.DoesNotContain("PREFIX", RowText(host, 0), StringComparison.Ordinal);
    }

    private static (LayoutChromeGeometry Geo, HostFrame Host) PaintDesktop(
        IReadOnlyList<TabBarTabSpec> tabs,
        string? sessionName = null)
    {
        var ui = DesktopUi();
        var geo = LayoutSpecs(80, 24, tabs, sessionName: sessionName);
        return (geo, Stamp(geo, ui, sessionName));
    }

    private static LayoutChromeGeometry LayoutSpecs(
        int cols,
        int rows,
        IReadOnlyList<TabBarTabSpec> tabs,
        int overflowOffset = 0,
        AttachClientMode mode = AttachClientMode.Terminal,
        string? sessionName = null,
        bool revealFocused = false,
        int? lastTabBarWidth = null,
        AttachUiConfig? ui = null) =>
        LayoutChromeGeometry.Compute(
            cols,
            rows,
            new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = tabs[0].Label },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            ui ?? DesktopUi(),
            tabs.Count,
            mode,
            overflowOffset: overflowOffset,
            sessionName: sessionName,
            revealFocused: revealFocused,
            lastTabBarWidth: lastTabBarWidth,
            tabSpecs: tabs);

    private static IReadOnlyList<TabBarTabSpec> OverflowTabs(int focused) =>
        Enumerable.Range(1, 8)
            .Select(i => new TabBarTabSpec("t" + i, "long-label-" + i, i == focused))
            .ToList();

    private static IReadOnlyList<(string Id, string Label, bool Active)> AutoTuples(
        IReadOnlyList<TabBarTabSpec> tabs) =>
        TabBarTabSpec.ToTuples(tabs);

    private static void ComposeChromeAndPane(
        HostFrame host,
        HostFrameCellSink sink,
        LayoutChromeGeometry geo,
        AttachUiConfig ui,
        string paneGlyph)
    {
        LayoutChromePainter.Stamp(sink, geo, ui, theme: ThemePalette.Catppuccin);
        var style = AssembledStyle.Default;
        for (var row = geo.Content.Row; row < geo.Content.EndRow; row++)
        {
            for (var col = geo.Content.Col; col < geo.Content.EndCol; col++)
                host.StampGlyphRun(col, row, paneGlyph, style, 1);
        }
    }

    private static LayoutChromeGeometry Layout(
        int cols,
        int rows,
        IReadOnlyList<(string Id, string Label, bool Active)> tabs,
        AttachUiConfig ui,
        int? tabCount = null,
        int overflowOffset = 0,
        AttachClientMode mode = AttachClientMode.Terminal,
        string? sessionName = null) =>
        LayoutChromeGeometry.Compute(
            cols,
            rows,
            new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = tabs[0].Label },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            ui,
            tabCount ?? tabs.Count,
            mode,
            tabs,
            overflowOffset,
            sessionName: sessionName);

    private static HostFrame Stamp(
        LayoutChromeGeometry geo,
        AttachUiConfig ui,
        string? sessionName = null,
        string? agentState = null)
    {
        var host = new HostFrame();
        host.Resize(geo.Cols, geo.Rows);
        LayoutChromePainter.Stamp(
            new HostFrameCellSink(host),
            geo,
            ui,
            chips: new StatusChipComposeInput
            {
                SessionName = sessionName,
                AgentState = agentState,
                MaxCols = geo.Cols,
            },
            theme: ThemePalette.Catppuccin);
        return host;
    }

    private static AttachUiConfig DesktopUi() => new()
    {
        PaneBorders = false,
        PaneOuterBorders = false,
        PaneGaps = false,
        PaneScrollbars = false,
        HideTabBarWhenSingleTab = false,
        TabBarPosition = TabBarPosition.Top,
        TabBarRight = [],
        MobileWidthThreshold = 0,
        MouseCapture = true,
    };

    private static string RowText(HostFrame host, int row)
    {
        var chars = new List<string>(host.Cols);
        for (var col = 0; col < host.Cols; col++)
            chars.Add(host.CellAt(col, row).Text);
        return string.Concat(chars);
    }

    private static AssembledCell GlyphCell(HostFrame host, CellRect rect, string glyph)
    {
        for (var col = rect.Col; col < rect.EndCol; col++)
        {
            var cell = host.CellAt(col, rect.Row);
            if (cell.Text.Contains(glyph, StringComparison.Ordinal))
                return cell;
        }

        Assert.Fail($"Expected glyph '{glyph}' inside {rect.Col},{rect.Row} {rect.Cols}x{rect.Rows}.");
        return default;
    }

    private static uint Encode(ThemeColor color) => HostFrameCellSink.Encode(color);
}
