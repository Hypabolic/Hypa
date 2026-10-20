using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Cli.Attach;
using Hypa.Terminal;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A real Ghostty pane, the live capture with its dirty-row hint, the live
/// cells encoder and the attach unpacker. After each body, the client grid
/// must equal, cell by cell, the grid of a full frame of the same VT state.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class RowShiftEncodeTests(ITestOutputHelper output)
{
    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task One_line_of_output_in_a_full_pane_sends_a_shift_of_one_and_only_the_new_row()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var pane = NewPane(120, 40, out var vt);
        using var _ = vt;
        pane.FeedVtForTests(LogLines(1, 40));
        var client = new ClientGrid();
        var baseline = client.Apply(Capture(pane), baseline: null);

        pane.FeedVtForTests("\r\n" + LogLines(41, 41));
        var frame = Capture(pane);
        var shifted = Encode(frame, baseline, rowShift: true);
        var plain = Encode(frame, baseline, rowShift: false);
        output.WriteLine($"one-row scroll at 120x40: plain delta {plain.Payload.WireBytes} B, shifted delta {shifted.Payload.WireBytes} B");

        Assert.False(shifted.Payload.Full);
        Assert.Equal(1, shifted.Payload.Shift);
        var row = Assert.Single(shifted.Payload.Rows!);
        Assert.Equal(39, row.I);
        Assert.StartsWith("0041 ", row.T, StringComparison.Ordinal);
        Assert.Equal(0, plain.Payload.Shift);
        Assert.True(
            shifted.Payload.WireBytes * 4 <= plain.Payload.WireBytes,
            $"the shifted delta ({shifted.Payload.WireBytes} B) is not four times smaller than the plain delta ({plain.Payload.WireBytes} B)");

        client.ApplyPayload(shifted.Payload);
        AssertGridEqualsFull(client.Frame!, frame, "after the one-row scroll");
    }

    [SkippableFact]
    public async Task Scrolls_of_more_rows_reverse_scrolls_and_scrolls_with_edits_give_the_full_frame_grid()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var pane = NewPane(80, 24, out var vt);
        using var _ = vt;
        pane.FeedVtForTests(Lines(1, 24));
        var client = new ClientGrid();
        var baseline = client.Apply(Capture(pane), baseline: null);
        var shifts = new List<int>();
        var steps = new (string Name, string Bytes)[]
        {
            ("three rows up", "\r\n" + Lines(25, 27)),
            ("five rows up", "\r\n" + Lines(28, 32)),
            // CSI 2 T scrolls the screen down by two rows. The top two rows are new.
            ("two rows down", "\u001b[2T\u001b[1;1Hnew top 1\u001b[2;1Hnew top 2\u001b[24;1H"),
            // Reverse index at the top row scrolls down by one.
            ("reverse index", "\u001b[1;1H\u001bMreverse line\u001b[24;1H"),
            // Scroll up and change two other rows in the same body.
            ("scroll with edits", "\r\n" + Lines(33, 34) + "\u001b[5;1H\u001b[2Kedited row five\u001b[12;10Hmid\u001b[24;1H"),
            ("scroll with colour", "\r\n\u001b[31mred line\u001b[0m\r\n\u001b[44mblue bg\u001b[0m"),
            ("wide glyphs scroll", "\r\n日本語の行\r\nplain after wide"),
        };

        foreach (var (name, bytes) in steps)
        {
            pane.FeedVtForTests(bytes);
            var frame = Capture(pane);
            var body = Encode(frame, baseline, rowShift: true);
            var plain = Encode(frame, baseline, rowShift: false);
            output.WriteLine($"{name}: shift {body.Payload.Shift}, rows {body.Payload.Rows?.Count ?? 0}, "
                + $"bytes {body.Payload.WireBytes} (plain {plain.Payload.WireBytes})");
            shifts.Add(body.Payload.Shift);
            client.ApplyPayload(body.Payload);
            baseline = frame;
            AssertGridEqualsFull(client.Frame!, frame, name);
            Assert.True(body.Payload.WireBytes <= plain.Payload.WireBytes, name + ": the shift made the body larger");
        }

        Assert.Contains(shifts, shift => shift > 1);
        Assert.Contains(shifts, shift => shift < 0);
    }

    [SkippableFact]
    public async Task A_change_in_one_row_sends_a_plain_delta_with_no_shift()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var pane = NewPane(80, 24, out var vt);
        using var _ = vt;
        pane.FeedVtForTests(Lines(1, 23) + "\r\n$ ");
        var client = new ClientGrid();
        var baseline = client.Apply(Capture(pane), baseline: null);

        pane.FeedVtForTests("ls");
        var frame = Capture(pane);
        var body = Encode(frame, baseline, rowShift: true);

        Assert.False(body.Payload.Full);
        Assert.Equal(0, body.Payload.Shift);
        Assert.DoesNotContain("\"shift\"", Encoding.UTF8.GetString(body.PayloadUtf8), StringComparison.Ordinal);
        var row = Assert.Single(body.Payload.Rows!);
        Assert.Equal(23, row.I);
        client.ApplyPayload(body.Payload);
        AssertGridEqualsFull(client.Frame!, frame, "after the one-row change");
    }

    [SkippableFact]
    public async Task Clearing_a_full_pane_sends_a_full_frame_that_is_not_a_reanchor()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var pane = NewPane(120, 40, out var vt);
        using var _ = vt;
        // Each row is full of text.
        var full = new StringBuilder();
        for (var i = 1; i <= 40; i++)
        {
            if (i > 1)
                full.Append("\r\n");
            full.Append(i.ToString("D3")).Append(' ').Append(new string((char)('a' + (i % 26)), 116));
        }

        pane.FeedVtForTests(full.ToString());
        var client = new ClientGrid();
        var baseline = client.Apply(Capture(pane), baseline: null);

        pane.FeedVtForTests("\u001b[2J\u001b[H$ ");
        var frame = Capture(pane);
        var plainBytes = DeltaBytes(frame, baseline);
        var body = Encode(frame, baseline, rowShift: true);
        output.WriteLine($"clear at 120x40: plain delta {plainBytes} B, sent body {body.Payload.WireBytes} B (full {body.Payload.Full})");

        Assert.True(body.Payload.Full);
        Assert.False(body.Payload.Reanchor);
        Assert.True(body.Payload.WireBytes * 4 < plainBytes, "the full frame is not much smaller than the delta");
        Assert.True(AttachCellsLinePacker.IsOrderedCellsPayload(body.Payload));
        client.ApplyPayload(body.Payload);
        AssertGridEqualsFull(client.Frame!, frame, "after the clear");
    }

    [SkippableFact]
    public async Task A_frame_with_no_baseline_is_a_full_reanchor()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var pane = NewPane(80, 24, out var vt);
        using var _ = vt;
        pane.FeedVtForTests(Lines(1, 30));
        var body = Encode(Capture(pane), baseline: null, rowShift: true);

        Assert.True(body.Payload.Full);
        Assert.True(body.Payload.Reanchor);
        Assert.Equal(0, body.Payload.Shift);
    }

    [SkippableFact]
    public async Task A_sliced_shift_body_moves_the_rows_one_time()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var pane = NewPane(120, 40, out var vt);
        using var _ = vt;
        pane.FeedVtForTests(Lines(1, 40));
        var client = new ClientGrid();
        var baseline = client.Apply(Capture(pane), baseline: null);

        // Ten new lines: the shift is 10 and the body has ten rows.
        pane.FeedVtForTests("\r\n" + Lines(41, 50));
        var frame = Capture(pane);
        var body = Encode(frame, baseline, rowShift: true);
        Assert.Equal(10, body.Payload.Shift);

        var slices = AttachCellsLinePacker.SliceToFit(body.Payload, "sub_1", maxLineBytes: 700);
        Assert.True(slices.Count > 1, $"the body of {body.Payload.WireBytes} B was not sliced ({slices.Count} parts)");
        Assert.Equal(10, slices[0].Shift);
        Assert.All(slices.Skip(1), slice => Assert.Equal(0, slice.Shift));
        foreach (var slice in slices)
            client.ApplyPayload(slice);
        AssertGridEqualsFull(client.Frame!, frame, "after the sliced shift");
    }

    private static string Lines(int first, int last)
    {
        var text = new StringBuilder();
        for (var i = first; i <= last; i++)
        {
            if (i > first)
                text.Append("\r\n");
            text.Append("line ").Append(i).Append(" of the scroll test");
        }

        return text.ToString();
    }

    /// <summary>
    /// Lines as a build log or <c>cat</c> of a file writes them: each line
    /// has its own text, so a row does not look like the row below it.
    /// </summary>
    private static string LogLines(int first, int last)
    {
        const string words = "build test pack sign upload cache fetch parse lint link emit check load store index merge";
        var vocabulary = words.Split(' ');
        var text = new StringBuilder();
        for (var i = first; i <= last; i++)
        {
            if (i > first)
                text.Append("\r\n");
            text.Append(i.ToString("D4")).Append(' ');
            var seed = (uint)(i * 2654435761);
            for (var word = 0; word < 12; word++)
            {
                seed = (seed * 1103515245) + 12345;
                text.Append(vocabulary[(seed >> 16) % (uint)vocabulary.Length]).Append(' ');
            }
        }

        return text.ToString();
    }

    private static PaneRuntime NewPane(int cols, int rows, out GhosttyVtEngine vt)
    {
        vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        var pane = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = cols,
                Rows = rows,
            },
            spawnProcess: static (_, _) => throw new InvalidOperationException("not started"),
            vt: vt);
        pane.LiveStampVerifyInterval = int.MaxValue;
        return pane;
    }

    /// <summary>The live capture, committed as the mux commits a posted paint.</summary>
    private static VtFrame Capture(PaneRuntime pane)
    {
        Assert.True(pane.TryCaptureLivePaintFrame(out var frame, out _, out _));
        Assert.NotNull(frame);
        pane.CommitPostedPaint();
        return frame!;
    }

    private static long _generation;

    private static LiveCellsEncoding Encode(VtFrame frame, VtFrame? baseline, bool rowShift)
    {
        var generation = Interlocked.Increment(ref _generation);
        var encoding = new VtCellsEncoder().Encode(
            frame,
            baseline,
            "p1",
            generation,
            baseline?.Generation ?? 0,
            frame.OccupantGeneration,
            rowShift: rowShift);
        Assert.NotNull(encoding);
        return encoding.Value;
    }

    /// <summary>The bytes of the update that the packer replaces with a full frame.</summary>
    private static int DeltaBytes(VtFrame frame, VtFrame baseline)
    {
        var delta = VtCellPacker.Prepare(frame, baseline, rowShift: true, fullWhenSmaller: false);
        Assert.NotNull(delta);
        Assert.False(delta.Value.Full);
        var payload = VtCellPacker.ToPayload(delta.Value, "p1", frame.Generation, baseline.Generation, frame.OccupantGeneration);
        return VtCellPacker.SerializeUtf8(payload).Length;
    }

    private static void AssertGridEqualsFull(AssembledSnapshot got, VtFrame frame, string step)
    {
        var full = VtCellUnpacker.Apply(
            VtCellPacker.ToPayload(VtCellPacker.Pack(frame), "p1", 1, 0, frame.OccupantGeneration),
            previous: null);
        Assert.Equal(full.Cols, got.Cols);
        Assert.Equal(full.Rows, got.Rows);
        for (var row = 0; row < full.Rows; row++)
        {
            for (var col = 0; col < full.Cols; col++)
            {
                var want = full.CellAt(col, row);
                var have = got.CellAt(col, row);
                Assert.True(
                    want.Equals(have),
                    $"{step}: cell {col},{row} want '{want.Text}' got '{have.Text}' "
                    + $"(row want '{VtCellUnpacker.RowText(full, row)}' got '{VtCellUnpacker.RowText(got, row)}')");
            }
        }

        Assert.Equal(full.Cursor, got.Cursor);
    }

    /// <summary>The kept pane grid of one attach client.</summary>
    private sealed class ClientGrid
    {
        public AssembledSnapshot? Frame { get; private set; }

        /// <summary>Encodes <paramref name="frame"/>, applies the body, and returns the frame as the next baseline.</summary>
        public VtFrame Apply(VtFrame frame, VtFrame? baseline)
        {
            ApplyPayload(Encode(frame, baseline, rowShift: true).Payload);
            return frame;
        }

        public void ApplyPayload(TerminalRenderCellsPayload payload) =>
            Frame = VtCellUnpacker.Apply(payload, Frame);
    }
}
