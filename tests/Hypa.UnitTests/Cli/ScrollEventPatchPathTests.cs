using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// <c>pane.scroll_changed</c> and the client patch path. Two panes: typed
/// echo deltas in the left pane, bulk deltas and scroll events in the right
/// pane. A second attach with the patch path off composes the full redraw of
/// each paint. After each paint both host frames are equal, cell by cell.
/// </summary>
public sealed class ScrollEventPatchPathTests
{
    private const int Cols = 100;
    private const int Rows = 30;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scroll_events_that_change_no_chrome_cell_cause_no_full_compose(bool desktopTheme)
    {
        using var pair = new Pair(desktopTheme);
        var left = pair.Box("p1");
        var right = pair.Box("p2");
        var track = pair.Patch.Chrome!.Panes.Single(p => p.PaneId == "p2").Gutter!.Value.Rows;
        // Past this max the thumb has one row at the bottom of the track.
        var follow = track * track * 2;
        Assert.Equal(1, ScrollbarThumbMath.Compute(0, follow, track, track)!.Value.Length);

        pair.Feed(Full("p1", left, 1, "$ "));
        pair.Feed(Full("p2", right, 1, "build"));
        pair.Scroll("p2", 0, follow);
        pair.Feed(Delta("p1", left, 1, 2, 0, "$ "));

        var full = pair.Patch.Latency.Paint.FullComposes;
        var patches = pair.Patch.Latency.Paint.Patches;
        long g1 = 2;
        long g2 = 1;
        var typed = "$ ";
        for (var step = 0; step < 40; step++)
        {
            typed += (char)('a' + (step % 26));
            pair.Feed(Delta("p1", left, g1, ++g1, 0, typed));
            pair.Feed(Delta("p2", right, g2, ++g2, step % right.Rows, "chunk " + step));
            // The pane follows its output. The history grows and the drain
            // makes it smaller again. The thumb does not move.
            pair.Scroll("p2", 0, follow + (step % 7 < 5 ? step : -step));
        }

        var counters = pair.Patch.Latency.Paint;
        Assert.Equal(0, counters.Fallbacks(HostPatchFallback.Chrome));
        Assert.Equal(full, counters.FullComposes);
        Assert.Equal(patches + 80, counters.Patches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_scroll_event_that_moves_the_thumb_repaints_the_gutter_and_equals_a_full_redraw(bool desktopTheme)
    {
        using var pair = new Pair(desktopTheme);
        var left = pair.Box("p1");
        var right = pair.Box("p2");
        var gutter = pair.Patch.Chrome!.Panes.Single(p => p.PaneId == "p2").Gutter!.Value;
        var follow = gutter.Rows * gutter.Rows * 2;

        pair.Feed(Full("p1", left, 1, "$ "));
        pair.Feed(Full("p2", right, 1, "build"));
        pair.Scroll("p2", 0, follow);
        long g1 = 1;
        pair.Feed(Delta("p1", left, g1, ++g1, 0, "$ l"));
        var thumbBefore = ThumbRows(pair.Patch, gutter);

        // A short history gives a long thumb, then the view scrolls up: the
        // thumb moves. A max of 0 hides the scrollbar, and the next shows it.
        var states = new (int Offset, int Max)[] { (0, gutter.Rows), (gutter.Rows / 2, gutter.Rows), (0, 0), (0, follow) };
        var shown = new List<string>();
        foreach (var (offset, max) in states)
        {
            var full = pair.Patch.Latency.Paint.FullComposes;
            var patches = pair.Patch.Latency.Paint.Patches;
            pair.Scroll("p2", offset, max);
            Assert.Contains("p2", pair.Patch.PendingGutterPanes);
            pair.Feed(Delta("p1", left, g1, ++g1, 0, "$ l" + g1));

            // The gutter of the right pane shows the new scrollbar, as the
            // full redraw shows it, and the paint was a patch.
            Assert.Empty(pair.Patch.PendingGutterPanes);
            shown.Add(ThumbRows(pair.Patch, gutter));
            Assert.Equal(full, pair.Patch.Latency.Paint.FullComposes);
            Assert.Equal(patches + 1, pair.Patch.Latency.Paint.Patches);
        }

        Assert.Equal(4, shown.Distinct().Count());
        Assert.Equal(thumbBefore, shown[^1]);
    }

    [Fact]
    public void A_gutter_under_the_desktop_status_row_takes_a_full_compose()
    {
        // With no pane borders the gutter runs to the last host row. The
        // desktop status row paints over that row after the gutter.
        using var pair = new Pair(desktopTheme: true, borders: false);
        var status = pair.Patch.Chrome!.StatusRow!.Value;
        var gutter = pair.Patch.Chrome.Panes.Single(p => p.PaneId == "p2").Gutter!.Value;
        Assert.True(status.Intersects(gutter));
        var left = pair.Box("p1");
        var right = pair.Box("p2");

        pair.Feed(Full("p1", left, 1, "$ "));
        pair.Feed(Full("p2", right, 1, "build"));
        pair.Scroll("p2", 0, gutter.Rows * gutter.Rows * 2);
        pair.Feed(Delta("p1", left, 1, 2, 0, "$ l"));
        var chrome = pair.Patch.Latency.Paint.Fallbacks(HostPatchFallback.Chrome);

        pair.Scroll("p2", 0, gutter.Rows);
        pair.Feed(Delta("p1", left, 2, 3, 0, "$ ls"));
        Assert.Equal(chrome + 1, pair.Patch.Latency.Paint.Fallbacks(HostPatchFallback.Chrome));
        Assert.Empty(pair.Patch.PendingGutterPanes);
    }

    [Fact]
    public void An_older_scroll_state_of_the_same_mux_is_dropped()
    {
        var live = NewLive(desktopTheme: false);
        Assert.True(Apply(live, Scroll("p1", 10, 100, revision: 5, epoch: "a")));
        Assert.Equal(10, Offset(live));

        // An event that left the mux before the state of revision 5.
        Assert.False(Apply(live, Scroll("p1", 4, 100, revision: 3, epoch: "a")));
        Assert.Equal(10, Offset(live));

        // A pane.scroll reply with a newer state.
        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 13, 100, revision: 6, epoch: "a"));
        Assert.Equal(13, Offset(live));
        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 11, 100, revision: 4, epoch: "a"));
        Assert.Equal(13, Offset(live));

        // A new mux counts from 1 again. Its states are not older. Its
        // events come on a new subscription.
        live.NoteScrollEventSubscriptionStarted();
        Assert.True(Apply(live, Scroll("p1", 2, 100, revision: 1, epoch: "b")));
        Assert.Equal(2, Offset(live));

        // A mux that sends no revision.
        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 0, 100, revision: null, epoch: null));
        Assert.Equal(0, Offset(live));
    }

    [Fact]
    public void A_cells_frame_origin_only_raises_the_offset()
    {
        var live = NewLive(desktopTheme: false);
        Assert.True(Apply(live, Scroll("p1", 20, 100, revision: 2, epoch: "a")));

        // A frame captured before the last reply carries an older origin.
        AttachSession.ApplyCellsViewportOrigin(live, new TerminalRenderCellsPayload { Kind = TerminalRenderCellsPayload.KindCells, PaneId = "p1", ViewportOrigin = 17 });
        Assert.Equal(20, Offset(live));

        AttachSession.ApplyCellsViewportOrigin(live, new TerminalRenderCellsPayload { Kind = TerminalRenderCellsPayload.KindCells, PaneId = "p1", ViewportOrigin = 26 });
        Assert.Equal(26, Offset(live));
    }

    [Fact]
    public void A_replayed_event_of_an_earlier_mux_does_not_replace_the_state_of_the_current_mux()
    {
        var live = NewLive(desktopTheme: false);
        const string earlier = "00000000000000000100-aaaaaaaa";
        const string current = "00000000000000000200-bbbbbbbb";

        // The current mux answers a wheel step.
        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 20, 100, revision: 3, epoch: current));
        Assert.Equal(20, Offset(live));

        // A journal replay delivers a record of the earlier mux after that
        // reply. Its revision is higher, but its epoch is earlier.
        Assert.False(Apply(live, Scroll("p1", 5, 100, revision: 50, epoch: earlier)));
        Assert.Equal(20, Offset(live));

        // The current mux goes on.
        Assert.True(Apply(live, Scroll("p1", 24, 100, revision: 4, epoch: current)));
        Assert.Equal(24, Offset(live));

        // A reply always comes from the mux that serves the attach now, even
        // when its clock put its epoch earlier.
        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 7, 100, revision: 1, epoch: earlier));
        Assert.Equal(7, Offset(live));
    }

    [Fact]
    public async Task A_track_jump_at_the_bottom_reads_max_offset_again_and_a_drag_step_does_not()
    {
        var live = NewLive(desktopTheme: false);
        // The mux did not send the last changes of max_offset: the view
        // follows its output.
        Assert.True(Apply(live, Scroll("p1", 0, 400, revision: 1, epoch: "a")));
        var pane = live.Chrome!.Panes.Single(p => p.PaneId == "p1");
        var bar = pane.Scrollbar!.Value;
        var row = bar.Row + (bar.Rows / 2);
        var stale = PaneChromeGutter.OffsetFromRow(pane.Scroll, bar, row);
        var port = FreshMaxPort(1000);

        await AttachSession.PublishServerScrollbarAsync(
            live,
            port,
            "p1",
            new MouseEngineResult(MouseCommandKind.SetHistoryTop, PaneId: "p1", Offset: stale, TrackRow: row, TrackJump: true),
            CancellationToken.None);

        Assert.Equal(2, port.Calls.Count);
        Assert.Equal(0, port.Calls[0].Params!["offset"]!.GetValue<int>());
        var fresh = PaneChromeGutter.OffsetFromRow(pane.Scroll with { MaxOffsetFromBottom = 1000 }, bar, row);
        Assert.NotEqual(stale, fresh);
        Assert.Equal(fresh, port.Calls[1].Params!["offset"]!.GetValue<int>());

        // A drag step keeps the offset that the drag mapped: no read first.
        var drag = NewLive(desktopTheme: false);
        Assert.True(Apply(drag, Scroll("p1", 0, 400, revision: 1, epoch: "a")));
        var dragPort = FreshMaxPort(1000);
        await AttachSession.PublishServerScrollbarAsync(
            drag,
            dragPort,
            "p1",
            new MouseEngineResult(MouseCommandKind.SetHistoryTop, PaneId: "p1", Offset: stale, TrackRow: row),
            CancellationToken.None);
        var call = Assert.Single(dragPort.Calls);
        Assert.Equal(stale, call.Params!["offset"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_thumb_press_reads_max_offset_again_without_moving_the_view_and_the_drag_maps_with_it()
    {
        var live = NewLive(desktopTheme: false);
        Assert.True(Apply(live, Scroll("p1", 0, 400, revision: 1, epoch: "a")));
        var pane = live.Chrome!.Panes.Single(p => p.PaneId == "p1");
        var bar = pane.Scrollbar!.Value;
        var thumb = PaneChromeGutter.Thumb(pane.Scroll, bar)!.Value;
        var context = new MouseFeedContext(live.Chrome, FocusedPaneId: "p1", MaxOffset: 400);

        var press = Assert.Single(live.Mouse.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, bar.Col, thumb.Top),
            context));
        Assert.Equal(MouseCommandKind.RefreshScrollMetrics, press.Kind);
        Assert.Equal(MouseEngineState.DraggingScrollbar, live.Mouse.State);

        var port = FreshMaxPort(1000);
        await AttachSession.RefreshScrollMetricsForDragAsync(live, port, "p1", CancellationToken.None);
        var read = Assert.Single(port.Calls);
        Assert.Equal(0, read.Params!["delta"]!.GetValue<int>());
        Assert.Equal(0, Offset(live));

        var target = bar.Row + 2;
        var moved = Assert.Single(live.Mouse.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Drag, bar.Col, target),
            context));
        Assert.Equal(MouseCommandKind.SetHistoryTop, moved.Kind);
        Assert.False(moved.TrackJump);
        var fresh = PaneChromeGutter.OffsetFromDragRow(pane.Scroll with { MaxOffsetFromBottom = 1000 }, bar, target, 0);
        var stale = PaneChromeGutter.OffsetFromDragRow(pane.Scroll, bar, target, 0);
        Assert.NotEqual(stale, fresh);
        Assert.Equal(fresh, moved.Offset);
    }

    [Fact]
    public void The_epoch_of_the_mux_that_answers_wins_when_a_replayed_epoch_sorts_after_it()
    {
        var live = NewLive(desktopTheme: false);
        // The wall clock went back across the mux restart, so the epoch of
        // the earlier mux sorts after the epoch of the current mux.
        const string earlier = "00000000000000000200-aaaaaaaa";
        const string current = "00000000000000000100-bbbbbbbb";

        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 20, 100, revision: 3, epoch: current));
        Assert.Equal(20, Offset(live));

        // A journal replay delivers a record of the earlier mux.
        Assert.False(Apply(live, Scroll("p1", 5, 100, revision: 50, epoch: earlier)));
        Assert.Equal(20, Offset(live));

        // The events of the current mux do not look old.
        Assert.True(Apply(live, Scroll("p1", 24, 100, revision: 4, epoch: current)));
        Assert.Equal(24, Offset(live));
        Assert.False(Apply(live, Scroll("p1", 6, 100, revision: 51, epoch: earlier)));
        Assert.True(Apply(live, Scroll("p1", 26, 100, revision: 5, epoch: current)));
        Assert.Equal(26, Offset(live));
    }

    [Fact]
    public void Before_a_reply_the_epoch_that_comes_later_in_the_event_stream_wins()
    {
        var live = NewLive(desktopTheme: false);
        const string earlier = "00000000000000000200-aaaaaaaa";
        const string current = "00000000000000000100-bbbbbbbb";

        // A replay of the earlier mux, then the live events of the current
        // mux: one subscription sends them in journal order.
        Assert.True(Apply(live, Scroll("p1", 30, 100, revision: 40, epoch: earlier)));
        Assert.True(Apply(live, Scroll("p1", 0, 100, revision: 1, epoch: current)));
        Assert.Equal(0, Offset(live));

        // The earlier mux does not come back: a late record of it is dropped.
        Assert.False(Apply(live, Scroll("p1", 35, 100, revision: 41, epoch: earlier)));
        Assert.True(Apply(live, Scroll("p1", 3, 100, revision: 2, epoch: current)));
        Assert.Equal(3, Offset(live));

        // After a reply of the current mux, an event of an epoch that the
        // attach did not see yet is a replayed record of an earlier mux.
        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 4, 100, revision: 3, epoch: current));
        Assert.False(Apply(live, Scroll("p1", 9, 100, revision: 99, epoch: "00000000000000000050-cccccccc")));
        Assert.Equal(4, Offset(live));

        // A new subscription can come from a restarted mux. Its events then
        // replace the epoch again.
        live.NoteScrollEventSubscriptionStarted();
        Assert.True(Apply(live, Scroll("p1", 0, 100, revision: 1, epoch: "00000000000000000060-dddddddd")));
        Assert.Equal(0, Offset(live));
    }

    [Fact]
    public void A_new_event_subscription_id_ends_the_pin_of_the_reply_epoch()
    {
        var live = NewLive(desktopTheme: false);
        live.ControlSub = "sub_1";
        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 8, 100, revision: 2, epoch: "00000000000000000900-aaaaaaaa"));
        Assert.False(Apply(live, Scroll("p1", 0, 100, revision: 1, epoch: "00000000000000000100-bbbbbbbb")));

        // The same id is no new subscription.
        live.ControlSub = "sub_1";
        Assert.False(Apply(live, Scroll("p1", 0, 100, revision: 1, epoch: "00000000000000000100-bbbbbbbb")));

        // A cube connect commits the subscription of another mux.
        live.ControlSub = "sub_7";
        Assert.True(Apply(live, Scroll("p1", 0, 100, revision: 1, epoch: "00000000000000000100-bbbbbbbb")));
        Assert.Equal(0, Offset(live));
    }

    [Fact]
    public void A_revisionless_event_does_not_replace_a_state_with_a_revision()
    {
        var live = NewLive(desktopTheme: false);
        // A mux that sends no revision: its events apply.
        Assert.True(Apply(live, Scroll("p1", 2, 100, revision: null, epoch: null)));
        Assert.True(Apply(live, Scroll("p1", 3, 100, revision: null, epoch: null)));
        Assert.Equal(3, Offset(live));

        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 20, 100, revision: 7, epoch: "a"));
        Assert.Equal(20, Offset(live));

        // A journal record in the old format (no revision) replays.
        Assert.False(Apply(live, Scroll("p1", 0, 100, revision: null, epoch: null)));
        Assert.Equal(20, Offset(live));

        // Another pane has no state with a revision.
        Assert.True(Apply(live, Scroll("p2", 4, 100, revision: null, epoch: null)));

        // A reply with no revision: the mux that serves the attach sends
        // none. Its events apply again, and a record of the earlier mux does not.
        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 11, 100, revision: null, epoch: null));
        Assert.Equal(11, Offset(live));
        Assert.True(Apply(live, Scroll("p1", 12, 100, revision: null, epoch: null)));
        Assert.Equal(12, Offset(live));
        Assert.False(Apply(live, Scroll("p1", 30, 100, revision: 8, epoch: "a")));
        Assert.Equal(12, Offset(live));
    }

    [Fact]
    public async Task A_track_jump_while_scrolled_back_reads_the_metrics_again_without_moving_the_view()
    {
        var live = NewLive(desktopTheme: false);
        Assert.True(Apply(live, Scroll("p1", 50, 400, revision: 1, epoch: "a")));
        var pane = live.Chrome!.Panes.Single(p => p.PaneId == "p1");
        var bar = pane.Scrollbar!.Value;
        var row = bar.Row + (bar.Rows / 2);
        var stale = PaneChromeGutter.OffsetFromRow(pane.Scroll, bar, row);
        // Output moved the view up and the history grew while the event
        // waited for the coalesce.
        var port = MovingMuxPort(offset: 57, max: 1000);

        await AttachSession.PublishServerScrollbarAsync(
            live,
            port,
            "p1",
            new MouseEngineResult(MouseCommandKind.SetHistoryTop, PaneId: "p1", Offset: stale, TrackRow: row, TrackJump: true),
            CancellationToken.None);

        Assert.Equal(2, port.Calls.Count);
        // The read is a step of 0 rows: an absolute offset of the old value
        // would move the view down.
        Assert.Equal(0, port.Calls[0].Params!["delta"]!.GetValue<int>());
        var fresh = PaneChromeGutter.OffsetFromRow(pane.Scroll with { OffsetFromBottom = 57, MaxOffsetFromBottom = 1000 }, bar, row);
        Assert.NotEqual(stale, fresh);
        Assert.Equal(fresh, port.Calls[1].Params!["offset"]!.GetValue<int>());
        Assert.Equal(fresh, Offset(live));
    }

    [Fact]
    public async Task A_thumb_press_while_scrolled_back_reads_the_metrics_again_and_the_drag_maps_with_them()
    {
        var live = NewLive(desktopTheme: false);
        Assert.True(Apply(live, Scroll("p1", 50, 400, revision: 1, epoch: "a")));
        var pane = live.Chrome!.Panes.Single(p => p.PaneId == "p1");
        var bar = pane.Scrollbar!.Value;
        var thumb = PaneChromeGutter.Thumb(pane.Scroll, bar)!.Value;
        var context = new MouseFeedContext(live.Chrome, FocusedPaneId: "p1", MaxOffset: 400);

        var press = Assert.Single(live.Mouse.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, bar.Col, thumb.Top),
            context));
        Assert.Equal(MouseCommandKind.RefreshScrollMetrics, press.Kind);

        var port = MovingMuxPort(offset: 57, max: 1000);
        await AttachSession.RefreshScrollMetricsForDragAsync(live, port, "p1", CancellationToken.None);
        var read = Assert.Single(port.Calls);
        Assert.Equal(0, read.Params!["delta"]!.GetValue<int>());
        Assert.Equal(57, Offset(live));

        var target = bar.Row + 2;
        var moved = Assert.Single(live.Mouse.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Drag, bar.Col, target),
            context));
        Assert.Equal(MouseCommandKind.SetHistoryTop, moved.Kind);
        var fresh = PaneChromeGutter.OffsetFromDragRow(pane.Scroll with { MaxOffsetFromBottom = 1000 }, bar, target, 0);
        var stale = PaneChromeGutter.OffsetFromDragRow(pane.Scroll, bar, target, 0);
        Assert.NotEqual(stale, fresh);
        Assert.Equal(fresh, moved.Offset);
    }

    [Fact]
    public async Task A_thumb_press_while_scrolled_back_does_not_read_from_a_mux_that_sends_no_revision()
    {
        // That mux does not know delta. It would set the view to the old
        // offset that the attach sends.
        var live = NewLive(desktopTheme: false);
        Assert.True(Apply(live, Scroll("p1", 50, 400, revision: null, epoch: null)));
        var port = MovingMuxPort(offset: 57, max: 1000);
        await AttachSession.RefreshScrollMetricsForDragAsync(live, port, "p1", CancellationToken.None);
        Assert.Empty(port.Calls);
        Assert.Equal(50, Offset(live));
    }

    [Fact]
    public async Task After_a_new_subscription_a_thumb_press_while_scrolled_back_does_not_read_with_a_revision_of_the_earlier_mux()
    {
        var live = NewLive(desktopTheme: false);
        // The earlier mux sent revisions, and the view is scrolled back.
        AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 50, 400, revision: 3, epoch: "a"));
        Assert.Equal(50, Offset(live));

        // The mux restarted, and the attach subscribed again. The new mux
        // does not know delta. Its view of the pane is at 57.
        live.NoteScrollEventSubscriptionStarted();
        var mux = new NoDeltaMux(offset: 57, max: 1000);
        await AttachSession.RefreshScrollMetricsForDragAsync(live, mux.Port, "p1", CancellationToken.None);

        Assert.Empty(mux.Port.Calls);
        Assert.Equal(57, mux.Offset);
    }

    [Fact]
    public async Task After_a_new_subscription_a_track_jump_while_scrolled_back_maps_with_the_stored_metrics_and_does_not_read()
    {
        var live = NewLive(desktopTheme: false);
        Assert.True(Apply(live, Scroll("p1", 50, 400, revision: 1, epoch: "a")));
        var pane = live.Chrome!.Panes.Single(p => p.PaneId == "p1");
        var bar = pane.Scrollbar!.Value;
        var row = bar.Row + (bar.Rows / 2);
        var mapped = PaneChromeGutter.OffsetFromRow(pane.Scroll, bar, row);
        Assert.NotEqual(50, mapped);

        live.NoteScrollEventSubscriptionStarted();
        var mux = new NoDeltaMux(offset: 57, max: 1000);
        await AttachSession.PublishServerScrollbarAsync(
            live,
            mux.Port,
            "p1",
            new MouseEngineResult(MouseCommandKind.SetHistoryTop, PaneId: "p1", Offset: mapped, TrackRow: row, TrackJump: true),
            CancellationToken.None);

        // One call: the jump to the row that the user clicked. A read with
        // the stored offset would first move the view of the new mux to 50.
        var call = Assert.Single(mux.Port.Calls);
        Assert.Null(call.Params!["delta"]);
        Assert.Equal(mapped, call.Params!["offset"]!.GetValue<int>());
        Assert.Equal(mapped, mux.Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task After_a_new_subscription_the_read_comes_back_when_the_serving_mux_sends_a_revision(bool fromReply)
    {
        var live = NewLive(desktopTheme: false);
        Assert.True(Apply(live, Scroll("p1", 50, 400, revision: 1, epoch: "a")));
        live.NoteScrollEventSubscriptionStarted();

        // The new stream shows the epoch of the serving mux.
        if (fromReply)
            AttachSession.ApplyPaneScrollReply(live, "p1", Scroll("p1", 52, 400, revision: 1, epoch: "b"));
        else
            Assert.True(Apply(live, Scroll("p1", 52, 400, revision: 2, epoch: "a")));

        var port = MovingMuxPort(offset: 57, max: 1000, epoch: fromReply ? "b" : "a");
        await AttachSession.RefreshScrollMetricsForDragAsync(live, port, "p1", CancellationToken.None);
        var read = Assert.Single(port.Calls);
        Assert.Equal(0, read.Params!["delta"]!.GetValue<int>());
        Assert.Equal(57, Offset(live));
    }

    /// <summary>
    /// A mux with no <c>delta</c> and no revision: an absolute
    /// <c>offset</c> sets its view of the pane. <see cref="Offset"/> is that view.
    /// </summary>
    private sealed class NoDeltaMux
    {
        public NoDeltaMux(int offset, int max)
        {
            Offset = offset;
            Port.Handler = (method, parameters) =>
            {
                Assert.Equal(Hypa.AgentRuntime.Protocol.ProtocolMethods.PaneScroll, method);
                Offset = Math.Clamp(parameters!["offset"]!.GetValue<int>(), 0, max);
                return MouseTestGeom.Parse($$"""{"pane_id":"p1","offset":{{Offset}},"max_offset":{{max}}}""");
            };
        }

        public MouseRecordingPort Port { get; } = new();

        public int Offset { get; private set; }
    }

    /// <summary>
    /// A mux whose view of the pane is at <paramref name="offset"/> with a
    /// max of <paramref name="max"/>. A <c>delta</c> counts from its own
    /// offset. An absolute offset sets it.
    /// </summary>
    private static MouseRecordingPort MovingMuxPort(int offset, int max, string epoch = "a")
    {
        var port = new MouseRecordingPort();
        var current = offset;
        port.Handler = (method, parameters) =>
        {
            Assert.Equal(Hypa.AgentRuntime.Protocol.ProtocolMethods.PaneScroll, method);
            current = parameters!["delta"] is { } delta
                ? Math.Clamp(current + delta.GetValue<int>(), 0, max)
                : Math.Clamp(parameters["offset"]!.GetValue<int>(), 0, max);
            return MouseTestGeom.Parse(
                $$"""{"pane_id":"p1","offset":{{current}},"max_offset":{{max}},"revision":{{port.Calls.Count + 1}},"revision_epoch":"{{epoch}}"}""");
        };
        return port;
    }

    /// <summary>A mux that answers each pane.scroll with the requested offset and a max of <paramref name="max"/>.</summary>
    private static MouseRecordingPort FreshMaxPort(int max)
    {
        var port = new MouseRecordingPort();
        port.Handler = (method, parameters) =>
        {
            Assert.Equal(Hypa.AgentRuntime.Protocol.ProtocolMethods.PaneScroll, method);
            var offset = parameters!["offset"]!.GetValue<int>();
            return MouseTestGeom.Parse(
                $$"""{"pane_id":"p1","offset":{{offset}},"max_offset":{{max}},"revision":{{port.Calls.Count + 1}},"revision_epoch":"a"}""");
        };
        return port;
    }

    private static bool Apply(AttachLiveState live, JsonElement payload) =>
        AttachSession.TryApplyPaneScrollChanged(live, payload);

    private static int Offset(AttachLiveState live)
    {
        Assert.True(live.TryGetPaneScrollMetrics("p1", out var offset, out _, out _));
        return offset;
    }

    private static JsonElement Scroll(string paneId, int offset, int max, long? revision, string? epoch)
    {
        var payload = new JsonObject { ["pane_id"] = paneId, ["offset"] = offset, ["max_offset"] = max };
        if (revision is { } value)
            payload["revision"] = value;
        if (epoch is not null)
            payload["revision_epoch"] = epoch;
        using var doc = JsonDocument.Parse(payload.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>The glyph and the style of each host cell of a gutter.</summary>
    private static string ThumbRows(AttachLiveState live, CellRect gutter)
    {
        var cells = new List<string>();
        for (var r = gutter.Row; r < gutter.EndRow; r++)
        {
            var cell = live.Host.CellAt(gutter.Col, r);
            cells.Add(cell.Text + ":" + cell.Style);
        }

        return string.Join("|", cells);
    }

    private static TerminalRenderCellsPayload Full(string paneId, CellRect box, long generation, string text) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = paneId,
        Full = true,
        GridCols = box.Cols,
        GridRows = box.Rows,
        Generation = generation,
        Rows = [new TerminalRenderCellRow { I = 0, T = text }],
        Cursor = new TerminalRenderCursorPayload { Col = text.Length, Row = 0, Visible = true },
    };

    private static TerminalRenderCellsPayload Delta(
        string paneId,
        CellRect box,
        long baseGeneration,
        long generation,
        int row,
        string text) => new()
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = paneId,
            Full = false,
            GridCols = box.Cols,
            GridRows = box.Rows,
            BaseGeneration = baseGeneration,
            Generation = generation,
            Rows = [new TerminalRenderCellRow { I = row, T = text }],
            Cursor = new TerminalRenderCursorPayload { Col = text.Length, Row = row, Visible = true },
        };

    private static AttachUiConfig ScrollUi(bool borders = true) => new()
    {
        PaneBorders = borders,
        PaneOuterBorders = borders,
        PaneScrollbars = true,
        HideTabBarWhenSingleTab = true,
        TabBarRight = [],
        MobileWidthThreshold = 0,
    };

    private static LayoutNodeDto SplitRoot() => MouseTestGeom.RightSplit();

    private static AttachLiveState NewLive(bool desktopTheme, bool borders = true)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(ScrollUi(borders))),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new MouseRecordingPort(), "w1", "t1", "p1", "lease-r"),
            Ui = ScrollUi(borders),
            ChromeEnabled = true,
            PaneId = "p1",
            SessionName = "scroll",
        };
        if (desktopTheme)
            live.Theme = ThemeRuntime.FromConfig(new AttachThemeConfig { Name = "borland" }).Value;
        live.ChromeSeed = new ChromeComputeSeed(SplitRoot(), false, null, "p1", Cols, Rows);
        live.Chrome = AttachSession.ComputeLiveChrome(live, Cols, Rows, SplitRoot(), false, null, "p1");
        return live;
    }

    /// <summary>
    /// Two attaches that get the same input. <see cref="Patch"/> uses the
    /// patch path. <see cref="Full"/> composes the full redraw of each paint.
    /// </summary>
    private sealed class Pair : IDisposable
    {
        private readonly UnixRawTerminal _patchTty = new(new MemoryStream(), Cols, Rows);
        private readonly UnixRawTerminal _fullTty = new(new MemoryStream(), Cols, Rows);

        public Pair(bool desktopTheme, bool borders = true)
        {
            Patch = NewLive(desktopTheme, borders);
            Full = NewLive(desktopTheme, borders);
            Full.PatchPaintEnabled = false;
        }

        public AttachLiveState Patch { get; }

        public AttachLiveState Full { get; }

        public CellRect Box(string paneId) => Patch.Chrome!.Panes.Single(p => p.PaneId == paneId).Content;

        public void Scroll(string paneId, int offset, int max)
        {
            var payload = ScrollEventPatchPathTests.Scroll(paneId, offset, max, revision: null, epoch: null);
            Assert.True(AttachSession.TryApplyPaneScrollChanged(Patch, payload, _patchTty));
            Assert.True(AttachSession.TryApplyPaneScrollChanged(Full, payload, _fullTty));
        }

        public void Feed(TerminalRenderCellsPayload cells)
        {
            var patched = AttachSession.TryWriteCells(_patchTty, Patch, cells, reanchorBeforePaint: true);
            var full = AttachSession.TryWriteCells(_fullTty, Full, cells, reanchorBeforePaint: true);
            Assert.Equal(full, patched);
            AssertSame();
        }

        private void AssertSame()
        {
            Assert.Equal(Full.Host.Cols, Patch.Host.Cols);
            Assert.Equal(Full.Host.Rows, Patch.Host.Rows);
            for (var r = 0; r < Full.Host.Rows; r++)
            {
                for (var c = 0; c < Full.Host.Cols; c++)
                {
                    var want = Full.Host.CellAt(c, r);
                    var got = Patch.Host.CellAt(c, r);
                    Assert.True(
                        HostFrame.CellsEqual(want, got),
                        $"host cell {c},{r}: full '{want.Text}' {want.Style} patch '{got.Text}' {got.Style}");
                    Assert.Equal(
                        Full.HostEncoder.TryGetKeptCell(c, r, out var keptWant),
                        Patch.HostEncoder.TryGetKeptCell(c, r, out var keptGot));
                    Assert.True(
                        HostFrame.CellsEqual(keptWant, keptGot),
                        $"kept cell {c},{r}: full '{keptWant.Text}' patch '{keptGot.Text}'");
                }
            }

            Assert.Equal(Full.Host.Cursor, Patch.Host.Cursor);
        }

        public void Dispose()
        {
            _patchTty.Dispose();
            _fullTty.Dispose();
        }
    }
}
