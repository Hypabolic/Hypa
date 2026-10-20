using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Application cursor and bracketed paste travel on snapshot modes and on
/// every cells body. The host TTY stays in normal cursor mode.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class CursorPasteModeTests
{
    private readonly string? _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public void Snapshot_modes_follow_application_cursor_and_bracketed_paste()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        using var vt = new GhosttyVtEngine(8, 3, libraryPathOverride: _lib);
        vt.Feed("\u001b[?1h"u8);
        Assert.True(vt.CaptureSnapshot().Modes.ApplicationCursor);
        var on = Pack(vt.CaptureSnapshot());
        Assert.Contains("\"application_cursor\":true", on, StringComparison.Ordinal);

        vt.Feed("\u001b[?1l"u8);
        Assert.False(vt.CaptureSnapshot().Modes.ApplicationCursor);
        var off = Pack(vt.CaptureSnapshot());
        Assert.DoesNotContain("application_cursor", off, StringComparison.Ordinal);

        vt.Feed("\u001b[?1h\u001b[?2004h"u8);
        var both = vt.CaptureSnapshot();
        Assert.True(both.Modes.ApplicationCursor);
        Assert.True(both.Modes.BracketedPaste);
        var json = JsonSerializer.Serialize(both, VtSnapshotWireJsonContext.Default.VtStructuredSnapshot);
        var parsed = AttachSnapshotPacker.ParseSnapshotJson("pane_1", json);
        Assert.True(parsed.Frame.Modes.ApplicationCursor);
        Assert.True(parsed.Frame.Modes.BracketedPaste);
        var packed = Pack(both);
        Assert.Contains("\"application_cursor\":true", packed, StringComparison.Ordinal);
        Assert.Contains("\"bracketed_paste\":true", packed, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("\u001b[?1h", "\u001b[?1l", "application_cursor")]
    [InlineData("\u001b[?2004h", "\u001b[?2004l", "bracketed_paste")]
    public async Task Mode_only_delta_states_the_current_flag(string on, string off, string field)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests(on);
        await session.PaintAsync();
        var delta = Assert.Single(session.CellsSince(mark));
        Assert.False(delta.GetProperty("full").GetBoolean());
        Assert.Equal(0, delta.GetProperty("rows").GetArrayLength());
        Assert.True(delta.GetProperty(field).GetBoolean());

        mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests(off);
        await session.PaintAsync();
        var cleared = Assert.Single(session.CellsSince(mark));
        Assert.False(cleared.TryGetProperty(field, out var flag) && flag.ValueKind == JsonValueKind.True);
    }

    [SkippableTheory]
    [InlineData("\u001b[?1h", "application_cursor")]
    [InlineData("\u001b[?2004h", "bracketed_paste")]
    public async Task Text_and_mode_share_one_delta(string on, string field)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("Q" + on);
        await session.PaintAsync();
        var delta = Assert.Single(session.CellsSince(mark));
        Assert.False(delta.GetProperty("full").GetBoolean());
        Assert.True(delta.GetProperty("rows").GetArrayLength() > 0);
        Assert.True(delta.GetProperty(field).GetBoolean());
        Assert.Contains("Q", delta.GetRawText(), StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("\u001b[?1h", "application_cursor")]
    [InlineData("\u001b[?2004h", "bracketed_paste")]
    public async Task Dropped_mode_next_body_is_full_reanchor(string on, string field)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        session.Sink.MaxLineBytes = 256;
        session.Runtime.PublishFedOutputForTests(on);
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));

        session.Sink.MaxLineBytes = 1_048_576;
        session.Runtime.PublishFedOutputForTests(on);
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.True(body.GetProperty("full").GetBoolean());
        Assert.True(body.GetProperty("reanchor").GetBoolean());
        Assert.True(body.GetProperty(field).GetBoolean());
    }

    [SkippableTheory]
    [InlineData("\u001b[?1h", "application_cursor")]
    [InlineData("\u001b[?2004h", "bracketed_paste")]
    public async Task Dropped_text_then_mode_sends_the_current_grid(string on, string field)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        await session.Runtime.ResizeAsync(220, 3, CancellationToken.None);
        var mark = session.Sink.Count;
        session.Sink.MaxLineBytes = 256;
        session.Runtime.PublishFedOutputForTests("GRIDMARK");
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));

        session.Sink.MaxLineBytes = 1_048_576;
        session.Runtime.PublishFedOutputForTests(on);
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.True(body.GetProperty("full").GetBoolean());
        Assert.True(body.GetProperty("reanchor").GetBoolean());
        Assert.True(body.GetProperty(field).GetBoolean());
        Assert.Contains("GRIDMARK", body.GetRawText(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Reattach_first_render_carries_application_cursor()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        await session.Cp.DispatchAsync(
            ProtocolMethods.EventsUnsubscribe,
            Json(new JsonObject { ["subscription_id"] = session.SubId }),
            session.Sink,
            CancellationToken.None);
        session.Runtime.FeedVtForTests("\u001b[?1h");

        var again = await session.AttachAgainAsync();
        var first = again.RendersSince(again.ObserveMark).FirstOrDefault();
        Assert.True(first.ValueKind == JsonValueKind.Object, again.DumpSince(again.ObserveMark));
        Assert.True(HasFlag(first, "application_cursor"), first.GetRawText());
    }

    [SkippableFact]
    public async Task Reanchor_marked_after_the_first_sample_encodes_a_full_body()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("Z");
        await session.PaintAsync();

        session.Cp.BeforeCleanReanchorDecisionForTests = paneId =>
            session.Cp.MarkLiveObserversReanchorForTests(paneId);
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?1h");
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.True(body.GetProperty("full").GetBoolean());
        Assert.True(body.GetProperty("reanchor").GetBoolean());
        Assert.True(body.GetProperty("application_cursor").GetBoolean());
        Assert.Contains("Z", body.GetRawText(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Reanchor_mark_during_admission_survives_the_delta_commit()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Cp.BeforeLiveCellsAdmitForTests = paneId =>
        {
            session.Cp.BeforeLiveCellsAdmitForTests = null;
            session.Cp.MarkLiveObserversReanchorForTests(paneId);
        };
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("Q");
        await session.PaintAsync();
        var delta = Assert.Single(session.CellsSince(mark));
        Assert.False(delta.GetProperty("full").GetBoolean());
        Assert.Contains("Q", delta.GetRawText(), StringComparison.Ordinal);
        Assert.True(session.Cp.ObserverReanchorPendingForTests(session.SubId, session.PaneId));

        mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?1h");
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.True(body.GetProperty("full").GetBoolean());
        Assert.True(body.GetProperty("reanchor").GetBoolean());
        Assert.True(body.GetProperty("application_cursor").GetBoolean());
        Assert.Contains("Q", body.GetRawText(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Pending_observer_receives_a_full_frame_when_another_has_a_mode_change()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("Z");
        await session.PaintAsync();
        var other = await session.AddObserverAsync("cursor-mode-b");

        other.Sink.FailNextRenders = 1;
        var held = session.Sink.Count;
        var otherHeld = other.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?1h");
        await session.PaintAsync();
        var admitted = Assert.Single(session.CellsSince(held));
        Assert.False(admitted.GetProperty("full").GetBoolean());
        Assert.True(admitted.GetProperty("application_cursor").GetBoolean());
        Assert.Empty(CellsOn(other.Sink, otherHeld));

        session.Cp.MarkObserverReanchorForTests(session.SubId, session.PaneId);
        held = session.Sink.Count;
        otherHeld = other.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?1h");
        await session.PaintAsync();

        var full = Assert.Single(session.CellsSince(held));
        Assert.True(full.GetProperty("full").GetBoolean());
        Assert.True(full.GetProperty("reanchor").GetBoolean());
        Assert.True(full.GetProperty("application_cursor").GetBoolean());
        Assert.Contains("Z", full.GetRawText(), StringComparison.Ordinal);

        var delta = Assert.Single(CellsOn(other.Sink, otherHeld));
        Assert.False(delta.GetProperty("full").GetBoolean());
        Assert.True(delta.GetProperty("application_cursor").GetBoolean());
    }

    [SkippableFact]
    public async Task Sync_pair_emits_no_mode_only_body()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?2026h");
        await session.PaintAsync();
        session.Runtime.PublishFedOutputForTests("\u001b[?2026l");
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));
    }

    [SkippableFact]
    public async Task Live_flush_does_not_post_a_mid_sync_frame()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ");
        await session.PaintAsync();

        // The clock does not move, so this request falls inside the render
        // tick and queues a deferred live paint.
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("abc");
        // A PTY read ends inside a block that clears the input line. This
        // feed raises no output, so only the deferred paint can capture it.
        session.Runtime.FeedVtForTests("\u001b[?2026h\r\u001b[2K");
        await session.PaintAsync();
        Assert.DoesNotContain(
            session.CellsSince(mark),
            body => body.TryGetProperty("sync", out var sync) && sync.GetBoolean());
    }

    [SkippableFact]
    public async Task Echo_paints_at_once_while_the_pty_write_is_still_in_progress()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var pty = new HeldInputPtyFactory(TestPaneFactories.ProcessIo());
        await using var session = await OpenAsync(ptyFactory: pty);
        session.Runtime.PublishFedOutputForTests("$ ");
        await session.PaintAsync();

        var claim = await session.Cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            Json(new JsonObject { ["pane_id"] = session.PaneId, ["scope"] = "input" }),
            session.Sink,
            CancellationToken.None);
        await session.Cp.DispatchAsync(
            ProtocolMethods.PaneSendKeys,
            Json(new JsonObject
            {
                ["pane_id"] = session.PaneId,
                ["lease_id"] = claim.GetProperty("lease_id").GetString(),
                ["encoding"] = "utf8",
                ["data"] = "a",
            }),
            session.Sink,
            CancellationToken.None);
        await Poll.GuardAsync(pty.WriteStarted, "PTY write start");

        // The clock does not move, so without the echo window this request
        // would wait for the render tick. The PTY write has not completed.
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("a");
        await session.Cp.WaitOutputDeliveredForTestsAsync(session.PaneId);
        var body = Assert.Single(session.CellsSince(mark));
        Assert.Contains("a", body.GetProperty("rows").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(3, body.GetProperty("cursor").GetProperty("col").GetInt32());
        Assert.False(pty.WriteStarted.IsFaulted);
    }

    [SkippableFact]
    public async Task Send_keys_that_are_not_keyboard_input_open_no_echo_window()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var pty = new HeldInputPtyFactory(TestPaneFactories.ProcessIo());
        await using var session = await OpenAsync(ptyFactory: pty);
        session.Runtime.PublishFedOutputForTests("$ ");
        await session.PaintAsync();

        var claim = await session.Cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            Json(new JsonObject { ["pane_id"] = session.PaneId, ["scope"] = "input" }),
            session.Sink,
            CancellationToken.None);
        // A mouse report. The bytes still reach the PTY.
        await session.Cp.DispatchAsync(
            ProtocolMethods.PaneSendKeys,
            Json(new JsonObject
            {
                ["pane_id"] = session.PaneId,
                ["lease_id"] = claim.GetProperty("lease_id").GetString(),
                ["encoding"] = "utf8",
                ["data"] = "\u001b[<0;1;1M",
                ["keyboard"] = false,
            }),
            session.Sink,
            CancellationToken.None);
        await Poll.GuardAsync(pty.WriteStarted, "PTY write start");

        // The clock does not move. With no echo window the paint waits for
        // the render tick of the pane.
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("a");
        await session.Cp.WaitOutputDeliveredForTestsAsync(session.PaneId);
        Assert.Empty(session.CellsSince(mark));
    }

    [SkippableFact]
    public async Task Block_opened_after_the_sync_check_still_holds_the_live_paint()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ");
        await session.PaintAsync();

        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("abc");
        // The PTY reader opens the block after the flush read the live sync
        // state and before the capture.
        session.Cp.BeforeLiveCaptureForTests = _ =>
        {
            session.Cp.BeforeLiveCaptureForTests = null;
            session.Runtime.FeedVtForTests("\u001b[?2026h\r\u001b[2K");
        };
        await session.PaintAsync();
        Assert.Null(session.Cp.BeforeLiveCaptureForTests);
        Assert.DoesNotContain(
            session.CellsSince(mark),
            body => body.TryGetProperty("sync", out var sync) && sync.GetBoolean());
    }

    [SkippableFact]
    public async Task Unclosed_sync_block_ends_at_the_timeout_and_paints_without_sync()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ");
        await session.PaintAsync();

        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?2026hX");
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));
        Assert.True(session.Runtime.IsSynchronizedOutputActive);

        // Ghostty resets mode 2026 1 s after a block starts. The mux does the
        // same, so the timeout paint shows the caret again.
        await session.AdvanceAsync(ControlPlaneService.SynchronizedOutputTimeout);
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.Contains("X", body.GetProperty("rows").GetRawText(), StringComparison.Ordinal);
        Assert.False(IsSync(body));
        Assert.True(body.GetProperty("cursor").GetProperty("visible").GetBoolean());
        Assert.False(session.Runtime.IsSynchronizedOutputActive);

        // The block ended. No paint inside it follows.
        mark = session.Sink.Count;
        await session.AdvanceAsync(ControlPlaneService.SynchronizedOutputTimeout);
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));
    }

    [SkippableFact]
    public async Task Sync_block_that_closes_before_the_timeout_posts_one_frame()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ");
        await session.PaintAsync();

        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?2026hX");
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));

        session.Runtime.PublishFedOutputForTests("Y\u001b[?2026l");
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.Contains("XY", body.GetProperty("rows").GetRawText(), StringComparison.Ordinal);
        Assert.False(IsSync(body));

        // The closed block leaves no timeout paint.
        mark = session.Sink.Count;
        await session.AdvanceAsync(ControlPlaneService.SynchronizedOutputTimeout);
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));
        Assert.False(session.Runtime.IsSynchronizedOutputActive);
    }

    [SkippableFact]
    public async Task Block_after_an_ended_block_holds_for_its_own_timeout()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ");
        await session.PaintAsync();
        session.Runtime.PublishFedOutputForTests("\u001b[?2026hX");
        await session.PaintAsync();
        await session.AdvanceAsync(ControlPlaneService.SynchronizedOutputTimeout);
        await session.PaintAsync();
        Assert.False(session.Runtime.IsSynchronizedOutputActive);

        // The next block starts its own timeout. The start of the ended
        // block does not apply to it.
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?2026hZ");
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));
        Assert.True(session.Runtime.IsSynchronizedOutputActive);

        await session.AdvanceAsync(ControlPlaneService.SynchronizedOutputTimeout);
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.Contains("Z", body.GetProperty("rows").GetRawText(), StringComparison.Ordinal);
        Assert.False(IsSync(body));
        Assert.False(session.Runtime.IsSynchronizedOutputActive);
    }

    [SkippableFact]
    public async Task Expired_block_that_only_the_capture_sees_ends_before_a_post()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ");
        await session.PaintAsync();

        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?2026hX");
        await session.PaintAsync();
        // The VT closes the block, and the mux does not see the close. The
        // block start stays.
        session.Runtime.FeedVtForTests("\u001b[?2026l");
        // At the timeout the flush sees no open block. Then the PTY reader
        // opens a block before the capture.
        session.Cp.BeforeLiveCaptureForTests = _ =>
        {
            session.Cp.BeforeLiveCaptureForTests = null;
            session.Runtime.FeedVtForTests("\u001b[?2026hZ");
        };
        await session.AdvanceAsync(ControlPlaneService.SynchronizedOutputTimeout);
        await session.PaintAsync();
        Assert.Null(session.Cp.BeforeLiveCaptureForTests);

        var bodies = session.CellsSince(mark);
        Assert.DoesNotContain(bodies, IsSync);
        Assert.Contains(bodies, body => body.GetProperty("rows").GetRawText().Contains('Z', StringComparison.Ordinal));
        Assert.False(session.Runtime.IsSynchronizedOutputActive);
    }

    [SkippableFact]
    public async Task Attach_inside_an_open_block_of_an_idle_pane_ends_the_block_at_the_timeout()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ");
        await session.PaintAsync();
        // This feed raises no output, so no live paint waits for the block.
        session.Runtime.FeedVtForTests("\u001b[?2026hX");

        await session.AttachAgainAsync();
        var attached = session.CellsSince(session.ObserveMark);
        Assert.NotEmpty(attached);
        Assert.True(IsSync(attached[^1]));

        // The pane stays idle. The attach armed the block timeout.
        var mark = session.Sink.Count;
        await session.AdvanceAsync(ControlPlaneService.SynchronizedOutputTimeout);
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.False(IsSync(body));
        Assert.True(body.GetProperty("cursor").GetProperty("visible").GetBoolean());
        Assert.False(session.Runtime.IsSynchronizedOutputActive);
    }

    [SkippableFact]
    public async Task Pane_ends_a_block_only_at_the_expected_feed_generation()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.FeedVtForTests("\u001b[?2026hX");
        var generation = session.Runtime.FeedGeneration;
        Assert.False(session.Runtime.LastFeedPaintDecision.RequestPaint);

        // A feed came after the caller read the generation: the block stays.
        Assert.False(session.Runtime.TryEndSynchronizedOutput(generation - 1, out var current));
        Assert.Equal(generation, current);
        Assert.True(session.Runtime.IsSynchronizedOutputActive);

        Assert.True(session.Runtime.TryEndSynchronizedOutput(generation, out var ended));
        Assert.Equal(generation + 1, ended);
        Assert.Equal(ended, session.Runtime.FeedGeneration);
        Assert.False(session.Runtime.IsSynchronizedOutputActive);
        Assert.Equal(new PaneFeedPaintDecision(ended, RequestPaint: true), session.Runtime.LastFeedPaintDecision);

        // No open block: nothing changes.
        Assert.False(session.Runtime.TryEndSynchronizedOutput(ended, out _));
        Assert.Equal(ended, session.Runtime.FeedGeneration);
    }

    [SkippableTheory]
    [InlineData("\b", 3, true, 0)]
    [InlineData("\u001b[C", 5, true, 0)]
    [InlineData("\u001b[D", 3, true, 0)]
    [InlineData("\r", 0, true, 0)]
    [InlineData("\u001b[1;3H", 2, true, 0)]
    [InlineData("\u001b[?25l", 4, false, 0)]
    [InlineData("\u001b[5 q", 4, true, 5)]
    public async Task Cursor_only_change_reaches_the_observer(string bytes, int col, bool visible, int shape)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ab");
        await session.PaintAsync();
        var seeded = session.CellsSince(0).Last().GetProperty("cursor");
        Assert.Equal(4, seeded.GetProperty("col").GetInt32());

        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests(bytes);
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.False(body.GetProperty("full").GetBoolean());
        Assert.Equal(0, body.GetProperty("rows").GetArrayLength());
        var cursor = body.GetProperty("cursor");
        Assert.Equal(col, cursor.GetProperty("col").GetInt32());
        Assert.Equal(0, cursor.GetProperty("row").GetInt32());
        Assert.Equal(visible, cursor.GetProperty("visible").GetBoolean());
        var expectedShape = shape == 0 ? seeded.GetProperty("shape").GetInt32() : shape;
        Assert.Equal(expectedShape, cursor.GetProperty("shape").GetInt32());
    }

    [SkippableFact]
    public async Task Cursor_only_change_reaches_an_observer_without_writer_lanes()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(writerLanes: false);
        session.Runtime.PublishFedOutputForTests("$ ab");
        await session.PaintAsync();

        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\b");
        await session.PaintAsync();
        var render = Assert.Single(session.RendersSince(mark));
        Assert.Equal(3, CursorCol(render));

        mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[C\u001b[D");
        await session.PaintAsync();
        Assert.Empty(session.RendersSince(mark));
    }

    [SkippableFact]
    public async Task Alternate_screen_switch_reaches_the_observer()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?1049h");
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.Equal("alt", body.GetProperty("active_screen").GetString());

        mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?1049l");
        await session.PaintAsync();
        body = Assert.Single(session.CellsSince(mark));
        Assert.Equal("main", body.GetProperty("active_screen").GetString());
    }

    [SkippableFact]
    public async Task Cursor_shown_again_reaches_the_observer()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ab\u001b[?25l");
        await session.PaintAsync();

        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?25h");
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.True(body.GetProperty("cursor").GetProperty("visible").GetBoolean());
    }

    [SkippableFact]
    public async Task Mode_change_with_cursor_move_carries_the_new_cursor()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ab");
        await session.PaintAsync();

        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\b\u001b[?1h");
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.Equal(3, body.GetProperty("cursor").GetProperty("col").GetInt32());
        Assert.True(body.GetProperty("application_cursor").GetBoolean());
    }

    [SkippableFact]
    public async Task Delete_over_trailing_space_then_space_moves_the_caret_each_key()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ foo  ");
        await session.PaintAsync();

        // zsh echoes DEL over a trailing space as a bare BS.
        (string Bytes, int Col)[] steps = [("\b", 6), (" ", 7), ("\b", 6), (" ", 7)];
        foreach (var (bytes, col) in steps)
        {
            var mark = session.Sink.Count;
            session.Runtime.PublishFedOutputForTests(bytes);
            await session.PaintAsync();
            var body = Assert.Single(session.CellsSince(mark));
            Assert.Equal(col, body.GetProperty("cursor").GetProperty("col").GetInt32());
        }
    }

    [SkippableFact]
    public async Task Claude_code_space_echo_moves_the_caret()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        await session.Runtime.ResizeAsync(120, 40, CancellationToken.None);
        // A hint on row 34 and a "> " prompt on row 36 (1-based). The caret
        // is after the prompt. The second DEL chunk erases the hint.
        session.Runtime.PublishFedOutputForTests("\u001b[2J\u001b[34;51Hhint\u001b[36;1H> ");
        await session.PaintAsync();

        // Claude Code 2.1.301 echo bytes, one entry for each read. A space at
        // the end of the input and the arrows are cursor moves only.
        (string Key, string Bytes, int Col)[] steps =
        [
            ("a", "\u001b[?25l\u001b[H\r\u001b[2C\u001b[35Ba\u001b[K\u001b[40;1H\u001b[36;4H\u001b[?25h", 3),
            ("b", "\u001b[?25l\u001b[H\r\u001b[3C\u001b[35Bb\u001b[40;1H\u001b[36;5H\u001b[?25h", 4),
            ("space", "\u001b[?25l\u001b[36;6H\u001b[?25h", 5),
            ("c", "\u001b[?25l\u001b[H\r\u001b[5C\u001b[35Bc\u001b[40;1H\u001b[36;7H\u001b[?25h", 6),
            ("space", "\u001b[?25l\u001b[36;8H\u001b[?25h", 7),
            ("space", "\u001b[?25l\u001b[36;9H\u001b[?25h", 8),
            ("del", "\u001b[?25l\u001b[36;8H\u001b[?25h", 7),
            ("del-hint", "\u001b[?25l\u001b[H\r\u001b[50C\u001b[33B\u001b[K\u001b[40;1H\u001b[36;8H\u001b[?25h", 7),
            ("del", "\u001b[?25l\u001b[36;7H\u001b[?25h", 6),
            ("d", "\u001b[?25l\u001b[H\r\u001b[6C\u001b[35Bd\u001b[40;1H\u001b[36;8H\u001b[?25h", 7),
            ("left", "\u001b[?25l\u001b[36;7H\u001b[?25h", 6),
            ("right", "\u001b[?25l\u001b[36;8H\u001b[?25h", 7),
        ];
        foreach (var (key, bytes, col) in steps)
        {
            var mark = session.Sink.Count;
            session.Runtime.PublishFedOutputForTests(bytes);
            await session.PaintAsync();
            var bodies = session.CellsSince(mark);
            Assert.True(bodies.Count == 1, $"{key}: {bodies.Count} cells bodies");
            var cursor = bodies[0].GetProperty("cursor");
            Assert.True(col == cursor.GetProperty("col").GetInt32(), $"{key}: {cursor.GetRawText()}");
            Assert.Equal(35, cursor.GetProperty("row").GetInt32());
            Assert.True(cursor.GetProperty("visible").GetBoolean());
        }
    }

    [SkippableFact]
    public async Task Esu_after_admitted_sync_frame_posts_the_visible_caret()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("X");
        await session.PaintAsync();
        session.Runtime.PublishFedOutputForTests("\u001b[?2026hY");
        await session.PaintAsync();
        // An attach does not hold for an open block. The new observer holds
        // a sync frame.
        await session.AttachAgainAsync();
        var synced = session.CellsSince(session.ObserveMark);
        Assert.NotEmpty(synced);
        Assert.True(IsSync(synced[^1]));

        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?2026l");
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.False(body.TryGetProperty("sync", out var sync) && sync.GetBoolean());
        Assert.Equal(0, body.GetProperty("rows").GetArrayLength());
        Assert.True(body.GetProperty("cursor").GetProperty("visible").GetBoolean());
    }

    [SkippableFact]
    public async Task Clean_paint_with_unchanged_cursor_posts_nothing()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ab");
        await session.PaintAsync();

        var mark = session.Sink.Count;
        // Visible already, and a move to the current cell (row 1, col 5).
        session.Runtime.PublishFedOutputForTests("\u001b[?25h\u001b[1;5H");
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));
    }

    [SkippableFact]
    public async Task Cursor_only_change_reaches_each_observer_once()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ab");
        await session.PaintAsync();
        var other = await session.AddObserverAsync("cursor-mode-b");

        var mark = session.Sink.Count;
        var otherMark = other.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\b");
        await session.PaintAsync();
        var body = Assert.Single(session.CellsSince(mark));
        Assert.Equal(3, body.GetProperty("cursor").GetProperty("col").GetInt32());
        var otherBody = Assert.Single(CellsOn(other.Sink, otherMark));
        Assert.Equal(3, otherBody.GetProperty("cursor").GetProperty("col").GetInt32());

        // A feed that leaves the cursor where both observers hold it.
        mark = session.Sink.Count;
        otherMark = other.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[C\u001b[D");
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));
        Assert.Empty(CellsOn(other.Sink, otherMark));
    }

    [SkippableFact]
    public async Task Observer_that_dropped_a_cursor_move_catches_up_and_the_other_gets_nothing()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("$ ab");
        await session.PaintAsync();
        var other = await session.AddObserverAsync("cursor-mode-b");
        var held = CellsOn(other.Sink, 0).Last().GetProperty("generation").GetInt64();

        // The second observer's lane rejects the cursor-only body.
        other.Sink.FailNextRenders = 1;
        var mark = session.Sink.Count;
        var otherMark = other.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\b");
        await session.PaintAsync();
        Assert.Equal(3, Assert.Single(session.CellsSince(mark)).GetProperty("cursor").GetProperty("col").GetInt32());
        Assert.Empty(CellsOn(other.Sink, otherMark));

        // A feed that leaves the cursor at col 3: only the lagging observer
        // posts, as a delta from the frame it still holds.
        mark = session.Sink.Count;
        otherMark = other.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[C\u001b[D");
        await session.PaintAsync();
        Assert.Empty(session.CellsSince(mark));
        var caughtUp = Assert.Single(CellsOn(other.Sink, otherMark));
        Assert.False(caughtUp.GetProperty("full").GetBoolean());
        Assert.Equal(held, caughtUp.GetProperty("base_generation").GetInt64());
        Assert.Equal(3, caughtUp.GetProperty("cursor").GetProperty("col").GetInt32());
    }

    private async Task<Session> OpenAsync(bool writerLanes = true, IPtyProcessFactory? ptyFactory = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-cursor-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("cursor-mode"));
        state.UpdateSession(s => s with { Name = "cursor-mode", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(dir, label: "default");
        var store = new SqliteRuntimeSessionStore(paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(paths);
        var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var factory = new PaneRuntimeFactory(
            new PaneIntelligencePipeline(),
            ptyFactory: ptyFactory ?? TestPaneFactories.ProcessIo(),
            vtEngineFactory: new VtEngineFactory(new VtProviderSelection
            {
                LibraryPathOverride = _lib,
            }));
        var clock = new ManualTimeProvider();
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            // The fake clock moves only in Session.AdvanceAsync, so the
            // coalescer timer never paints on its own. Otherwise a paint runs
            // only inside Session.PaintAsync.
            timeProvider: clock);
        var sink = new LaneSink("cursor-mode") { UsesWriterLanes = writerLanes };
        var session = new Session(dir, paths, cp, sink, clock);
        try
        {
            var sub = await cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                Json(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["types"] = new JsonArray("output", "render"),
                    ["live"] = true,
                }),
                sink,
                CancellationToken.None);
            session.SubId = sub.GetProperty("subscription_id").GetString()!;
            await cp.CompleteEventsSubscribeAsync(session.SubId, [], sink, CancellationToken.None);
            var create = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject
                {
                    ["command"] = "/bin/cat",
                    ["cwd"] = dir,
                }),
                CancellationToken.None);
            session.PaneId = create.GetProperty("pane_id").GetString()!;
            session.Runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneId));
            await cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Json(new JsonObject
                {
                    ["subscription_id"] = session.SubId,
                    ["pane_ids"] = new JsonArray(session.PaneId),
                }),
                sink,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = session.PaneId,
                    ["subscription_id"] = session.SubId,
                }),
                sink,
                CancellationToken.None);
            // An observer without writer lanes gets no observe frame. Its
            // renders start with the first live paint.
            if (writerLanes)
                Assert.NotEmpty(session.CellsSince(0));
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static string Pack(VtStructuredSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, VtSnapshotWireJsonContext.Default.VtStructuredSnapshot);
        var parsed = AttachSnapshotPacker.ParseSnapshotJson("pane_1", json);
        var parts = AttachSnapshotPacker.Pack(
            "pane_1",
            parsed,
            new DefaultEventPayloadRedactor(),
            generation: 1);
        return string.Concat(parts.Select(part => Encoding.UTF8.GetString(part)));
    }

    private static bool IsSync(JsonElement body) =>
        body.TryGetProperty("sync", out var sync) && sync.GetBoolean();

    private static bool HasFlag(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.True)
            return true;
        return payload.TryGetProperty("modes", out var modes)
            && modes.ValueKind == JsonValueKind.Object
            && modes.TryGetProperty(name, out var nested)
            && nested.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Cursor column of a cells body or of a JSON snapshot render.
    /// </summary>
    private static int CursorCol(JsonElement render)
    {
        if (render.TryGetProperty("cursor", out var cursor))
            return cursor.GetProperty("col").GetInt32();
        if (render.TryGetProperty("snapshot", out var snapshot)
            && snapshot.TryGetProperty("cursor", out var nested))
            return nested.TryGetProperty("col", out var col) ? col.GetInt32() : 0;
        Assert.Fail("no cursor: " + render.GetRawText());
        return -1;
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private static List<JsonElement> CellsOn(LaneSink sink, int lineMark) =>
        PayloadsOn(sink, lineMark, TerminalRenderCellsPayload.KindCells);

    private static List<JsonElement> PayloadsOn(LaneSink sink, int lineMark, string? kind)
    {
        var list = new List<JsonElement>();
        foreach (var line in sink.Lines.Skip(lineMark))
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("params", out var parms))
                continue;
            if (!parms.TryGetProperty("type", out var type)
                || type.GetString() != ProtocolEventTypes.TerminalRender)
                continue;
            if (!parms.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
                continue;
            if (kind is not null
                && (!payload.TryGetProperty("kind", out var payloadKind)
                    || payloadKind.GetString() != kind))
                continue;
            list.Add(payload.Clone());
        }

        return list;
    }

    private sealed record Observer(LaneSink Sink, string SubId);

    private sealed class Session : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly RuntimeStatePaths _paths;

        public Session(
            string dir,
            RuntimeStatePaths paths,
            ControlPlaneService cp,
            LaneSink sink,
            ManualTimeProvider clock)
        {
            _dir = dir;
            _paths = paths;
            Cp = cp;
            Sink = sink;
            Clock = clock;
        }

        public ManualTimeProvider Clock { get; }

        public ControlPlaneService Cp { get; }

        public LaneSink Sink { get; private set; }

        public PaneRuntime Runtime { get; set; } = null!;

        public string PaneId { get; set; } = "";

        public string SubId { get; set; } = "";

        public int ObserveMark { get; private set; }

        public List<JsonElement> RendersSince(int lineMark) =>
            PayloadsSince(lineMark, kind: null);

        public string DumpSince(int lineMark) =>
            string.Join('\n', Sink.Lines.Skip(lineMark));

        public List<JsonElement> CellsSince(int lineMark) =>
            CellsOn(Sink, lineMark);

        /// <summary>
        /// Paint the pending frame, then wait until the hub has delivered it to
        /// every sink. The hub applies the sink line cap when it delivers, so a
        /// test that changes <see cref="LaneSink.MaxLineBytes"/> does so only
        /// after this call. The output emit can queue one more paint, so the
        /// flush runs again after the emit ends.
        /// </summary>
        public async Task PaintAsync()
        {
            await Poll.GuardAsync(Cp.FlushCoalescedPaintForTestsAsync(PaneId), "paint flush");
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
            await Poll.GuardAsync(Cp.FlushCoalescedPaintForTestsAsync(PaneId), "paint flush");
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
        }

        /// <summary>
        /// Move the clock. Wait for the paint of a coalescer tick that falls
        /// inside the move, and for its delivery.
        /// </summary>
        public async Task AdvanceAsync(TimeSpan by)
        {
            Clock.Advance(by);
            await Poll.GuardAsync(Cp.CoalescerTickFlushedForTestsAsync(), "coalescer tick");
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
        }

        public async Task<Observer> AddObserverAsync(string connectionId)
        {
            var sink = new LaneSink(connectionId);
            var sub = await Cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                Json(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["types"] = new JsonArray("output", "render"),
                    ["live"] = true,
                }),
                sink,
                CancellationToken.None);
            var subId = sub.GetProperty("subscription_id").GetString()!;
            await Cp.CompleteEventsSubscribeAsync(subId, [], sink, CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Json(new JsonObject
                {
                    ["subscription_id"] = subId,
                    ["pane_ids"] = new JsonArray(PaneId),
                }),
                sink,
                CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["subscription_id"] = subId,
                }),
                sink,
                CancellationToken.None);
            return new Observer(sink, subId);
        }

        private List<JsonElement> PayloadsSince(int lineMark, string? kind) =>
            PayloadsOn(Sink, lineMark, kind);

        public async Task<Session> AttachAgainAsync()
        {
            var sink = new LaneSink("cursor-mode-2");
            Sink = sink;
            var sub = await Cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                Json(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["types"] = new JsonArray("output", "render"),
                    ["live"] = true,
                }),
                sink,
                CancellationToken.None);
            var subId = sub.GetProperty("subscription_id").GetString()!;
            await Cp.CompleteEventsSubscribeAsync(subId, [], sink, CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Json(new JsonObject
                {
                    ["subscription_id"] = subId,
                    ["pane_ids"] = new JsonArray(PaneId),
                }),
                sink,
                CancellationToken.None);
            ObserveMark = Sink.Count;
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["subscription_id"] = subId,
                }),
                sink,
                CancellationToken.None);
            SubId = subId;
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            await Cp.ShutdownAsync(CancellationToken.None);
            SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
        }
    }

    /// <summary>
    /// A process-io PTY whose input write never completes until the pane
    /// closes. The child still runs and its output still reaches the pane.
    /// </summary>
    private sealed class HeldInputPtyFactory(IPtyProcessFactory inner) : IPtyProcessFactory
    {
        private readonly TaskCompletionSource _writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteStarted => _writeStarted.Task;

        public PtyProviderOptions Options => inner.Options;

        public IPtyProcess Spawn(
            string fileName,
            IReadOnlyList<string> args,
            string cwd,
            int cols,
            int rows,
            IReadOnlyDictionary<string, string>? env = null) =>
            new HeldInputPty(inner.Spawn(fileName, args, cwd, cols, rows, env), _writeStarted);
    }

    private sealed class HeldInputPty(IPtyProcess inner, TaskCompletionSource writeStarted) : IPtyProcess
    {
        private readonly HeldInputStream _input = new(writeStarted);

        public int Pid => inner.Pid;

        public bool IsRunning => inner.IsRunning;

        public int? ExitCode => inner.ExitCode;

        public Stream StandardInput => _input;

        public Stream StandardOutput => inner.StandardOutput;

        public void Resize(int cols, int rows) => inner.Resize(cols, rows);

        public Task WaitForExitAsync(CancellationToken ct) => inner.WaitForExitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            _input.Dispose();
            await inner.DisposeAsync();
        }
    }

    private sealed class HeldInputStream(TaskCompletionSource writeStarted) : Stream, IPtyInputProgress
    {
        private readonly CancellationTokenSource _closed = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async ValueTask<int> WriteAcceptedAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            writeStarted.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
            await Task.Delay(Timeout.Infinite, linked.Token);
            return 0;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed.IsCancellationRequested)
                _closed.Cancel();
            base.Dispose(disposing);
        }
    }

    private sealed class LaneSink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId { get; } = id;

        public bool UsesWriterLanes { get; init; } = true;

        public int MaxLineBytes { get; set; } = 1_048_576;

        public int FailNextRenders { get; set; }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _lines.Count;
            }
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            Note(jsonLine);
            return Task.CompletedTask;
        }

        public WriterLaneResult TryEnqueueRender(string jsonLine) => Admit(jsonLine);

        public WriterLaneResult TryEnqueueRender(byte[] utf8Json) =>
            Admit(Encoding.UTF8.GetString(utf8Json));

        public WriterLaneResult EnqueueOrderedRender(string jsonLine) => Admit(jsonLine);

        public WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) =>
            Admit(Encoding.UTF8.GetString(utf8Json));

        private WriterLaneResult Admit(string json)
        {
            lock (_gate)
            {
                if (FailNextRenders > 0)
                {
                    FailNextRenders--;
                    return WriterLaneResult.TooLarge;
                }

                _lines.Add(json);
                return WriterLaneResult.Ok;
            }
        }

        private void Note(string json)
        {
            lock (_gate)
                _lines.Add(json);
        }
    }
}
