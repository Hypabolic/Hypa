using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Packs a pane <see cref="VtFrame"/> into a compact cell payload.
// / frame.
/// first frame or size change. The Ghostty dirty-row hint never selects
/// the cells that the payload carries.
/// </summary>
public static class VtCellPacker
{
    // changed cell follows the last written cell. One extra JSON row object
    // costs about 20 bytes; eight default cells cost about 8 bytes.
    private const int SegmentJoinColumns = 8;

    public static VtCellPackResult? Prepare(
        VtFrame current,
        VtFrame? baseline,
        IReadOnlyList<int>? dirtyRows)
    {
        ArgumentNullException.ThrowIfNull(current);
        // Partial cannot prove unmarked rows are stable. Diff against the
        // admitted frame, not the Ghostty dirty-row hint.
        _ = dirtyRows;
        if (baseline is null)
            return Pack(current);
        if (baseline.CellsVisuallyEqual(current))
            return null;
        if (baseline.Cols != current.Cols
            || baseline.Rows != current.Rows
            || baseline.ViewportOrigin != current.ViewportOrigin
            || baseline.OccupantGeneration != current.OccupantGeneration
            || !string.Equals(baseline.ActiveScreen, current.ActiveScreen, StringComparison.Ordinal))
            return Pack(current);
        return PackDelta(current, baseline);
    }

    public static VtCellPackResult Pack(VtFrame current, bool emitHyperlinkTable = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        var rows = new List<TerminalRenderCellRow>(current.Rows);
        var changedCells = 0;
        List<string>? linkTable = null;
        for (var r = 0; r < current.Rows; r++)
        {
            var packed = PackRow(current, r, omitBlankDefault: true, emitHyperlinkTable, ref linkTable);
            if (packed is null)
                continue;
            rows.Add(packed.Value.Row);
            changedCells += packed.Value.ChangedCells;
        }

        return new VtCellPackResult(
            true,
            changedCells,
            rows,
            PackCursor(current.Cursor),
            current.Cols,
            current.Rows,
            current.Provider,
            current.ActiveScreen,
            current.ViewportOrigin,
            current.Modes.SynchronizedOutput,
            current.Modes.Mouse,
            current.Modes.MouseEncoding,
            linkTable,
            current.Modes.BracketedPaste,
            current.Modes.ApplicationCursor);
    }

    private static VtCellPackResult PackDelta(VtFrame current, VtFrame baseline)
    {
        var rows = new List<TerminalRenderCellRow>();
        var changedCells = 0;
        List<string>? linkTable = null;
        var segments = new List<(int Start, int End)>(4);
        for (var r = 0; r < current.Rows; r++)
        {
            CollectChangedSegments(current, baseline, r, segments);
            for (var i = 0; i < segments.Count; i++)
            {
                var (start, end) = segments[i];
                var packed = PackRow(
                    current,
                    r,
                    omitBlankDefault: false,
                    emitHyperlinkTable: false,
                    ref linkTable,
                    start,
                    end);
                if (packed is null)
                    continue;
                rows.Add(packed.Value.Row with { C = start });
                changedCells += packed.Value.ChangedCells;
            }
        }

        return new VtCellPackResult(
            false,
            changedCells,
            rows,
            PackCursor(current.Cursor),
            current.Cols,
            current.Rows,
            current.Provider,
            current.ActiveScreen,
            current.ViewportOrigin,
            current.Modes.SynchronizedOutput,
            current.Modes.Mouse,
            current.Modes.MouseEncoding,
            linkTable,
            current.Modes.BracketedPaste,
            current.Modes.ApplicationCursor);
    }

    private static void CollectChangedSegments(
        VtFrame current,
        VtFrame baseline,
        int row,
        List<(int Start, int End)> dest)
    {
        dest.Clear();
        var cols = current.Cols;
        Span<byte> marked = cols <= 256 ? stackalloc byte[cols] : new byte[cols];
        marked.Clear();
        var any = false;
        for (var c = 0; c < cols; c++)
        {
            if (current.CellAt(row, c).VisuallyEquals(baseline.CellAt(row, c)))
                continue;
            marked[c] = 1;
            any = true;
        }

        if (!any)
            return;

        for (var c = 1; c < cols; c++)
        {
            if (marked[c] == 0)
                continue;
            var start = c;
            while (start > 0
                && (current.CellAt(row, start).IsContinuation
                    || baseline.CellAt(row, start).IsContinuation))
            {
                start--;
                marked[start] = 1;
            }
        }

        // invalidates max(width_current, width_baseline) columns.
        for (var c = 0; c < cols; c++)
        {
            if (marked[c] == 0)
                continue;
            var cell = current.CellAt(row, c);
            var prev = baseline.CellAt(row, c);
            if (cell.IsContinuation && prev.IsContinuation)
                continue;
            var span = Math.Max(Math.Max(1, cell.Width), Math.Max(1, prev.Width));
            for (var k = 1; k < span && c + k < cols; k++)
                marked[c + k] = 1;
        }

        var i = 0;
        while (i < cols)
        {
            if (marked[i] == 0)
            {
                i++;
                continue;
            }

            var start = i;
            var end = i + 1;
            while (end < cols && marked[end] != 0)
                end++;
            dest.Add((start, end));
            i = end;
        }

        for (var n = dest.Count - 1; n >= 1; n--)
        {
            var gap = dest[n].Start - dest[n - 1].End;
            if (gap < SegmentJoinColumns)
                dest[n - 1] = (dest[n - 1].Start, dest[n].End);
            else
                continue;
            dest.RemoveAt(n);
        }
    }

    public static TerminalRenderCellsPayload ToPayload(
        VtCellPackResult packed,
        string paneId,
        long generation,
        long baseGeneration,
        int occupantGeneration)
    {
        // message one time.
        // send_to_all_clients encodes above the client loop, then sends the
        // same bytes to every client. WireBytes is filled from that one emit
        // serialize, not a probe.
        return new TerminalRenderCellsPayload
        {
            PaneId = paneId,
            Kind = TerminalRenderCellsPayload.KindCells,
            Full = packed.Full,
            Reanchor = packed.Full,
            GridCols = packed.GridCols,
            GridRows = packed.GridRows,
            Generation = generation,
            BaseGeneration = baseGeneration,
            OccupantGeneration = occupantGeneration,
            ChangedCells = packed.ChangedCells,
            Rows = packed.Rows,
            Cursor = packed.Cursor,
            Provider = packed.Provider,
            ActiveScreen = packed.ActiveScreen,
            ViewportOrigin = packed.ViewportOrigin,
            Sync = packed.Sync,
            Mouse = packed.Mouse,
            MouseEncoding = packed.MouseEncoding,
            BracketedPaste = packed.BracketedPaste,
            ApplicationCursor = packed.ApplicationCursor,
            Hl = packed.HyperlinkTable,
        };
    }

    public static string Serialize(TerminalRenderCellsPayload payload) =>
        JsonSerializer.Serialize(payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);

    public static byte[] SerializeUtf8(TerminalRenderCellsPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);

    private static TerminalRenderCursorPayload PackCursor(VtFrameCursor cursor)
    {
        if (!cursor.HasCursor)
            return new TerminalRenderCursorPayload { HasCursor = false };
        return new TerminalRenderCursorPayload
        {
            Col = cursor.Col,
            Row = cursor.Row,
            Visible = cursor.Visible,
            Shape = cursor.Shape,
        };
    }

    private static (TerminalRenderCellRow Row, int ChangedCells)? PackRow(
        VtFrame current,
        int r,
        bool omitBlankDefault,
        bool emitHyperlinkTable,
        ref List<string>? linkTable,
        int startCol = 0,
        int endColExclusive = -1)
    {
        var cols = current.Cols;
        if (endColExclusive < 0 || endColExclusive > cols)
            endColExclusive = cols;
        if (startCol < 0)
            startCol = 0;
        if (startCol >= endColExclusive)
            return null;

        var text = new StringBuilder(endColExclusive - startCol);
        List<int>? widths = null;
        List<int>? glyphs = null;
        var runs = new List<TerminalRenderStyleRun>();
        TerminalRenderStyleRun? open = null;
        int openStyleIndex = -1;
        int openLinkIndex = -2;
        var changed = 0;
        var col = startCol;
        var blankDefault = true;
        while (col < endColExclusive)
        {
            var packedCell = current.PackedAt(r, col);
            var cell = current.CellAt(r, col);
            var width = cell.Width > 0 ? cell.Width : 1;
            if (cell.IsContinuation)
            {
                col++;
                continue;
            }

            var glyph = string.IsNullOrEmpty(cell.Text) ? " " : cell.Text;
            text.Append(glyph);
            if (width != 1)
            {
                widths ??= new List<int>(current.Cols);
                while (widths.Count < changed)
                    widths.Add(1);
                widths.Add(width);
            }
            else if (widths is not null)
                widths.Add(1);

            if (glyph.Length != 1)
            {
                glyphs ??= new List<int>(current.Cols);
                while (glyphs.Count < changed)
                    glyphs.Add(1);
                glyphs.Add(glyph.Length);
            }
            else if (glyphs is not null)
                glyphs.Add(1);

            if (!IsDefault(in cell) || glyph != " ")
                blankDefault = false;

            var style = ToRun(in cell, col, width, emitHyperlinkTable, ref linkTable);
            if (open is null)
            {
                open = style;
                openStyleIndex = packedCell.StyleIndex;
                openLinkIndex = packedCell.HyperlinkIndex;
            }
            else if (packedCell.StyleIndex == openStyleIndex
                && packedCell.HyperlinkIndex == openLinkIndex
                && SameStyle(open, style))
            {
                open = open with { N = open.N + width };
            }
            else
            {
                if (!IsDefaultRun(open))
                    runs.Add(open);
                open = style;
                openStyleIndex = packedCell.StyleIndex;
                openLinkIndex = packedCell.HyperlinkIndex;
            }

            changed++;
            col += width;
        }

        if (open is not null && !IsDefaultRun(open))
            runs.Add(open);

        if (omitBlankDefault && blankDefault)
            return null;

        return (new TerminalRenderCellRow
        {
            I = r,
            T = text.ToString(),
            W = widths,
            G = glyphs,
            S = runs.Count == 0 ? null : runs,
        }, changed);
    }

    private static bool IsDefault(in VtCellView cell) =>
        cell.FgPacked == 0
        && cell.BgPacked == 0
        && cell.Modifier == 0
        && cell.UnderlineColorPacked == 0
        && cell.UnderlineStylePacked == 0
        && string.IsNullOrEmpty(cell.Hyperlink);

    private static TerminalRenderStyleRun ToRun(
        in VtCellView cell,
        int col,
        int n,
        bool emitHyperlinkTable,
        ref List<string>? linkTable)
    {
        int? hi = null;
        string? hyperlink = string.IsNullOrEmpty(cell.Hyperlink) ? null : cell.Hyperlink;
        if (emitHyperlinkTable && hyperlink is not null)
        {
            linkTable ??= new List<string>();
            var index = linkTable.IndexOf(hyperlink);
            if (index < 0)
            {
                index = linkTable.Count;
                linkTable.Add(hyperlink);
            }

            hi = index + 1;
            hyperlink = null;
        }

        return new()
        {
            C = col,
            N = n,
            Fg = cell.FgPacked,
            Bg = cell.BgPacked,
            Bold = cell.Bold ? true : null,
            Dim = cell.Dim ? true : null,
            Italic = cell.Italic ? true : null,
            Underline = cell.Underline ? true : null,
            Inverse = cell.Inverse ? true : null,
            Invisible = cell.Invisible ? true : null,
            Strikethrough = cell.Strikethrough ? true : null,
            Blink = cell.Blink ? true : null,
            Overline = cell.Overline ? true : null,
            UnderlineColor = cell.UnderlineColorPacked,
            UnderlineStyle = cell.UnderlineStylePacked == 0 ? null : (int)cell.UnderlineStylePacked,
            Hyperlink = hyperlink,
            Hi = hi,
        };
    }

    private static bool SameStyle(TerminalRenderStyleRun a, TerminalRenderStyleRun b) =>
        a.Fg == b.Fg
        && a.Bg == b.Bg
        && a.Bold == b.Bold
        && a.Dim == b.Dim
        && a.Italic == b.Italic
        && a.Underline == b.Underline
        && a.Inverse == b.Inverse
        && a.Invisible == b.Invisible
        && a.Strikethrough == b.Strikethrough
        && a.Blink == b.Blink
        && a.Overline == b.Overline
        && a.UnderlineColor == b.UnderlineColor
        && a.UnderlineStyle == b.UnderlineStyle
        && a.Hyperlink == b.Hyperlink
        && a.Hi == b.Hi;

    private static bool IsDefaultRun(TerminalRenderStyleRun run) =>
        run.Fg == 0
        && run.Bg == 0
        && run.Bold is null
        && run.Dim is null
        && run.Italic is null
        && run.Underline is null
        && run.Inverse is null
        && run.Invisible is null
        && run.Strikethrough is null
        && run.Blink is null
        && run.Overline is null
        && run.UnderlineColor == 0
        && run.UnderlineStyle is null
        && run.Hyperlink is null
        && run.Hi is null;
}

/// <summary>Packed pane cells before JSON wrap.</summary>
public readonly record struct VtCellPackResult(
    bool Full,
    int ChangedCells,
    IReadOnlyList<TerminalRenderCellRow> Rows,
    TerminalRenderCursorPayload Cursor,
    int GridCols,
    int GridRows,
    string Provider,
    string ActiveScreen,
    int ViewportOrigin,
    bool Sync = false,
    string? Mouse = null,
    string? MouseEncoding = null,
    IReadOnlyList<string>? HyperlinkTable = null,
    bool BracketedPaste = false,
    bool ApplicationCursor = false);
