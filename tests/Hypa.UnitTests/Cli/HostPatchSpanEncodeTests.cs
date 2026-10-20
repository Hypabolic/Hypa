using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The patch encode compares only the host cells that the patch stamped
/// (the pane box columns of its rows), and the chrome stamps printable ASCII
/// runs with no grapheme walk. The bytes and the cells are the same as on
/// the full paths.
/// </summary>
public sealed class HostPatchSpanEncodeTests
{
    private static readonly AssembledStyle Red = AssembledStyle.Default with { Fg = 0xFFFF0000 };
    private static readonly AssembledStyle Blue = AssembledStyle.Default with { Fg = 0xFF0000FF, Bold = true };

    [Fact]
    public void Span_encode_writes_the_bytes_of_a_diff_of_the_full_rows()
    {
        var baseline = new HostFrame();
        baseline.Resize(30, 5);
        baseline.StampGlyphRun(0, 0, "left pane text", Red);
        baseline.StampGlyphRun(10, 0, "bulk row zero", Blue);
        // A wide cell that ends where the span starts.
        baseline.StampGlyphRun(8, 1, "日", Red);
        baseline.StampGlyphRun(10, 1, "bulk 語 text", Blue);
        // A wide cell in the last two columns of the span.
        baseline.StampGlyphRun(18, 2, "語", Blue);
        baseline.StampGlyphRun(20, 2, "right", Red);
        // A wide cell that crosses the end of the span.
        baseline.Stamp(19, 3, new AssembledCell("本", 2, false, Blue));
        baseline.Stamp(20, 3, new AssembledCell("", 1, true, Blue));
        baseline.StampGlyphRun(21, 3, "tail", Red);
        baseline.Cursor = new HostCursor(3, 0, Visible: true);

        var spans = new HostBlitEncoder();
        var rows = new HostBlitEncoder();
        Commit(spans, baseline);
        Commit(rows, baseline);

        var next = new HostFrame();
        next.CopyFrom(baseline);
        // Two panes change on row 0 in one paint.
        next.StampGlyphRun(2, 0, "XYZ", Blue);
        next.StampGlyphRun(12, 0, "QQQ", Red);
        next.StampGlyphRun(10, 1, "BULK x TEXT", Red);
        next.StampGlyphRun(18, 2, "ab", Red);
        next.Stamp(19, 3, new AssembledCell("n", 1, false, Red));
        next.Cursor = new HostCursor(14, 1, Visible: true);

        var spanBlit = spans.EncodeRows(
            next,
            [
                new HostRowSpan(1, 10, 22),
                new HostRowSpan(0, 10, 20),
                new HostRowSpan(0, 0, 5),
                new HostRowSpan(2, 10, 20),
                new HostRowSpan(3, 10, 20),
                // Out of the frame: no effect.
                new HostRowSpan(9, 0, 30),
            ]);
        var rowBlit = rows.EncodeRows(
            next,
            [new HostRowSpan(0, 0, 30), new HostRowSpan(1, 0, 30), new HostRowSpan(2, 0, 30), new HostRowSpan(3, 0, 30)]);

        Assert.NotNull(spanBlit);
        Assert.NotNull(rowBlit);
        Assert.Equal(rowBlit.Value.Bytes, spanBlit.Value.Bytes);
        Assert.False(spanBlit.Value.NoOp);

        spans.Commit(next, spanBlit.Value);
        rows.Commit(next, rowBlit.Value);
        for (var r = 0; r < next.Rows; r++)
        {
            for (var c = 0; c < next.Cols; c++)
            {
                Assert.True(spans.TryGetKeptCell(c, r, out var kept));
                Assert.True(HostFrame.CellsEqual(next.CellAt(c, r), kept), $"kept cell {c},{r}");
                Assert.True(rows.TryGetKeptCell(c, r, out var keptRows));
                Assert.True(HostFrame.CellsEqual(keptRows, kept), $"kept cell {c},{r}");
            }
        }
    }

    [Fact]
    public void Span_encode_of_unchanged_cells_is_a_no_op()
    {
        var frame = new HostFrame();
        frame.Resize(20, 3);
        frame.StampGlyphRun(0, 1, "same text", Red);
        frame.Cursor = new HostCursor(1, 1, Visible: true);
        var encoder = new HostBlitEncoder();
        Commit(encoder, frame);

        var blit = encoder.EncodeRows(frame, [new HostRowSpan(1, 0, 10)]);

        Assert.NotNull(blit);
        Assert.True(blit.Value.NoOp);
    }

    public static TheoryData<int, int> Placements() => new()
    {
        { 0, -1 },
        { -3, -1 },
        { 5, 4 },
        { 26, -1 },
        { 30, -1 },
    };

    [Theory]
    [MemberData(nameof(Placements))]
    public void Printable_ascii_run_stamps_the_cells_of_the_grapheme_rule(int col, int maxCols)
    {
        const string text = "ab~ x{}|";
        var ascii = new HostFrame();
        ascii.Resize(30, 2);
        ascii.StampGlyphRun(col, 0, text, Red, maxCols);
        // A control character sends the run through the grapheme rule. The
        // safe encode drops it, so the text is the same.
        var graphemes = new HostFrame();
        graphemes.Resize(30, 2);
        graphemes.StampGlyphRun(col, 0, text + "\u0007", Red, maxCols);

        for (var r = 0; r < ascii.Rows; r++)
        {
            for (var c = 0; c < ascii.Cols; c++)
            {
                Assert.True(
                    HostFrame.CellsEqual(graphemes.CellAt(c, r), ascii.CellAt(c, r)),
                    $"cell {c},{r}: grapheme '{graphemes.CellAt(c, r).Text}' ascii '{ascii.CellAt(c, r).Text}'");
            }
        }
    }

    private static void Commit(HostBlitEncoder encoder, HostFrame frame)
    {
        var blit = encoder.Encode(frame);
        encoder.Commit(frame, blit);
    }
}
