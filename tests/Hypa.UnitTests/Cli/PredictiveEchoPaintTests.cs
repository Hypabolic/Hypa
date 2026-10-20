using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Latency;
using Hypa.Cli.Attach.Prediction;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The attach client paints shown predictions into the one host-size frame,
/// through the patch path, with one host write and no RPC.
/// </summary>
public sealed class PredictiveEchoPaintTests
{
    private const string Pane = "p1";
    private const int Cols = 80;
    private const int Rows = 24;

    private readonly ManualTimeProvider _time = new();
    private long _seq;

    [Fact]
    public void A_shown_prediction_paints_one_patch_with_the_glyph_and_the_caret()
    {
        var (tty, output, live) = Open();
        using var _ = tty;
        Confirm(tty, live, "$ ", 'a');

        var mark = output.Length;
        var patches = live.Latency.Paint.Patches;
        var composes = live.Latency.Paint.FullComposes;
        Type(tty, live, "b");

        Assert.Equal("$ ab", HostRow(live, 0));
        Assert.Equal(new HostCursor(4, 0, true), live.Host.Cursor with { Shape = 0 });
        Assert.Equal(patches + 1, live.Latency.Paint.Patches);
        Assert.Equal(composes, live.Latency.Paint.FullComposes);
        Assert.Equal(1, CountWrites(output, mark));
        Assert.Equal(1, live.Latency.Report().Hops["predicted-paint"].Count);
        Assert.Equal(1, live.Latency.Report().PredictedCellsShown);
    }

    [Fact]
    public void A_tentative_key_paints_nothing()
    {
        var (tty, output, live) = Open();
        using var _ = tty;
        Apply(tty, live, Full("$ "));
        var mark = output.Length;
        Type(tty, live, "a");
        Assert.Equal(0, CountWrites(output, mark));
        Assert.Equal("$", HostRow(live, 0));
    }

    [Fact]
    public void The_confirming_frame_replaces_the_prediction_with_the_same_cells()
    {
        var (tty, _, live) = Open();
        using var __ = tty;
        Confirm(tty, live, "$ ", 'a');
        Type(tty, live, "b");
        var predicted = live.Host.CellAt(3, 0);

        Apply(tty, live, Delta("$ ab") with { InputAck = _seq });
        Assert.Equal(predicted, live.Host.CellAt(3, 0));
        Assert.Equal(0, live.Prediction.Stats().Pending);
        Assert.Null(live.PaintedPrediction);
    }

    [Fact]
    public void A_mismatch_restores_the_pane_cells_when_the_judge_timer_fires()
    {
        var (tty, _, live) = Open();
        using var __ = tty;
        Confirm(tty, live, "$ ", 'a');
        Type(tty, live, "b");

        // The application echoed X. The ack is not 50 ms old yet.
        Apply(tty, live, Delta("$ aX") with { InputAck = _seq });
        Assert.Equal("$ ab", HostRow(live, 0));

        _time.Advance(EchoPredictor.JudgeDelay);
        Assert.Equal("$ aX", HostRow(live, 0));
        Assert.Equal(new HostCursor(4, 0, true), live.Host.Cursor with { Shape = 0 });
        Assert.Equal(1, live.Latency.Report().MispredictedCellsShown);
        Assert.Null(live.PaintedPrediction);
    }

    [Fact]
    public void A_cells_delta_on_another_row_keeps_the_predicted_cell_on_the_host()
    {
        var (tty, _, live) = Open();
        using var __ = tty;
        Confirm(tty, live, "$ ", 'a');
        Type(tty, live, "b");
        Apply(tty, live, Delta("$ a", row: 5, rowText: "hint") with { InputAck = null });
        Assert.Equal("$ ab", HostRow(live, 0));
        Assert.Equal("hint", HostRow(live, 5));
    }

    [Fact]
    public void A_space_moves_the_caret_with_no_cell_and_confirms_by_the_cursor()
    {
        var (tty, _, live) = Open();
        using var __ = tty;
        Confirm(tty, live, "> ", 'a');
        Type(tty, live, " ");
        Assert.Equal(new HostCursor(4, 0, true), live.Host.Cursor with { Shape = 0 });

        // Claude Code moves the cursor only.
        Apply(tty, live, Delta("> a", cursor: 4) with { InputAck = _seq });
        Assert.Equal(1, live.Prediction.Stats().ConfirmedByCursor);
        Assert.Equal(new HostCursor(4, 0, true), live.Host.Cursor with { Shape = 0 });
    }

    [Fact]
    public void A_hidden_cursor_frame_shows_the_pane_frame_and_keeps_the_epoch()
    {
        var (tty, _, live) = Open();
        using var __ = tty;
        Confirm(tty, live, "> ", 'a');
        Type(tty, live, "b");
        Assert.Equal("> ab", HostRow(live, 0));

        Apply(tty, live, Delta("> a", cursor: 0, visible: false) with { InputAck = _seq });
        Assert.Equal("> a", HostRow(live, 0));
        Assert.Equal(1, live.Prediction.Stats().Pending);

        Apply(tty, live, Delta("> ab", cursor: 4) with { InputAck = _seq });
        Assert.Equal("> ab", HostRow(live, 0));
        Assert.Equal(0, live.Prediction.Stats().Mismatches);
        Assert.Equal(0, live.Prediction.Stats().Pending);
    }

    [Fact]
    public void An_ack_only_body_confirms_a_prediction_against_the_shown_frame()
    {
        var (tty, _, live) = Open();
        using var __ = tty;
        Apply(tty, live, Full("$ "));
        Type(tty, live, "a");
        // The bypassed paint showed the echo with no ack.
        Apply(tty, live, Delta("$ a"));
        Assert.Equal(0, live.Prediction.Stats().ConfirmedByGlyph);

        Apply(tty, live, AckOnly(_gen, _seq));
        Assert.Equal(1, live.Prediction.Stats().ConfirmedByGlyph);
        Type(tty, live, "b");
        Assert.Equal("$ ab", HostRow(live, 0));
    }

    [Fact]
    public void An_ack_only_body_for_another_generation_confirms_nothing()
    {
        var (tty, _, live) = Open();
        using var __ = tty;
        Apply(tty, live, Full("$ "));
        Type(tty, live, "a");
        Apply(tty, live, Delta("$ a"));
        Apply(tty, live, AckOnly(_gen + 3, _seq));
        Assert.Equal(0, live.Prediction.Stats().ConfirmedByGlyph);
        Assert.Equal(_gen, live.LastAppliedBlit(Pane).Generation);
    }

    [Fact]
    public void An_ack_only_body_before_a_later_delta_of_the_same_batch_closes_its_keys()
    {
        var (tty, _, live) = Open();
        using var __ = tty;
        Apply(tty, live, Full("$ "));
        Type(tty, live, "a");
        Apply(tty, live, Delta("$ a"));
        var shown = _gen;
        var next = Delta("$ ab");
        AttachSession.TryWriteCellsBatch(tty, live, [AckOnly(shown, _seq), next], reanchorBeforePaint: true);
        Assert.Equal(0, live.Latency.Report().OpenKeys);
        Assert.Equal(1, live.Latency.Report().KeysAcked);
        Assert.Equal(next.Generation, live.LastAppliedBlit(Pane).Generation);
    }

    [Fact]
    public void An_ack_only_body_for_a_delta_of_the_same_batch_closes_its_keys_after_the_write()
    {
        var (tty, _, live) = Open();
        using var __ = tty;
        Apply(tty, live, Full("$ "));
        Type(tty, live, "a");
        var delta = Delta("$ a");
        AttachSession.TryWriteCellsBatch(tty, live, [delta, AckOnly(delta.Generation, _seq)], reanchorBeforePaint: true);
        Assert.Equal(0, live.Latency.Report().OpenKeys);
        Assert.Equal(1, live.Prediction.Stats().ConfirmedByGlyph);
    }

    [Fact]
    public void Scrollback_deletes_the_epoch_so_it_does_not_come_back()
    {
        var (tty, _, live) = Open();
        using var __ = tty;
        Confirm(tty, live, "$ ", 'a');
        Type(tty, live, "b");
        Assert.Equal("$ ab", HostRow(live, 0));

        // A host compose while the pane is scrolled back deletes the epoch.
        live.SetPaneScrollMetrics(Pane, 5, 100, Rows);
        Assert.True(live.TryGetPaneFrame(Pane, out var stored));
        AttachSession.PaintAssembled(tty, stored!, live);
        Assert.Equal(0, live.Prediction.Stats().Pending);

        live.SetPaneScrollMetrics(Pane, 0, 100, Rows);
        AttachSession.PaintAssembled(tty, stored!, live);
        Assert.Equal("$ a", HostRow(live, 0));
    }

    [Fact]
    public void The_alternate_screen_shows_no_prediction()
    {
        var (tty, output, live) = Open();
        using var __ = tty;
        Confirm(tty, live, "$ ", 'a');
        Apply(tty, live, Delta("$ a") with { ActiveScreen = "alternate" });
        var mark = output.Length;
        Type(tty, live, "b");
        Assert.Equal(0, CountWrites(output, mark));
        Assert.Equal("$ a", HostRow(live, 0));
    }

    [Fact]
    public void A_detected_agent_on_the_alternate_screen_paints_the_prediction()
    {
        var (tty, output, live) = Open();
        using var __ = tty;
        AttachSession.ApplyNotifyEvent(live, AgentEvent(Pane, "claude"), tty);
        ConfirmOnAlternateScreen(tty, live, "> ", 'a');

        var mark = output.Length;
        Type(tty, live, "b");
        Assert.Equal("> ab", HostRow(live, 0));
        Assert.Equal(new HostCursor(4, 0, true), live.Host.Cursor with { Shape = 0 });
        Assert.Equal(1, CountWrites(output, mark));
        Assert.Equal(1, live.Latency.Report().PredictedCellsShown);
    }

    [Fact]
    public void An_agent_that_ends_while_predictions_show_resets_them_and_paints_the_pane_cells()
    {
        var (tty, output, live) = Open();
        using var __ = tty;
        AttachSession.ApplyNotifyEvent(live, AgentEvent(Pane, "claude"), tty);
        ConfirmOnAlternateScreen(tty, live, "> ", 'a');
        Type(tty, live, "b");
        Assert.Equal("> ab", HostRow(live, 0));

        var mark = output.Length;
        AttachSession.ApplyNotifyEvent(live, AgentEvent(Pane, agent: null), tty);
        Assert.Equal("> a", HostRow(live, 0));
        Assert.Equal(new HostCursor(3, 0, true), live.Host.Cursor with { Shape = 0 });
        Assert.Equal(1, CountWrites(output, mark));
        var stats = live.Prediction.Stats();
        Assert.Equal(0, stats.Pending);
        Assert.Equal(1, stats.ResetCalls[PredictionResetReason.AgentEnded]);
        Assert.Null(live.PaintedPrediction);

        // The pane now has no agent: the alternate screen stops prediction.
        mark = output.Length;
        Type(tty, live, "c");
        Assert.Equal(0, CountWrites(output, mark));
        Assert.Equal("> a", HostRow(live, 0));
    }

    [Fact]
    public void A_paste_payload_shows_no_prediction()
    {
        var (tty, output, live) = Open();
        using var __ = tty;
        Confirm(tty, live, "$ ", 'a');
        var mark = output.Length;
        Type(tty, live, "pasted", paste: true);
        Assert.Equal(0, CountWrites(output, mark));
        Assert.Equal(0, live.Prediction.Stats().Pending);
    }

    [Fact]
    public void Never_paints_no_prediction_and_binds_no_mark()
    {
        var (tty, output, live) = Open(new NeverEchoDisplayPolicy());
        using var __ = tty;
        Apply(tty, live, Full("$ "));
        Assert.False(live.Prediction.Predicts);
        var mark = output.Length;
        Type(tty, live, "a");
        Apply(tty, live, Delta("$ a") with { InputAck = _seq });
        Type(tty, live, "b");
        Assert.Equal(1, CountWrites(output, mark));
        Assert.Equal("$ a", HostRow(live, 0));
    }

    [Fact]
    public void Chrome_mode_stamps_the_prediction_inside_the_focused_pane_box()
    {
        var predictor = new EchoPredictor(_time, new AlwaysEchoDisplayPolicy());
        var meter = new AttachLatencyMeter(_time) { SeqObserver = predictor };
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split(), latency: meter, prediction: predictor);
        live.Time = _time;
        using var tty = new UnixRawTerminal(new MemoryStream(), Cols, Rows);
        var box = Assert.Single(live.Chrome!.Panes, pane => pane.PaneId == Pane).Content;
        Apply(tty, live, Full("$ ", box.Cols, box.Rows));
        Type(tty, live, "a");
        Apply(tty, live, Delta("$ a", cols: box.Cols, rows: box.Rows) with { InputAck = _seq });
        Type(tty, live, "b");

        Assert.Equal("b", live.Host.CellAt(box.Col + 3, box.Row).Text);
        Assert.Equal(new HostCursor(box.Col + 4, box.Row, true), live.Host.Cursor with { Shape = 0 });
    }

    [Fact]
    public void Paste_bytes_and_typed_bytes_do_not_merge()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var engine = new KeyEngine(table);
        var events = engine.Feed(Encoding.ASCII.GetBytes("a\x1b[200~xy\x1b[201~b"));
        var merged = AttachSession.CoalescePaneBytes(events);
        Assert.Equal(
            [("a", false), ("xy", true), ("b", false)],
            merged.Select(ev => (Encoding.ASCII.GetString(ev.Bytes!), ev.Paste)));
    }

    private long _gen = 1;

    private (UnixRawTerminal Tty, MemoryStream Output, AttachLiveState Live) Open(IEchoDisplayPolicy? policy = null)
    {
        var output = new MemoryStream();
        var tty = new UnixRawTerminal(output, Cols, Rows);
        var meter = new AttachLatencyMeter(_time);
        var predictor = new EchoPredictor(_time, policy ?? new AlwaysEchoDisplayPolicy(), meter);
        meter.SeqObserver = predictor;
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            PaneId = Pane,
            Latency = meter,
            Prediction = predictor,
        };
        live.Time = _time;
        return (tty, output, live);
    }

    /// <summary>Paints <paramref name="prompt"/>, types one glyph and confirms it.</summary>
    private void Confirm(UnixRawTerminal tty, AttachLiveState live, string prompt, char glyph)
    {
        Apply(tty, live, Full(prompt));
        Type(tty, live, glyph.ToString());
        Apply(tty, live, Delta(prompt + glyph) with { InputAck = _seq });
        Assert.Equal(1, live.Prediction.Stats().ConfirmedByGlyph);
    }

    /// <summary>As <see cref="Confirm"/>, with each frame on the alternate screen.</summary>
    private void ConfirmOnAlternateScreen(UnixRawTerminal tty, AttachLiveState live, string prompt, char glyph)
    {
        Apply(tty, live, Full(prompt) with { ActiveScreen = "alternate" });
        Type(tty, live, glyph.ToString());
        Apply(tty, live, Delta(prompt + glyph) with { InputAck = _seq, ActiveScreen = "alternate" });
        Assert.Equal(1, live.Prediction.Stats().ConfirmedByGlyph);
    }

    /// <summary>The mux event for a change of the pane agent field.</summary>
    private static JsonElement AgentEvent(string paneId, string? agent) =>
        JsonDocument.Parse(new JsonObject
        {
            ["event"] = ProtocolEventTypes.RuntimeEvent,
            ["params"] = new JsonObject
            {
                ["type"] = ProtocolEventTypes.PaneAgentStatusChanged,
                ["payload"] = new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["occupant_generation"] = 1,
                    ["tab_id"] = "t1",
                    ["agent_status"] = agent is null ? "unknown" : "working",
                    ["agent"] = agent,
                    ["seen"] = true,
                },
            },
        }.ToJsonString()).RootElement.Clone();

    /// <summary>The dispatch site and the input sender steps for one payload.</summary>
    private void Type(UnixRawTerminal tty, AttachLiveState live, string text, bool paste = false)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        live.Latency.NoteStdinRead(bytes, live.Latency.Now());
        var mark = live.Prediction.Predicts ? live.Prediction.ReserveMark() : 0;
        var stamp = live.Latency.StampKeys(bytes) with { Mark = mark };
        live.Latency.NoteSent(Pane, ++_seq, [stamp]);
        if (mark != 0)
            AttachSession.ObservePaneInput(tty, live, bytes, mark, paste);
    }

    private void Apply(UnixRawTerminal tty, AttachLiveState live, TerminalRenderCellsPayload cells)
    {
        AttachSession.TryWriteCells(tty, live, cells, reanchorBeforePaint: true);
        if (!cells.AckOnly)
            _gen = cells.Generation;
    }

    private TerminalRenderCellsPayload Full(string text, int cols = Cols, int rows = Rows) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = Pane,
        Full = true,
        GridCols = cols,
        GridRows = rows,
        Generation = 1,
        Rows = [new TerminalRenderCellRow { I = 0, T = text }],
        Cursor = new TerminalRenderCursorPayload { Col = text.Length, Row = 0, Visible = true },
    };

    private TerminalRenderCellsPayload Delta(
        string text,
        int? cursor = null,
        bool visible = true,
        int cols = Cols,
        int rows = Rows,
        int? row = null,
        string? rowText = null)
    {
        var list = new List<TerminalRenderCellRow> { new() { I = 0, T = text } };
        if (row is { } extra)
            list = [new TerminalRenderCellRow { I = extra, T = rowText ?? "" }];
        return new TerminalRenderCellsPayload
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = Pane,
            GridCols = cols,
            GridRows = rows,
            BaseGeneration = _gen,
            Generation = _gen + 1,
            Rows = list,
            Cursor = new TerminalRenderCursorPayload { Col = cursor ?? text.Length, Row = 0, Visible = visible },
        };
    }

    private static TerminalRenderCellsPayload AckOnly(long generation, long ack) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = Pane,
        AckOnly = true,
        Generation = generation,
        InputAck = ack,
    };

    private static string HostRow(AttachLiveState live, int row)
    {
        var text = new StringBuilder();
        for (var c = 0; c < live.Host.Cols; c++)
            text.Append(live.Host.CellAt(c, row).Text);
        return text.ToString().TrimEnd();
    }

    private static int CountWrites(MemoryStream output, long mark)
    {
        var bytes = output.ToArray();
        var text = Encoding.UTF8.GetString(bytes, (int)mark, bytes.Length - (int)mark);
        var count = 0;
        for (var i = text.IndexOf(HostBlitEncoder.SyncBegin, StringComparison.Ordinal);
            i >= 0;
            i = text.IndexOf(HostBlitEncoder.SyncBegin, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
