using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class VtBlitEncoderTests
{
    [Fact]
    public void Palette_n_encodes_indexed_sgr()
    {
        var sb = new System.Text.StringBuilder();
        CellSgrEncoder.AppendSgr(
            sb,
            new CellSgr(VtColorPack.FromPalette(1), VtColorPack.FromPalette(2), false, false, false, false, false, false, false, UnderlineColor: VtColorPack.FromPalette(3)));
        var text = sb.ToString();
        Assert.Contains("38;5;1", text, StringComparison.Ordinal);
        Assert.Contains("48;5;2", text, StringComparison.Ordinal);
        Assert.Contains("58;5;3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("38;5;256", text, StringComparison.Ordinal);

        sb.Clear();
        CellSgrEncoder.AppendSgr(
            sb,
            new CellSgr(VtColorPack.FromPalette(256), VtColorPack.FromPalette(-1), false, false, false, false, false, false, false));
        Assert.Equal("", sb.ToString());

        sb.Clear();
        CellSgrEncoder.AppendSgr(
            sb,
            new CellSgr(0, 0, false, false, false, false, false, false, false));
        Assert.Equal("", sb.ToString());
    }

    [Fact]
    public void Truecolor_sgr_writes_38_2_and_48_2_digits()
    {
        var sb = new System.Text.StringBuilder();
        var style = new CellSgr(
            VtColorPack.FromRgb(0xFF, 0x80, 0x00),
            VtColorPack.FromRgb(0x01, 0x02, 0x03),
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            UnderlineColor: VtColorPack.FromRgb(0x0A, 0x0B, 0x0C));
        CellSgrEncoder.AppendSgr(sb, style);
        var text = sb.ToString();
        Assert.Equal("\u001b[38;2;255;128;0;48;2;1;2;3;58;2;10;11;12m", text);
        Assert.DoesNotContain(";;", text, StringComparison.Ordinal);

        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        CellSgrEncoder.AppendSgr(writer, style);
        Assert.Equal(text, System.Text.Encoding.UTF8.GetString(writer.WrittenSpan));
    }

    [Fact]
    public void Encode_is_pure_and_skips_equal_cells()
    {
        var first = Frame("a");
        var encoded = VtBlitEncoder.Encode(first, baseline: null);
        Assert.True(encoded.Full);
        Assert.True(encoded.WireBytes > 0);
        var again = VtBlitEncoder.Encode(first, first);
        Assert.False(again.Full);
        Assert.Equal(0, again.ChangedCells);
    }

    [Fact]
    public void Cells_visually_equal_compares_style()
    {
        var a = new VtCellView("x", 1, false, "#FF0000", null, false, false, false, false, false, false, false);
        var b = a with { Fg = "#00FF00" };
        Assert.True(VtBlitEncoder.CellsVisuallyEqual(a, a));
        Assert.False(VtBlitEncoder.CellsVisuallyEqual(a, b));
    }

    [Fact]
    public void Full_on_resize_and_generation_gap()
    {
        var a = Frame("a", cols: 2, rows: 1, generation: 1);
        var resized = Frame("a", cols: 3, rows: 1, generation: 2);
        Assert.True(VtBlitEncoder.RequiresFull(resized, a));
        var gapped = Frame("b", cols: 2, rows: 1, generation: 4);
        Assert.True(VtBlitEncoder.RequiresFull(gapped, a));
        var next = Frame("b", cols: 2, rows: 1, generation: 2);
        Assert.False(VtBlitEncoder.RequiresFull(next, a));
    }

    [Fact]
    public void Osc8_sanitizes_esc_bel_and_closes_before_switch()
    {
        var sb = new System.Text.StringBuilder();
        string? active = null;
        CellSgrEncoder.WriteHyperlinkIfChanged(sb, ref active, "https://a.example/\u001bevil\u0007");
        Assert.Equal("https://a.example/evil", active);
        Assert.DoesNotContain("\u0007", sb.ToString(), StringComparison.Ordinal);
        var first = sb.ToString();
        Assert.Contains("https://a.example/evil", first, StringComparison.Ordinal);
        CellSgrEncoder.WriteHyperlinkIfChanged(sb, ref active, "https://b.example/");
        var wire = sb.ToString()[first.Length..];
        Assert.StartsWith("\u001b]8;;\u001b\\", wire, StringComparison.Ordinal);
        Assert.Contains("\u001b]8;;https://b.example/\u001b\\", wire, StringComparison.Ordinal);
        CellSgrEncoder.WriteHyperlinkIfChanged(sb, ref active, "\u001b\u0007");
        Assert.Null(active);
        Assert.EndsWith("\u001b]8;;\u001b\\", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_emits_osc8_hyperlink_open_change_and_close()
    {
        var linked = Cell("a", "https://example.com/a");
        var other = Cell("b", "https://example.com/b");
        var plain = Cell("c", null);
        var frame = new VtFrame(
            "p1", 3, 1,
            [[linked, other, plain]],
            new VtFrameCursor(0, 0, false, 0),
            new VtFrameModes(false, true, false, false, "none", false),
            0, 1, 1);
        var encoded = VtBlitEncoder.Encode(frame, baseline: null);
        Assert.Contains("\u001b]8;;https://example.com/a\u001b\\", encoded.Ansi, StringComparison.Ordinal);
        Assert.Contains("\u001b]8;;https://example.com/b\u001b\\", encoded.Ansi, StringComparison.Ordinal);
        Assert.Contains("\u001b]8;;\u001b\\", encoded.Ansi, StringComparison.Ordinal);
        var again = VtBlitEncoder.Encode(frame, frame);
        Assert.Equal(0, again.ChangedCells);
        Assert.DoesNotContain("https://example.com", again.Ansi, StringComparison.Ordinal);
    }

    [Fact]
    public void Changed_cell_is_small()
    {
        var a = Frame("a");
        var b = Frame("b");
        var encoded = VtBlitEncoder.Encode(b, a);
        Assert.False(encoded.Full);
        Assert.Equal(1, encoded.ChangedCells);
        Assert.True(encoded.WireBytes < 64);
        Assert.Contains("b", encoded.Ansi, StringComparison.Ordinal);
    }

    private static VtCellView Cell(string glyph, string? hyperlink) =>
        new(glyph, 1, false, null, null, false, false, false, false, false, false, false, Hyperlink: hyperlink);

    private static VtFrame Frame(string glyph, int cols = 1, int rows = 1, long generation = 1)
    {
        var cells = new VtCellView[rows][];
        for (var r = 0; r < rows; r++)
        {
            cells[r] = new VtCellView[cols];
            for (var c = 0; c < cols; c++)
                cells[r][c] = new VtCellView(glyph, 1, false, null, null, false, false, false, false, false, false, false);
        }

        return new VtFrame(
            "p1",
            cols,
            rows,
            cells,
            new VtFrameCursor(0, 0, true, 0),
            new VtFrameModes(false, true, false, false, "none", false),
            0,
            1,
            generation);
    }
}
