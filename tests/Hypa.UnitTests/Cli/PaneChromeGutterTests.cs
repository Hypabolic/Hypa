using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class PaneChromeGutterTests
{
    [Fact]
    public void Unknown_main_metrics_reserve_blank_gutter()
    {
        var geo = SinglePane();
        var pane = Assert.Single(geo.Panes);
        Assert.Null(pane.Close);
        Assert.NotNull(pane.Gutter);
        Assert.Null(pane.Scrollbar);
        Assert.Equal(pane.Frame.Cols - 1, pane.Content.Cols);
        Assert.Equal(pane.Frame.Rows, pane.Content.Rows);
        Assert.Equal(pane.Frame.EndCol - 1, pane.Gutter!.Value.Col);
        Assert.NotEqual(
            ChromeHitKind.Scrollbar,
            ChromeHitTest.Hit(geo, pane.Gutter.Value.Col, pane.Gutter.Value.Row)?.Kind);
        Assert.Null(HitClose(geo, pane));
    }

    [Fact]
    public void Known_history_shows_scrollbar_hit_and_thumb()
    {
        var geo = Bind(SinglePane(), History(offset: 0, max: 10, viewport: 10));
        var pane = Assert.Single(geo.Panes);
        Assert.Equal(pane.Gutter, pane.Scrollbar);
        var hit = ChromeHitTest.Hit(geo, pane.Scrollbar!.Value.Col, pane.Scrollbar.Value.Row);
        Assert.Equal(ChromeHitKind.Scrollbar, hit?.Kind);
        Assert.Equal("p1", hit?.PaneId);
        Assert.Null(pane.Close);

        var track = new CellRect(0, 0, 1, 10);
        var thumb = PaneChromeGutter.Thumb(History(offset: 0, max: 10, viewport: 10), track);
        Assert.Equal(new PaneScrollbarThumb(5, 5), thumb);
        var top = PaneChromeGutter.Thumb(History(offset: 10, max: 10, viewport: 10), track);
        Assert.Equal(new PaneScrollbarThumb(0, 5), top);
    }

    [Fact]
    public void Alternate_screen_reclaims_width()
    {
        var geo = Bind(
            SinglePane(),
            new PaneChromeScrollState(AlternateScreen: true, MetricsKnown: true, MaxOffsetFromBottom: 12, ViewportRows: 10));
        var pane = Assert.Single(geo.Panes);
        Assert.Null(pane.Gutter);
        Assert.Null(pane.Scrollbar);
        Assert.Equal(pane.Frame.Cols, pane.Content.Cols);
        Assert.Null(HitClose(geo, pane));
    }

    [Fact]
    public void Wide_layout_root_without_leaf_id_still_frames_focused_pane()
    {
        var geo = LayoutChromeGeometry.Compute(
            120,
            40,
            root: new LayoutNodeDto { Type = "pane" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            ScrollUi(),
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: 27);
        var pane = Assert.Single(geo.Panes);
        Assert.Equal("p1", pane.PaneId);
        Assert.True(pane.Content.Cols > 0);
        Assert.True(pane.Content.Rows > 0);
        Assert.True(pane.Content.Cols < 120);
    }

    [Fact]
    public void Four_or_fewer_inner_columns_do_not_reserve()
    {
        var geo = LayoutChromeGeometry.Compute(
            4,
            8,
            new LayoutNodeDto { Type = "pane", PaneId = "p1" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            ScrollUi() with { HideTabBarWhenSingleTab = true, MobileWidthThreshold = 0 },
            tabCount: 1,
            AttachClientMode.Terminal);
        var pane = Assert.Single(geo.Panes);
        Assert.Null(pane.Gutter);
        Assert.Null(pane.Scrollbar);
        Assert.Equal(pane.Frame.Cols, pane.Content.Cols);
    }

    [Fact]
    public void Hidden_scrollbar_has_no_hit_after_clear()
    {
        var withHistory = Bind(SinglePane(), History(offset: 4, max: 8, viewport: 10));
        var cleared = Bind(withHistory, History(offset: 0, max: 0, viewport: 10));
        var pane = Assert.Single(cleared.Panes);
        Assert.NotNull(pane.Gutter);
        Assert.Null(pane.Scrollbar);
        Assert.Equal(withHistory.Panes[0].Content, pane.Content);
        Assert.NotEqual(
            ChromeHitKind.Scrollbar,
            ChromeHitTest.Hit(cleared, pane.Gutter!.Value.Col, pane.Gutter.Value.Row)?.Kind);
    }

    [Fact]
    public void Metric_transition_to_alternate_changes_pty_content_size()
    {
        var live = LiveForChrome();
        var main = AttachSession.ComputeLiveChrome(live, 80, 24, SingleRoot(), false, null, "p1");
        Assert.Equal(79, main.Panes[0].Content.Cols);

        live.SetPaneScrollMetrics("p1", 3, 9, 23);
        live.NotePaneAlt("p1", true);
        var alt = AttachSession.ComputeLiveChrome(live, 80, 24, SingleRoot(), false, null, "p1");
        Assert.Equal(80, alt.Panes[0].Content.Cols);
        Assert.Null(alt.Panes[0].Scrollbar);
    }

    [Fact]
    public void Known_main_metrics_keep_pty_width_when_history_appears()
    {
        var live = LiveForChrome();
        var idle = AttachSession.ComputeLiveChrome(live, 80, 24, SingleRoot(), false, null, "p1");
        live.SetPaneScrollMetrics("p1", 2, 6, idle.Panes[0].Content.Rows);
        var history = AttachSession.ComputeLiveChrome(live, 80, 24, SingleRoot(), false, null, "p1");
        Assert.Equal(idle.Panes[0].Content, history.Panes[0].Content);
        Assert.NotNull(history.Panes[0].Scrollbar);
    }

    [Fact]
    public void Painter_uses_herdr_focus_colors()
    {
        var focused = Bind(SinglePane(), History(offset: 0, max: 10, viewport: 10));
        var host = Stamp(focused);
        var bar = focused.Panes[0].Scrollbar!.Value;
        var thumb = PaneChromeGutter.Thumb(focused.Panes[0].Scroll, bar)!.Value;
        var theme = ThemePalette.Catppuccin;
        var trackCell = host.CellAt(bar.Col, bar.Row);
        Assert.Equal(PaneChromeGutter.TrackGlyph, trackCell.Text);
        Assert.Equal(HostFrameCellSink.Encode(theme.Overlay0), trackCell.Style.Fg);
        var thumbCell = host.CellAt(bar.Col, thumb.Top);
        Assert.Equal(PaneChromeGutter.FocusedThumbGlyph, thumbCell.Text);
        Assert.Equal(HostFrameCellSink.Encode(theme.Overlay1), thumbCell.Style.Fg);

        var idle = Bind(
            SinglePane(focusedPaneId: "other"),
            History(offset: 0, max: 10, viewport: 10));
        var idleHost = Stamp(idle);
        var idleBar = idle.Panes[0].Scrollbar!.Value;
        var idleTrack = idleHost.CellAt(idleBar.Col, idleBar.Row);
        Assert.Equal(PaneChromeGutter.TrackGlyph, idleTrack.Text);
        Assert.Equal(HostFrameCellSink.Encode(theme.SurfaceDim), idleTrack.Style.Fg);
        var idleThumb = PaneChromeGutter.Thumb(idle.Panes[0].Scroll, idleBar)!.Value;
        var idleThumbCell = idleHost.CellAt(idleBar.Col, idleThumb.Top);
        Assert.Equal(PaneChromeGutter.TrackGlyph, idleThumbCell.Text);
        Assert.Equal(HostFrameCellSink.Encode(theme.Overlay0), idleThumbCell.Style.Fg);
    }

    [Fact]
    public void Narrow_height_still_maps_thumb()
    {
        var track = new CellRect(10, 2, 1, 1);
        var metrics = History(offset: 0, max: 4, viewport: 2);
        var thumb = PaneChromeGutter.Thumb(metrics, track);
        Assert.Equal(new PaneScrollbarThumb(2, 1), thumb);
        Assert.Equal(0, PaneChromeGutter.OffsetFromRow(metrics, track, 2));
    }

    [Fact]
    public void Split_borders_remain_draggable()
    {
        var geo = LayoutChromeGeometry.Compute(
            80,
            24,
            MouseTestGeom.RightSplit(),
            false,
            null,
            "p1",
            ScrollUi(),
            2,
            AttachClientMode.Terminal,
            [("t1", "one", true), ("t2", "two", false)]);
        Assert.NotEmpty(geo.SplitBorders);
        var split = geo.SplitBorders[0];
        var hit = ChromeHitTest.Hit(geo, split.Rect.Col, split.Rect.Row);
        Assert.Equal(ChromeHitKind.SplitBorder, hit?.Kind);
        foreach (var pane in geo.Panes)
            Assert.Null(pane.Close);
    }

    [Fact]
    public void Close_stays_on_menu_and_key()
    {
        var item = Assert.Single(
            ContextMenuModel.PaneItems(),
            i => i.Id == ContextMenuModel.Close);
        Assert.Equal("Close pane", item.Label);
        Assert.Equal(KeyActionId.ClosePane, item.Action);
        Assert.Equal("prefix+x", KeysConfig.Default().ClosePane.ToString());

        var apply = ChromeHitApply.Apply(
            new ChromeHit(ChromeHitKind.PaneClose, PaneId: "p1"),
            overflowOffset: 0,
            maxOverflowOffset: 0);
        Assert.Equal(KeyActionId.ClosePane, apply.Request?.Action);
    }

    [Fact]
    public void Thumb_press_grabs_and_drag_keeps_offset()
    {
        var geo = Bind(SinglePane(), History(offset: 0, max: 10, viewport: 10));
        var bar = geo.Panes[0].Scrollbar!.Value;
        var thumb = PaneChromeGutter.Thumb(geo.Panes[0].Scroll, bar)!.Value;
        var grabRow = thumb.Top + 1;
        var engine = new MouseEngine();
        var ctx = new MouseFeedContext(geo, FocusedPaneId: "p1", MaxOffset: 10);
        var press = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, bar.Col, grabRow),
            ctx);
        Assert.Equal(MouseEngineState.DraggingScrollbar, engine.State);
        Assert.DoesNotContain(press, r => r.Kind is MouseCommandKind.SetHistoryTop);

        var drag = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Drag, bar.Col, grabRow),
            ctx);
        var moved = Assert.Single(drag);
        Assert.Equal(MouseCommandKind.SetHistoryTop, moved.Kind);
        Assert.Equal(
            PaneChromeGutter.OffsetFromDragRow(geo.Panes[0].Scroll, bar, grabRow, 1),
            moved.Offset);
    }

    [Fact]
    public void Track_press_jumps_without_drag()
    {
        var geo = Bind(SinglePane(), History(offset: 0, max: 10, viewport: 10));
        var bar = geo.Panes[0].Scrollbar!.Value;
        var engine = new MouseEngine();
        var ctx = new MouseFeedContext(geo, FocusedPaneId: "p1", MaxOffset: 10);
        var press = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, bar.Col, bar.Row),
            ctx);
        Assert.Equal(MouseEngineState.Idle, engine.State);
        var jump = Assert.Single(press);
        Assert.Equal(MouseCommandKind.SetHistoryTop, jump.Kind);
        Assert.Equal(PaneChromeGutter.OffsetFromRow(geo.Panes[0].Scroll, bar, bar.Row), jump.Offset);
    }

    [Fact]
    public void Half_positions_round_away_from_zero_like_herdr()
    {
        var track = new CellRect(0, 0, 1, 5);
        var metrics = History(offset: 0, max: 3, viewport: 3);
        var thumb = PaneChromeGutter.Thumb(metrics, track);
        // 3 * 5 / 6 = 2.5 → Rust .round() = 3, C# ToEven = 2
        Assert.Equal(new PaneScrollbarThumb(2, 3), thumb);

        var dragTrack = new CellRect(0, 0, 1, 4);
        var drag = History(offset: 0, max: 5, viewport: 3);
        var fromHalf = PaneChromeGutter.OffsetFromDragRow(drag, dragTrack, dragTrack.Row + 1, 0);
        // thumb_len = 3*4/8 = 1.5 → 2; maxThumbTop = 2; desiredTop 1 → 1*5/2 = 2.5 → 3
        Assert.Equal(2, fromHalf);
    }

    [Fact]
    public void Idle_gutter_uses_default_cells_on_terminal_and_kanagawa()
    {
        foreach (var theme in new[] { ThemePalette.Terminal, ThemePalette.Kanagawa, ThemePalette.Catppuccin })
        {
            var geo = Bind(SinglePane(), History(offset: 4, max: 8, viewport: 10));
            var cleared = Bind(geo, History(offset: 0, max: 0, viewport: 10));
            var host = new HostFrame();
            host.Resize(cleared.Cols, cleared.Rows);
            LayoutChromePainter.Stamp(
                new HostFrameCellSink(host),
                geo,
                ScrollUi(),
                theme: theme);
            LayoutChromePainter.Stamp(
                new HostFrameCellSink(host),
                cleared,
                ScrollUi(),
                theme: theme);

            var gutter = cleared.Panes[0].Gutter!.Value;
            var surface = HostFrameCellSink.Encode(theme.Surface0);
            for (var row = gutter.Row; row < gutter.EndRow; row++)
            {
                var cell = host.CellAt(gutter.Col, row);
                Assert.Equal(" ", cell.Text);
                Assert.Equal(0u, cell.Style.Fg);
                Assert.Equal(0u, cell.Style.Bg);
                if (!theme.Surface0.IsReset)
                    Assert.NotEqual(surface, cell.Style.Bg);
            }
        }
    }

    [Fact]
    public async Task One_alternate_snapshot_leases_resize_without_a_second_event()
    {
        var (live, port, tty, assembler) = LiveForRender();
        var mainCols = live.Chrome!.Panes[0].Content.Cols;
        live.LastSentPaneSizes["p1"] = (mainCols, live.Chrome.Panes[0].Content.Rows);

        await AttachSession.HandleRenderEventAsync(
            SnapshotEvent("alternate", generation: 1),
            port,
            assembler,
            tty,
            live,
            CancellationToken.None);

        Assert.True(live.GetPaneAlt("p1"), "one snapshot must note alternate screen");
        Assert.False(live.PendingPaneChromeResize);
        Assert.Equal(live.Chrome.Panes[0].Frame.Cols, live.Chrome.Panes[0].Content.Cols);
        var alt = AssertResize(port, live.Chrome.Panes[0].Content.Cols, live.Chrome.Panes[0].Content.Rows);
        Assert.True(alt > mainCols);
        Assert.Equal("lease-r", LastResizeLease(port));
    }

    [Fact]
    public async Task One_return_to_main_snapshot_leases_the_first_new_size()
    {
        var (live, port, tty, assembler) = LiveForRender();
        live.NotePaneAlt("p1", true);
        live.Chrome = AttachSession.ComputeLiveChrome(live, 80, 24, SingleRoot(), false, null, "p1");
        var altCols = live.Chrome.Panes[0].Content.Cols;
        live.LastSentPaneSizes["p1"] = (altCols, live.Chrome.Panes[0].Content.Rows);

        await AttachSession.HandleRenderEventAsync(
            SnapshotEvent("main", generation: 2),
            port,
            assembler,
            tty,
            live,
            CancellationToken.None);

        Assert.False(live.PendingPaneChromeResize);
        Assert.True(live.Chrome.Panes[0].Content.Cols < altCols);
        AssertResize(port, live.Chrome.Panes[0].Content.Cols, live.Chrome.Panes[0].Content.Rows);
        Assert.Equal("lease-r", LastResizeLease(port));
    }

    [Fact]
    public async Task Alternate_cells_frame_resizes_the_pane_to_the_gutterless_width()
    {
        var (live, port, tty, assembler) = LiveForRender();
        var mainCols = live.Chrome!.Panes[0].Content.Cols;
        var rows = live.Chrome.Panes[0].Content.Rows;
        live.LastSentPaneSizes["p1"] = (mainCols, rows);

        await AttachSession.HandleRenderEventAsync(
            CellsEvent("alternate", mainCols, rows, generation: 1),
            port,
            assembler,
            tty,
            live,
            CancellationToken.None);

        Assert.True(live.GetPaneAlt("p1"));
        Assert.False(live.PendingPaneChromeResize);
        Assert.Equal(mainCols + 1, AssertResize(port, live.Chrome.Panes[0].Content.Cols, rows));
        Assert.Equal("lease-r", LastResizeLease(port));
    }

    [Fact]
    public async Task Alternate_cells_frame_paints_without_the_old_scrollbar()
    {
        var (live, port, tty, assembler) = LiveForRender();
        var rows = live.Chrome!.Panes[0].Content.Rows;
        live.SetPaneScrollMetrics("p1", 0, 50, rows);
        live.Chrome = AttachSession.ComputeLiveChrome(live, 80, 24, SingleRoot(), false, null, "p1");
        var mainCols = live.Chrome.Panes[0].Content.Cols;
        var track = live.Chrome.Panes[0].Scrollbar!.Value;
        live.LastSentPaneSizes["p1"] = (mainCols, rows);

        await AttachSession.HandleRenderEventAsync(
            CellsEvent("alternate", mainCols, rows, generation: 1),
            port,
            assembler,
            tty,
            live,
            CancellationToken.None);

        Assert.Null(live.Chrome.Panes[0].Scrollbar);
        for (var row = track.Row; row < track.EndRow; row++)
        {
            var text = live.Host.CellAt(track.Col, row).Text;
            Assert.NotEqual(PaneChromeGutter.TrackGlyph, text);
            Assert.NotEqual(PaneChromeGutter.FocusedThumbGlyph, text);
        }
    }

    [Fact]
    public async Task Refused_resize_stays_pending_and_is_sent_on_the_next_flush()
    {
        var (live, port, _, _) = LiveForRender();
        var mainCols = live.Chrome!.Panes[0].Content.Cols;
        var rows = live.Chrome.Panes[0].Content.Rows;
        live.LastSentPaneSizes["p1"] = (mainCols, rows);
        live.NotePaneAlt("p1", true);
        Assert.True(AttachSession.SyncPaneChromeGeometry(live, tty: null));
        var clock = new FrozenTime(DateTimeOffset.UnixEpoch);
        live.Time = clock;
        var refuse = true;
        port.Handler = (method, _) =>
        {
            if (method == ProtocolMethods.PaneResize && refuse)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.LeaseRequired, "lease required", ProtocolErrors.LeaseRequired);
            }

            return MouseTestGeom.Parse("{}");
        };

        // A lease refusal does not throw, so the render reader stays open.
        await AttachSession.FlushPaneChromeResizeAsync(port, live, CancellationToken.None);
        Assert.True(live.PendingPaneChromeResize);

        // Inside the backoff, a flush does not send the resize again.
        refuse = false;
        var sent = port.Calls.Count(call => call.Method == ProtocolMethods.PaneResize);
        await AttachSession.FlushPaneChromeResizeAsync(port, live, CancellationToken.None);
        Assert.True(live.PendingPaneChromeResize);
        Assert.Equal(sent, port.Calls.Count(call => call.Method == ProtocolMethods.PaneResize));

        clock.Advance(TimeSpan.FromSeconds(1));
        await AttachSession.FlushPaneChromeResizeAsync(port, live, CancellationToken.None);

        Assert.False(live.PendingPaneChromeResize);
        Assert.Equal(mainCols + 1, AssertResize(port, mainCols + 1, rows));
        Assert.Equal((mainCols + 1, rows), live.LastSentPaneSizes["p1"]);
    }

    [Fact]
    public async Task Other_resize_refusal_propagates_and_stays_pending()
    {
        var (live, port, _, _) = LiveForRender();
        var mainCols = live.Chrome!.Panes[0].Content.Cols;
        var rows = live.Chrome.Panes[0].Content.Rows;
        live.LastSentPaneSizes["p1"] = (mainCols, rows);
        live.NotePaneAlt("p1", true);
        Assert.True(AttachSession.SyncPaneChromeGeometry(live, tty: null));
        var clock = new FrozenTime(DateTimeOffset.UnixEpoch);
        live.Time = clock;
        var refuse = true;
        port.Handler = (method, _) =>
        {
            if (method == ProtocolMethods.PaneResize && refuse)
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "frozen");
            return MouseTestGeom.Parse("{}");
        };

        await Assert.ThrowsAsync<ControlPlaneException>(() =>
            AttachSession.FlushPaneChromeResizeAsync(port, live, CancellationToken.None));
        Assert.True(live.PendingPaneChromeResize);

        refuse = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        await AttachSession.FlushPaneChromeResizeAsync(port, live, CancellationToken.None);

        Assert.False(live.PendingPaneChromeResize);
        Assert.Equal((mainCols + 1, rows), live.LastSentPaneSizes["p1"]);
    }

    [Fact]
    public async Task Idle_heartbeat_sends_a_refused_resize_after_the_backoff()
    {
        var (live, port, tty, _) = LiveForRender();
        var mainCols = live.Chrome!.Panes[0].Content.Cols;
        var rows = live.Chrome.Panes[0].Content.Rows;
        live.LastSentPaneSizes["p1"] = (mainCols, rows);
        live.NotePaneAlt("p1", true);
        Assert.True(AttachSession.SyncPaneChromeGeometry(live, tty: null));
        var clock = new FrozenTime(DateTimeOffset.UnixEpoch);
        live.Time = clock;
        var refuse = true;
        port.Handler = (method, _) =>
        {
            if (method == ProtocolMethods.PaneResize && refuse)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.LeaseRequired, "lease required", ProtocolErrors.LeaseRequired);
            }

            return MouseTestGeom.Parse("{}");
        };

        // A heartbeat with no events still drains the control path.
        await AttachSession.ApplyControlEventsAsync([], live, port, tty, CancellationToken.None);
        Assert.True(live.PendingPaneChromeResize);
        Assert.Null(live.StatusError);

        refuse = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        await AttachSession.ApplyControlEventsAsync([], live, port, tty, CancellationToken.None);

        Assert.False(live.PendingPaneChromeResize);
        Assert.Equal(mainCols + 1, AssertResize(port, mainCols + 1, rows));
    }

    [Fact]
    public async Task Agent_status_that_changes_pane_width_sends_the_resize()
    {
        var (live, port, tty, _) = LiveForRender();
        var mainCols = live.Chrome!.Panes[0].Content.Cols;
        var rows = live.Chrome.Panes[0].Content.Rows;
        live.LastSentPaneSizes["p1"] = (mainCols, rows);
        // The pane is on the alternate screen, but chrome still has the main width.
        live.NotePaneAlt("p1", true);

        await AttachSession.ApplyControlEventsAsync(
            [AgentStatusEvent("working")], live, port, tty, CancellationToken.None);

        Assert.False(live.PendingPaneChromeResize);
        Assert.Equal(mainCols + 1, AssertResize(port, live.Chrome.Panes[0].Content.Cols, rows));
    }

    [Fact]
    public async Task Second_activation_of_an_endpoint_publishes_pane_sizes_again()
    {
        var (live, port, _, _) = LiveForRender();
        live.ActiveProjectionEndpointId = "plc_docker";
        live.LastSentPaneSizes["p1"] = (
            live.Chrome!.Panes[0].Content.Cols,
            live.Chrome.Panes[0].Content.Rows);

        AttachSession.ActivateEndpointProjection(live, "local");
        AttachSession.ActivateEndpointProjection(live, "plc_docker");
        live.Chrome = AttachSession.ComputeLiveChrome(live, 80, 24, SingleRoot(), false, null, "p1");
        live.PendingPaneChromeResize = true;
        await AttachSession.FlushPaneChromeResizeAsync(port, live, CancellationToken.None);

        Assert.Contains(port.Calls, call => call.Method == ProtocolMethods.PaneResize
            && call.Params?["pane_id"]?.GetValue<string>() == "p1");
        AssertResize(port, live.Chrome.Panes[0].Content.Cols, live.Chrome.Panes[0].Content.Rows);

        var sent = port.Calls.Count(call => call.Method == ProtocolMethods.PaneResize);
        AttachSession.ActivateEndpointProjection(live, "plc_docker");
        live.PendingPaneChromeResize = true;
        await AttachSession.FlushPaneChromeResizeAsync(port, live, CancellationToken.None);
        Assert.Equal(sent, port.Calls.Count(call => call.Method == ProtocolMethods.PaneResize));
    }

    [Fact]
    public async Task Zero_size_pane_resize_sets_status_without_throwing()
    {
        var (live, port, _, _) = LiveForRender();
        var pane = live.Chrome!.Panes[0] with { Content = new CellRect(1, 1, 0, 0) };
        live.Chrome = live.Chrome with { Panes = [pane] };
        live.PendingPaneChromeResize = true;

        await AttachSession.FlushPaneChromeResizeAsync(port, live, CancellationToken.None);

        Assert.Equal("pane content has no size", live.StatusError);
        Assert.DoesNotContain(port.Calls, call => call.Method == ProtocolMethods.PaneResize);
    }

    [Fact]
    public async Task Unexpected_resize_fault_is_logged_and_propagates()
    {
        var (live, port, _, _) = LiveForRender();
        var sink = new CapturingProcessLogSink();
        live.ProcessLog = sink;
        live.SessionName = "sess";
        live.AttachClientId = "client";
        port.Handler = (method, _) =>
        {
            if (method == ProtocolMethods.PaneResize)
                throw new InvalidOperationException("resize lease missing");
            return MouseTestGeom.Parse("{}");
        };
        live.PendingPaneChromeResize = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AttachSession.FlushPaneChromeResizeAsync(port, live, CancellationToken.None));

        Assert.Equal("resize lease missing", ex.Message);
        var fault = Assert.Single(sink.Records, record => record.Event == ProcessLogEvents.AttachLoopFault);
        Assert.Equal("pane.resize", fault.Action);
        Assert.Contains("InvalidOperationException", fault.Reason, StringComparison.Ordinal);
        Assert.Contains("resize lease missing", fault.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Scroll_changed_rebinds_hits()
    {
        var live = LiveForChrome();
        live.ChromeEnabled = true;
        live.ChromeSeed = new ChromeComputeSeed(SingleRoot(), false, null, "p1", 80, 24);
        live.Chrome = AttachSession.ComputeLiveChrome(live, 80, 24, SingleRoot(), false, null, "p1");
        Assert.Null(live.Chrome.Panes[0].Scrollbar);

        using var doc = System.Text.Json.JsonDocument.Parse(
            """{"pane_id":"p1","offset":2,"max_offset":8}""");
        Assert.True(AttachSession.TryApplyPaneScrollChanged(live, doc.RootElement));
        Assert.NotNull(live.Chrome?.Panes[0].Scrollbar);
        Assert.Equal(79, live.Chrome!.Panes[0].Content.Cols);
    }

    private static ChromeHit? HitClose(LayoutChromeGeometry geo, ChromePaneFrame pane)
    {
        var col = pane.Frame.EndCol - 1;
        var row = pane.Frame.Row;
        var hit = ChromeHitTest.Hit(geo, col, row);
        return hit is { Kind: ChromeHitKind.PaneClose } ? hit : null;
    }

    private static LayoutChromeGeometry Bind(
        LayoutChromeGeometry geo,
        PaneChromeScrollState scroll) =>
        PaneChromeGutter.Bind(geo, ScrollUi(), new Dictionary<string, PaneChromeScrollState>
        {
            [geo.Panes[0].PaneId] = scroll,
        });

    private static PaneChromeScrollState History(int offset, int max, int viewport) =>
        new(false, true, offset, max, viewport);

    private static LayoutChromeGeometry SinglePane(string focusedPaneId = "p1") =>
        LayoutChromeGeometry.Compute(
            80,
            24,
            SingleRoot(),
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId,
            ScrollUi(),
            tabCount: 1,
            AttachClientMode.Terminal);

    private static LayoutNodeDto SingleRoot() =>
        new() { Type = "pane", PaneId = "p1", Label = "main" };

    private static AttachUiConfig ScrollUi() => new()
    {
        PaneBorders = false,
        PaneOuterBorders = false,
        PaneGaps = false,
        PaneScrollbars = true,
        HideTabBarWhenSingleTab = true,
        TabBarPosition = TabBarPosition.Top,
        TabBarRight = [],
        MobileWidthThreshold = 0,
    };

    private static HostFrame Stamp(LayoutChromeGeometry geo)
    {
        var host = new HostFrame();
        host.Resize(geo.Cols, geo.Rows);
        LayoutChromePainter.Stamp(
            new HostFrameCellSink(host),
            geo,
            ScrollUi(),
            theme: ThemePalette.Catppuccin);
        return host;
    }

    private static (AttachLiveState Live, MouseRecordingPort Port, UnixRawTerminal Tty, SnapshotAssembler Assembler)
        LiveForRender()
    {
        var port = MouseTestGeom.ApplyPort();
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(ScrollUi())),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r"),
            Ui = ScrollUi(),
            ChromeEnabled = true,
            PaneId = "p1",
            ResizeLease = "lease-r",
        };
        live.ChromeSeed = new ChromeComputeSeed(SingleRoot(), false, null, "p1", 80, 24);
        live.Chrome = AttachSession.ComputeLiveChrome(live, 80, 24, SingleRoot(), false, null, "p1");
        var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        return (live, port, tty, new SnapshotAssembler());
    }

    private static JsonElement SnapshotEvent(string screen, long generation)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "params": {
                "type": "terminal.render",
                "payload": {
                  "pane_id": "p1",
                  "kind": "snapshot",
                  "row_start": 0,
                  "row_end": 1,
                  "complete": true,
                  "grid_cols": 80,
                  "grid_rows": 1,
                  "generation": {{generation}},
                  "provider": "basic",
                  "active_screen": "{{screen}}",
                  "snapshot": {
                    "provider": "basic",
                    "active_screen": "{{screen}}"
                  }
                }
              }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement CellsEvent(string screen, int cols, int rows, long generation)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "params": {
                "type": "terminal.render",
                "payload": {
                  "pane_id": "p1",
                  "kind": "{{TerminalRenderCellsPayload.KindCells}}",
                  "full": true,
                  "reanchor": true,
                  "grid_cols": {{cols}},
                  "grid_rows": {{rows}},
                  "generation": {{generation}},
                  "active_screen": "{{screen}}",
                  "rows": []
                }
              }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement AgentStatusEvent(string status)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "event": "{{ProtocolEventTypes.RuntimeEvent}}",
              "params": {
                "type": "{{ProtocolEventTypes.PaneAgentStatusChanged}}",
                "payload": {
                  "pane_id": "p1",
                  "tab_id": "t1",
                  "occupant_generation": 1,
                  "agent_status": "{{status}}",
                  "agent": "claude",
                  "seen": true
                }
              }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static int AssertResize(MouseRecordingPort port, int cols, int rows)
    {
        var resize = port.Calls.Last(c => c.Method == ProtocolMethods.PaneResize);
        Assert.Equal(cols, resize.Params?["cols"]?.GetValue<int>());
        Assert.Equal(rows, resize.Params?["rows"]?.GetValue<int>());
        return cols;
    }

    private static string? LastResizeLease(MouseRecordingPort port)
    {
        var resize = port.Calls.Last(c => c.Method == ProtocolMethods.PaneResize);
        return resize.Params?["lease_id"]?.GetValue<string>();
    }

    private static AttachLiveState LiveForChrome()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var port = MouseTestGeom.ApplyPort();
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(ScrollUi())),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r"),
            Ui = ScrollUi(),
            ChromeEnabled = true,
            PaneId = "p1",
        };
    }
}
