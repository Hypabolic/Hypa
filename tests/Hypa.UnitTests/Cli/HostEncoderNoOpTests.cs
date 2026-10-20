using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// A host paint that changes no cell and not the cursor writes nothing to
/// the host terminal, and it still runs the accepted-write work.
/// </summary>
public sealed class HostEncoderNoOpTests
{
    [Fact]
    public void Second_encode_of_the_same_frame_and_cursor_is_a_no_op_and_writes_nothing()
    {
        var output = new MemoryStream();
        using var tty = new UnixRawTerminal(output, 20, 4);
        var encoder = new HostBlitEncoder();
        var host = Frame("$ ls", new HostCursor(4, 0, Visible: true, Shape: 2));

        var first = encoder.Encode(host);
        Assert.False(first.NoOp);
        Assert.True(tty.WriteHostEncoded(first.Span, onAccepted: () => encoder.Commit(host, first), noOp: first.NoOp));
        var mark = output.Length;

        var second = encoder.Encode(host);
        Assert.True(second.NoOp);
        var accepted = false;
        Assert.True(tty.WriteHostEncoded(
            second.Span,
            onAccepted: () =>
            {
                encoder.Commit(host, second);
                accepted = true;
            },
            noOp: second.NoOp));
        Assert.True(accepted);
        Assert.Equal(mark, output.Length);
    }

    [Fact]
    public void No_op_paint_writes_its_cursor_bytes_after_another_writer_wrote()
    {
        var output = new MemoryStream();
        using var tty = new UnixRawTerminal(output, 20, 4);
        var encoder = new HostBlitEncoder();
        var host = Frame("$ ls", new HostCursor(4, 0, Visible: true));
        var first = encoder.Encode(host);
        Assert.True(tty.WriteHostEncoded(first.Span, onAccepted: () => encoder.Commit(host, first)));

        // A graphics or mode write can move the host cursor.
        tty.WriteBytes("\u001b[10;10H"u8);
        var mark = output.Length;
        var second = encoder.Encode(host);
        Assert.True(second.NoOp);
        Assert.True(tty.WriteHostEncoded(second.Span, onAccepted: () => encoder.Commit(host, second), noOp: true));
        var written = Encoding.UTF8.GetString(output.ToArray(), (int)mark, (int)(output.Length - mark));
        Assert.Contains("\u001b[1;5H", written, StringComparison.Ordinal);
    }

    public static TheoryData<string> CursorChanges() => ["position", "visibility", "shape", "presence"];

    [Theory]
    [MemberData(nameof(CursorChanges))]
    public void Cursor_only_change_is_not_a_no_op(string change)
    {
        var output = new MemoryStream();
        using var tty = new UnixRawTerminal(output, 20, 4);
        var encoder = new HostBlitEncoder();
        var cursor = new HostCursor(4, 0, Visible: true, Shape: 2);
        var host = Frame("$ ls", cursor);
        var first = encoder.Encode(host);
        Assert.True(tty.WriteHostEncoded(first.Span, onAccepted: () => encoder.Commit(host, first)));
        var mark = output.Length;

        host.Cursor = change switch
        {
            "position" => cursor with { Col = 3 },
            "visibility" => cursor with { Visible = false },
            "shape" => cursor with { Shape = 6 },
            _ => HostCursor.None,
        };
        var second = encoder.Encode(host);
        Assert.False(second.NoOp);
        Assert.True(tty.WriteHostEncoded(second.Span, onAccepted: () => encoder.Commit(host, second), noOp: second.NoOp));
        Assert.True(output.Length > mark);
    }

    [Fact]
    public void No_op_cells_paint_runs_the_accepted_write_work()
    {
        var output = new MemoryStream();
        using var tty = new UnixRawTerminal(output, 40, 6);
        var live = NewLive();
        var box = new CellRect(0, 0, 40, 6);
        Assert.True(AttachSession.TryWriteCells(tty, live, Cells(box, full: true, 0, 1, "$ ls"), reanchorBeforePaint: true));

        // The chrome queued before the paint commits only on an accepted
        // write. AbortChrome would drop it.
        live.QueueChrome("chrome-after-noop");
        live.WindowTitleOverride = "noop title";
        live.Ui = live.Ui with { HostStatus = HostStatusMode.On };
        live.SessionStatus = new SessionProgramStatusView
        {
            State = ProgramStatusState.Working,
            PaneId = "p1",
            Msg = "building",
            Source = new ProgramStatusSourceLabel("w1", "t1", "p1"),
        };
        var mark = output.Length;
        var noOp = Cells(box, full: false, 1, 2, rows: []);
        Assert.True(AttachSession.TryWriteCells(tty, live, noOp, reanchorBeforePaint: true));

        Assert.Equal(1, live.Latency.Paint.NoOps);
        Assert.Equal(2, live.LastAppliedBlit("p1").Generation);
        Assert.True(live.ChromeUnchanged("chrome-after-noop"));
        live.TryGetPaneFrame("p1", out var shown);
        Assert.True(live.HasNewerPainted(shown! with { Generation = 1 }));
        var written = Encoding.UTF8.GetString(output.ToArray(), (int)mark, (int)(output.Length - mark));
        Assert.DoesNotContain(HostBlitEncoder.SyncBegin, written, StringComparison.Ordinal);
        Assert.Contains("noop title", written, StringComparison.Ordinal);
        Assert.Contains(HostStatusReport.Encode(live.SessionStatus), written, StringComparison.Ordinal);
    }

    [Fact]
    public void Repaint_request_after_a_full_encode_stays_pending_after_its_commit()
    {
        var encoder = new HostBlitEncoder();
        var host = Frame("$ ls", new HostCursor(4, 0, Visible: true));
        var first = encoder.Encode(host);
        encoder.Commit(host, first);
        encoder.RequestRepaint();

        var repaint = encoder.Encode(host);
        Assert.True(repaint.Full);
        encoder.RequestRepaint();
        encoder.Commit(host, repaint);
        Assert.True(encoder.RepaintPending);

        var again = encoder.Encode(host);
        Assert.True(again.Full);
        encoder.Commit(host, again);
        Assert.False(encoder.RepaintPending);
        Assert.False(encoder.Encode(host).Full);
    }

    [Fact]
    public void Failed_accept_with_a_no_op_retry_is_accepted()
    {
        var output = new MemoryStream();
        using var tty = new UnixRawTerminal(output, 40, 6);
        var live = NewLive();
        var box = new CellRect(0, 0, 40, 6);
        Assert.True(AttachSession.TryWriteCells(tty, live, Cells(box, full: true, 0, 1, "$ ls"), reanchorBeforePaint: true));

        // A newer stored frame with the same cells makes the first accept
        // fail. The retry composes that frame, and it changes nothing.
        tty.BeforeComposedWrite = () =>
        {
            tty.BeforeComposedWrite = null;
            live.TryGetPaneFrame("p1", out var stored);
            live.SetPaneFrame(stored! with { Generation = 9 });
        };
        live.QueueChrome("chrome-after-retry");
        var mark = output.Length;
        AttachSession.ApplyOverlayPaint(tty, live, restoreFrame: true);

        Assert.Equal(1, tty.RecoveredComposeCount);
        Assert.Equal(1, live.Latency.Paint.NoOps);
        Assert.True(live.ChromeUnchanged("chrome-after-retry"));
        var written = Encoding.UTF8.GetString(output.ToArray(), (int)mark, (int)(output.Length - mark));
        Assert.DoesNotContain(HostBlitEncoder.SyncBegin, written, StringComparison.Ordinal);
    }

    private static HostFrame Frame(string text, HostCursor cursor)
    {
        var host = new HostFrame();
        host.Resize(20, 4);
        host.StampGlyphRun(0, 0, text, AssembledStyle.Default);
        host.Cursor = cursor;
        return host;
    }

    private static TerminalRenderCellsPayload Cells(
        CellRect box,
        bool full,
        long baseGeneration,
        long generation,
        string? text = null,
        TerminalRenderCellRow[]? rows = null) => new()
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p1",
            Full = full,
            GridCols = box.Cols,
            GridRows = box.Rows,
            BaseGeneration = baseGeneration,
            Generation = generation,
            Rows = rows ?? (text is null ? [] : [new TerminalRenderCellRow { I = 0, T = text }]),
            Cursor = full ? new TerminalRenderCursorPayload { Col = 4, Row = 0, Visible = true } : null,
        };

    private static AttachLiveState NewLive()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new MouseRecordingPort(), "w1", "t1", "p1", "lease-r"),
            PaneId = "p1",
            SessionName = "noop",
        };
    }
}
