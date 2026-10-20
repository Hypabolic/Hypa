using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Host paint bytes into a Ghostty VT. The grid that a terminal shows must
/// not depend on the paint path.
/// </summary>
[Trait("Category", "Ghostty")]
public sealed class HostPaintGhosttyTests
{
    private const int Cols = 80;
    private const int Rows = 24;

    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public void Patch_path_bytes_give_the_same_terminal_grid_as_full_redraws()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        var chrome = LayoutChromeGeometry.Compute(
            Cols,
            Rows,
            new LayoutNodeDto
            {
                Type = "split",
                Direction = "right",
                Ratio = 0.5,
                First = new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "left" },
                Second = new LayoutNodeDto { Type = "pane", PaneId = "p2", Label = "right" },
            },
            false,
            null,
            "p1",
            LayoutChromeGeometry.BareInsets,
            1,
            AttachClientMode.Terminal,
            [("t1", "one", true)]);
        var p1 = chrome.Panes.Single(pane => pane.PaneId == "p1").Content;
        var p2 = chrome.Panes.Single(pane => pane.PaneId == "p2").Content;
        using var patch = new Side(chrome, patchPath: true);
        using var full = new Side(chrome, patchPath: false);
        using var patchVt = new GhosttyVtEngine(Cols, Rows, maxScrollback: 0, libraryPathOverride: LibPath);
        using var fullVt = new GhosttyVtEngine(Cols, Rows, maxScrollback: 0, libraryPathOverride: LibPath);

        var steps = new List<TerminalRenderCellsPayload>
        {
            Cells("p1", p1, full: true, 0, 1, [Row(0, "$ ")], cursorCol: 2),
            Cells("p2", p2, full: true, 0, 1, [Row(0, "log")], cursorCol: 3),
        };
        long g = 1;
        foreach (var text in new[] { "$ g", "$ gi", "$ git", "$ git s", "$ git st" })
            steps.Add(Cells("p1", p1, full: false, g, ++g, [Row(0, text)], cursorCol: text.Length));
        steps.Add(Cells("p1", p1, full: false, g, ++g, [Row(1, "日本語 ┌─┐", fg: 0xFF3366CC, bold: true)], cursorCol: 0, cursorRow: 2));
        steps.Add(Cells("p1", p1, full: false, g, ++g, [Row(1, "short")], cursorCol: 5, cursorRow: 1));
        steps.Add(Cells("p1", p1, full: false, g, ++g, [], cursorCol: 1, cursorRow: 0));
        steps.Add(Cells("p2", p2, full: false, 1, 2, [Row(3, "tail -f", fg: 0xFF00AA00)], cursorCol: 7, cursorRow: 3));

        // Full bodies with no reanchor of the same size, as the mux sends
        // them for bulk output: every row, then only two rows, then styled
        // and wide cells. Then a delta chains on the last one.
        var dense = Enumerable.Range(0, p2.Rows)
            .Select(r => Row(r, "bulk " + r + " " + new string((char)('A' + (r % 26)), 24)))
            .ToArray();
        steps.Add(Cells("p2", p2, full: true, 0, 3, dense, cursorCol: 0, cursorRow: p2.Rows - 1));
        steps.Add(Cells("p2", p2, full: true, 0, 4, [Row(0, "cleared"), Row(6, "six")], cursorCol: 3, cursorRow: 6));
        steps.Add(Cells("p1", p1, full: false, g, ++g, [Row(0, "$ ls -la")], cursorCol: 8));
        steps.Add(Cells("p2", p2, full: true, 0, 5, [Row(2, "日本語 ┌─┐", fg: 0xFFCC3366, bold: true), Row(3, "plain")], cursorCol: 1, cursorRow: 3));
        steps.Add(Cells("p2", p2, full: false, 5, 6, [Row(4, "after")], cursorCol: 5, cursorRow: 4));

        foreach (var step in steps)
        {
            patch.Paint(step, patchVt);
            full.Paint(step, fullVt);
            AssertSameGrid(fullVt.CapturePaintSnapshot(), patchVt.CapturePaintSnapshot());
        }

        Assert.True(patch.Live.Latency.Paint.Patches >= 12, $"patches {patch.Live.Latency.Paint.Patches}");
        // Only the first body of each pane has no kept grid.
        Assert.Equal(2, patch.Live.Latency.Paint.Fallbacks(HostPatchFallback.FullFrame));
        Assert.Equal(0, full.Live.Latency.Paint.Patches);
    }

    [SkippableFact]
    public void Kept_style_encoder_gives_the_same_terminal_grid_as_the_reset_per_move_encoder()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 24;
        const int rows = 6;
        var frames = StyleFrames(cols, rows);
        var encoder = new HostBlitEncoder();
        using var current = new GhosttyVtEngine(cols, rows, maxScrollback: 0, libraryPathOverride: LibPath);
        using var reference = new GhosttyVtEngine(cols, rows, maxScrollback: 0, libraryPathOverride: LibPath);
        var first = true;
        foreach (var frame in frames)
        {
            var encoded = encoder.Encode(frame);
            current.Feed(encoded.Span);
            encoder.Commit(frame, encoded);
            reference.Feed(ResetPerMoveEncoder.Encode(frame, clear: first));
            first = false;
            AssertSameGrid(reference.CapturePaintSnapshot(), current.CapturePaintSnapshot());
        }
    }

    [SkippableFact]
    public void Snapshot_painter_gives_the_same_terminal_grid_as_the_reset_per_move_encoder()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 24;
        const int rows = 6;
        var frames = StyleFrames(cols, rows);
        using var current = new GhosttyVtEngine(cols, rows, maxScrollback: 0, libraryPathOverride: LibPath);
        using var reference = new GhosttyVtEngine(cols, rows, maxScrollback: 0, libraryPathOverride: LibPath);
        AssembledSnapshot? previous = null;
        var first = true;
        foreach (var frame in frames)
        {
            var snapshot = ToSnapshot(frame);
            current.Feed(SnapshotPainter.Paint(snapshot, eraseDisplay: first, previous: previous));
            reference.Feed(ResetPerMoveEncoder.Encode(frame, clear: first));
            previous = snapshot;
            first = false;
            AssertSameGrid(reference.CapturePaintSnapshot(), current.CapturePaintSnapshot());
        }
    }

    [Fact]
    public void Styled_cjk_row_keeps_its_style_across_cursor_moves()
    {
        const int cols = 24;
        var red = new AssembledStyle(VtColorPack.FromRgb(200, 30, 30), 0, Bold: true, false, false, false, false, false, false);
        var frame = new HostFrame();
        frame.Resize(cols, 1);
        frame.StampGlyphRun(0, 0, "日本語のテキスト", red);

        var kept = System.Text.Encoding.UTF8.GetString(new HostBlitEncoder().Encode(frame).Span);
        var resetPerMove = System.Text.Encoding.UTF8.GetString(ResetPerMoveEncoder.Encode(frame, clear: true));

        Assert.True(kept.Length < resetPerMove.Length, $"kept {kept.Length} reset-per-move {resetPerMove.Length}");
        var first = kept.IndexOf("日", StringComparison.Ordinal);
        var last = kept.LastIndexOf("ト", StringComparison.Ordinal);
        Assert.True(first > 0 && last > first);
        Assert.DoesNotContain("\u001b[0m", kept[first..last], StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[0;", kept[first..last], StringComparison.Ordinal);
    }

    /// <summary>
    /// Frames that cover wide CJK and box-drawing cells, a link span, a cell
    /// whose only change is its link, a style across a cursor move and
    /// across a skipped continuation cell, and default to styled and back.
    /// </summary>
    private static List<HostFrame> StyleFrames(int cols, int rows)
    {
        var red = new AssembledStyle(VtColorPack.FromRgb(200, 30, 30), 0, Bold: true, false, false, false, false, false, false);
        var blue = new AssembledStyle(VtColorPack.FromRgb(30, 30, 200), VtColorPack.FromRgb(240, 240, 240), false, false, Italic: true, Underline: false, false, false, false);
        var linkA = AssembledStyle.Default with { Underline = true, Hyperlink = "https://example.com/a" };
        var linkB = linkA with { Hyperlink = "https://example.com/b" };

        HostFrame Make(Action<HostFrame> stamp)
        {
            var frame = new HostFrame();
            frame.Resize(cols, rows);
            stamp(frame);
            frame.Cursor = new HostCursor(2, rows - 1, Visible: true);
            return frame;
        }

        var a = Make(f =>
        {
            f.StampGlyphRun(0, 0, "日本語", red);
            f.StampGlyphRun(6, 0, "┌──┐", red);
            f.StampGlyphRun(0, 1, "docs", linkA);
            f.StampGlyphRun(4, 1, "x", linkA with { Hyperlink = null });
            f.StampGlyphRun(0, 2, "ab", blue);
            f.StampGlyphRun(2, 2, "語", blue);
            f.StampGlyphRun(4, 2, "cd", blue);
            f.StampGlyphRun(0, 3, "plain", AssembledStyle.Default);
            f.StampGlyphRun(5, 3, "S", red);
            f.StampGlyphRun(6, 3, "p", AssembledStyle.Default);
        });
        var b = Make(f =>
        {
            f.StampGlyphRun(0, 0, "日本語", red);
            f.StampGlyphRun(6, 0, "┌┬─┐", red);
            f.StampGlyphRun(0, 1, "docs", linkB);
            f.StampGlyphRun(4, 1, "x", linkA with { Hyperlink = null });
            f.StampGlyphRun(0, 2, "ab", blue);
            f.StampGlyphRun(2, 2, "本", blue);
            f.StampGlyphRun(4, 2, "cd", blue);
            f.StampGlyphRun(0, 3, "plain", AssembledStyle.Default);
            f.StampGlyphRun(5, 3, "T", blue);
            f.StampGlyphRun(6, 3, "p", AssembledStyle.Default);
        });
        var c = Make(f =>
        {
            f.StampGlyphRun(0, 0, "ab", red);
            f.StampGlyphRun(2, 0, "日", red);
            f.StampGlyphRun(0, 3, "plainSp", AssembledStyle.Default);
            f.StampGlyphRun(0, 4, "│ x │", blue);
        });
        return [a, b, c];
    }

    private static AssembledSnapshot ToSnapshot(HostFrame frame)
    {
        var rows = new List<IReadOnlyList<AssembledCell>>(frame.Rows);
        for (var r = 0; r < frame.Rows; r++)
        {
            var row = new List<AssembledCell>(frame.Cols);
            for (var c = 0; c < frame.Cols; c++)
                row.Add(frame.CellAt(c, r));
            rows.Add(row);
        }

        var cursor = new AssembledCursor(frame.Cursor.Col, frame.Cursor.Row, frame.Cursor.Visible, frame.Cursor.Shape, HasCursor: true);
        return new AssembledSnapshot("p1", frame.Cols, frame.Rows, "ghostty", "main", rows, default, cursor);
    }

    /// <summary>
    /// The host encoder before it kept the style across cursor moves: a full
    /// frame, with a hyperlink close, a reset and the full style after each
    /// cursor move. The reference for the terminal grid.
    /// </summary>
    private static class ResetPerMoveEncoder
    {
        public static byte[] Encode(HostFrame frame, bool clear)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("\u001b[?2026h\u001b[?25l\u001b]8;;\u001b\\");
            if (clear)
                sb.Append("\u001b[2J");
            AssembledStyle? current = null;
            string? activeHyperlink = null;
            for (var r = 0; r < frame.Rows; r++)
            {
                var toSkip = 0;
                int? nextInlineCol = null;
                for (var c = 0; c < frame.Cols; c++)
                {
                    if (toSkip > 0)
                    {
                        toSkip--;
                        continue;
                    }

                    var cell = frame.CellAt(c, r);
                    var width = cell.Width > 0 ? cell.Width : 1;
                    if (cell.IsContinuation || (cell.Width > 0 && c + cell.Width > frame.Cols))
                    {
                        nextInlineCol = null;
                        continue;
                    }

                    if (nextInlineCol != c)
                    {
                        sb.Append("\u001b[").Append(r + 1).Append(';').Append(c + 1).Append('H');
                        current = null;
                    }

                    if (current is not { } live || !HostFrame.StylesEqual(live, cell.Style))
                    {
                        CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
                        sb.Append("\u001b[0m");
                        CellSgrEncoder.AppendSgr(sb, cell.Style.ToSgr());
                        current = cell.Style;
                    }

                    CellSgrEncoder.WriteHyperlinkIfChanged(sb, ref activeHyperlink, cell.Style.Hyperlink);
                    sb.Append(string.IsNullOrEmpty(cell.Text) ? " " : cell.Text);
                    nextInlineCol = cell.Text is { Length: 1 } glyph && char.IsAscii(glyph[0]) && width == 1
                        ? c + 1
                        : null;
                    toSkip = Math.Max(0, width - 1);
                }
            }

            CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
            sb.Append("\u001b[0m");
            var cursor = frame.Cursor;
            sb.Append("\u001b[").Append(cursor.Row + 1).Append(';').Append(cursor.Col + 1).Append('H');
            sb.Append(cursor.Visible ? "\u001b[?25h" : "\u001b[?25l");
            sb.Append("\u001b[?2026l");
            return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        }
    }

    internal static void AssertSameGrid(VtStructuredSnapshot want, VtStructuredSnapshot got)
    {
        Assert.Equal(want.Cols, got.Cols);
        Assert.Equal(want.Rows, got.Rows);
        for (var r = 0; r < want.Rows; r++)
        {
            for (var c = 0; c < want.Cols; c++)
            {
                var a = want.Cells[r][c];
                var b = got.Cells[r][c];
                Assert.True(
                    a.Text == b.Text
                    && a.Width == b.Width
                    && a.IsContinuation == b.IsContinuation
                    && a.Style == b.Style,
                    $"cell {c},{r}: want '{a.Text}' {a.Style} got '{b.Text}' {b.Style}");
            }
        }

        Assert.Equal(want.Cursor, got.Cursor);
    }

    private static TerminalRenderCellRow Row(int row, string text, uint fg = 0, bool bold = false)
    {
        var widths = new List<int>();
        var cols = 0;
        foreach (var ch in text)
        {
            var wide = ch >= 0x3000 && ch <= 0x9FFF;
            widths.Add(wide ? 2 : 1);
            cols += wide ? 2 : 1;
        }

        return new TerminalRenderCellRow
        {
            I = row,
            T = text,
            W = widths.ToArray(),
            S = fg == 0 && !bold ? null : [new TerminalRenderStyleRun { C = 0, N = cols, Fg = fg, Bold = bold }],
        };
    }

    private static TerminalRenderCellsPayload Cells(
        string paneId,
        CellRect box,
        bool full,
        long baseGeneration,
        long generation,
        TerminalRenderCellRow[] rows,
        int cursorCol,
        int cursorRow = 0) => new()
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = paneId,
            Full = full,
            GridCols = box.Cols,
            GridRows = box.Rows,
            BaseGeneration = baseGeneration,
            Generation = generation,
            Rows = rows,
            Cursor = new TerminalRenderCursorPayload { Col = cursorCol, Row = cursorRow, Visible = true },
        };

    private sealed class Side : IDisposable
    {
        private readonly MemoryStream _output = new();
        private readonly UnixRawTerminal _tty;
        private long _read;

        public Side(LayoutChromeGeometry chrome, bool patchPath)
        {
            _tty = new UnixRawTerminal(_output, Cols, Rows);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            Live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = null!,
                PaneId = "p1",
                SessionName = "ghostty",
                Chrome = chrome,
                PatchPaintEnabled = patchPath,
            };
        }

        public AttachLiveState Live { get; }

        public void Paint(TerminalRenderCellsPayload cells, GhosttyVtEngine vt)
        {
            Assert.True(AttachSession.TryWriteCells(_tty, Live, cells, reanchorBeforePaint: true));
            var length = _output.Length;
            vt.Feed(_output.GetBuffer().AsSpan((int)_read, (int)(length - _read)));
            _read = length;
        }

        public void Dispose() => _tty.Dispose();
    }
}
