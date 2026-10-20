using System.Text;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.Cli.Attach;

/// <summary>
/// Applies a compact dirty-row cell payload onto a retained pane frame.
/// <c>Vec&lt;CellData&gt;</c>. Write into that buffer. Do not allocate
/// per-cell heap records on size-stable paints.
/// </summary>
internal static class VtCellUnpacker
{
    /// <summary>
    /// Applies <paramref name="payload"/> on <paramref name="previous"/>.
    /// The new frame uses the storage of <paramref name="previous"/> when it
    /// has the same size, so the cells of <paramref name="previous"/> change.
    /// When <paramref name="into"/> is a grid of the payload size that is not
    /// that storage, the new frame uses <paramref name="into"/> and the cells
    /// of <paramref name="previous"/> do not change.
    /// </summary>
    public static AssembledSnapshot Apply(
        TerminalRenderCellsPayload payload,
        AssembledSnapshot? previous,
        AssembledCell[]? into = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var cols = Math.Max(1, payload.GridCols);
        var rows = Math.Max(1, payload.GridRows);
        var full = payload.Full || previous is null || previous.Cols != cols || previous.Rows != rows;
        var source = previous?.Storage is { Length: > 0 } prev ? prev : null;
        var separate = into is not null
            && into.Length == cols * rows
            && !ReferenceEquals(into, source);
        AssembledCell[] storage;
        if (separate)
        {
            storage = into!;
            if (full)
                Array.Fill(storage, AssembledCell.Blank);
        }
        else
        {
            storage = PaneFrameBuffer.Rent(
                cols,
                rows,
                source,
                previous?.Cols ?? 0,
                previous?.Rows ?? 0,
                fillBlank: full);
        }

        if (!full
            && previous is not null
            && previous.Cols == cols
            && previous.Rows == rows
            && !ReferenceEquals(previous.Storage, storage))
        {
            // A row shift into a separate grid moves the kept rows from the
            // previous grid. Any other body starts from a copy of it.
            if (payload.Shift != 0 && source is { } kept && kept.Length == storage.Length)
                ShiftRowsInto(kept, storage, cols, rows, payload.Shift);
            else
            {
                CopyPrevious(previous, storage, cols, rows);
                if (payload.Shift != 0)
                    ShiftRows(storage, cols, rows, payload.Shift);
            }
        }
        else if (!full && payload.Shift != 0)
        {
            // Move the kept rows first. The rows of the body are the cells
            // that still differ after the move.
            ShiftRows(storage, cols, rows, payload.Shift);
        }

        var rowViews = previous is not null
            && ReferenceEquals(previous.Storage, storage)
            && previous.Cells.Count == rows
                ? previous.Cells
                : PaneFrameBuffer.Rows(storage, cols, rows);

        if (payload.Rows is { } packed)
        {
            // A full body starts from a blank grid. A row with an index of
            // blankFrom or more has no write yet, so it is still blank.
            var blankFrom = full ? 0 : rows;
            foreach (var row in packed)
            {
                if ((uint)row.I >= (uint)rows)
                    continue;
                UnpackRow(storage, cols, row, payload.Hl, rowIsBlank: row.I >= blankFrom);
                blankFrom = Math.Max(blankFrom, row.I + 1);
            }
        }

        var cursor = ReadCursor(payload, previous);
        return new AssembledSnapshot(
            payload.PaneId ?? previous?.PaneId ?? "",
            cols,
            rows,
            payload.Provider ?? previous?.Provider ?? "ghostty",
            payload.ActiveScreen ?? previous?.ActiveScreen ?? "main",
            rowViews,
            previous?.Snapshot ?? default,
            cursor,
            payload.OccupantGeneration,
            payload.Generation,
            payload.Sync,
            payload.Mouse,
            payload.MouseEncoding,
            IgnoreSnapshotMouse: true,
            IngestFull: payload.Full || payload.Reanchor,
            BracketedPaste: payload.BracketedPaste,
            ApplicationCursor: payload.ApplicationCursor,
            // Each body states the mode. An absent flag is unknown, not the
            // value of the previous body.
            Echo: payload.Echo,
            Canonical: payload.Canonical)
        {
            Storage = storage,
        };
    }

    /// <summary>
    /// Row <c>r</c> takes the kept row <c>r + shift</c>. The rows that the
    /// move empties are blank. A shift of the full height or more empties
    /// every row.
    /// </summary>
    internal static void ShiftRows(AssembledCell[] storage, int cols, int rows, int shift)
    {
        // As a long: the absolute value of int.MinValue does not fit in an int.
        var distance = (int)Math.Min(rows, Math.Abs((long)shift));
        var kept = rows - distance;
        if (kept > 0)
        {
            // Array.Copy is safe when the two ranges overlap.
            if (shift > 0)
                Array.Copy(storage, distance * cols, storage, 0, kept * cols);
            else
                Array.Copy(storage, 0, storage, distance * cols, kept * cols);
        }

        var blankStart = shift > 0 ? kept * cols : 0;
        Array.Fill(storage, AssembledCell.Blank, blankStart, distance * cols);
    }

    /// <summary>
    /// <see cref="ShiftRows"/> from <paramref name="source"/> into the
    /// separate grid <paramref name="target"/> of the same size: row <c>r</c>
    /// of the target takes row <c>r + shift</c> of the source. The source
    /// does not change.
    /// </summary>
    internal static void ShiftRowsInto(AssembledCell[] source, AssembledCell[] target, int cols, int rows, int shift)
    {
        var distance = (int)Math.Min(rows, Math.Abs((long)shift));
        var kept = rows - distance;
        if (kept > 0)
        {
            if (shift > 0)
                Array.Copy(source, distance * cols, target, 0, kept * cols);
            else
                Array.Copy(source, 0, target, distance * cols, kept * cols);
        }

        var blankStart = shift > 0 ? kept * cols : 0;
        Array.Fill(target, AssembledCell.Blank, blankStart, distance * cols);
    }

    private static void CopyPrevious(
        AssembledSnapshot previous,
        AssembledCell[] storage,
        int cols,
        int rows)
    {
        if (previous.Storage is { Length: > 0 } src && src.Length == storage.Length)
        {
            if (!ReferenceEquals(src, storage))
                Array.Copy(src, storage, storage.Length);
            return;
        }

        for (var r = 0; r < rows; r++)
        {
            var row = r < previous.Cells.Count ? previous.Cells[r] : null;
            for (var c = 0; c < cols; c++)
            {
                storage[r * cols + c] = row is not null && c < row.Count
                    ? row[c]
                    : AssembledCell.Blank;
            }
        }
    }

    private static AssembledCursor ReadCursor(
        TerminalRenderCellsPayload payload,
        AssembledSnapshot? previous)
    {
        if (payload.Cursor is { } c)
        {
            if (c.HasCursor == false)
                return AssembledCursor.Default;
            return new AssembledCursor(c.Col, c.Row, c.Visible, c.Shape, HasCursor: true);
        }

        // Omitted cursor on a cells patch keeps the retained pane cursor.
        if (!payload.Full && previous is not null)
            return previous.Cursor;
        return AssembledCursor.Default;
    }

    private static void UnpackRow(
        AssembledCell[] storage,
        int cols,
        TerminalRenderCellRow packed,
        IReadOnlyList<string>? linkTable = null,
        bool rowIsBlank = false)
    {
        var rowIndex = packed.I;
        var origin = rowIndex * cols;
        if ((uint)origin >= (uint)storage.Length)
            return;
        // vector. A partial write touches only its own cells.
        var start = packed.C ?? 0;
        var replaceWholeRow = packed.C is null;
        if (replaceWholeRow && !rowIsBlank)
        {
            for (var c = 0; c < cols && origin + c < storage.Length; c++)
                storage[origin + c] = AssembledCell.Blank;
        }

        var text = packed.T ?? "";
        var widths = packed.W;
        var glyphs = packed.G;
        var runs = packed.S;
        var col = start;
        var primary = 0;
        var offset = 0;
        while (col < cols && offset < text.Length)
        {
            ReadOnlySpan<char> glyphSpan;
            if (glyphs is not null && primary < glyphs.Count)
            {
                var len = Math.Max(1, glyphs[primary]);
                if (offset + len > text.Length)
                    break;
                glyphSpan = text.AsSpan(offset, len);
                offset += len;
            }
            else
            {
                glyphSpan = text.AsSpan(offset, 1);
                offset++;
            }

            var glyph = AssembledCell.InternText(glyphSpan);
            var width = 1;
            if (widths is not null && primary < widths.Count && widths[primary] > 0)
                width = widths[primary];
            var style = StyleAt(runs, col, linkTable);
            storage[origin + col] = new AssembledCell(glyph, width, false, style);
            for (var k = 1; k < width && col + k < cols; k++)
                storage[origin + col + k] = new AssembledCell(string.Empty, 1, true, style);
            col += width;
            primary++;
        }
    }

    private static AssembledStyle StyleAt(IReadOnlyList<TerminalRenderStyleRun>? runs, int col, IReadOnlyList<string>? linkTable = null)
    {
        if (runs is null)
            return AssembledStyle.Default;
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (col >= run.C && col < run.C + Math.Max(1, run.N))
            {
                return new AssembledStyle(
                    run.Fg,
                    run.Bg,
                    run.Bold == true,
                    run.Dim == true,
                    run.Italic == true,
                    run.Underline == true,
                    run.Inverse == true,
                    run.Invisible == true,
                    run.Strikethrough == true,
                    run.Blink == true,
                    run.Overline == true,
                    run.UnderlineColor,
                    run.UnderlineStyle ?? 0,
                    ResolveHyperlink(run, linkTable));
            }
        }

        return AssembledStyle.Default;
    }

    private static string? ResolveHyperlink(TerminalRenderStyleRun run, IReadOnlyList<string>? linkTable)
    {
        if (!string.IsNullOrEmpty(run.Hyperlink))
            return run.Hyperlink;
        // Optional hl/hi shape: hi is index plus one into hl. Copies the
        if (run.Hi is { } hi && hi > 0 && linkTable is not null && hi - 1 < linkTable.Count)
            return linkTable[hi - 1];
        return null;
    }

    internal static string RowText(AssembledSnapshot snapshot, int row)
    {
        if (row < 0 || row >= snapshot.Rows)
            return "";
        var sb = new StringBuilder();
        for (var c = 0; c < snapshot.Cols; c++)
        {
            var cell = snapshot.CellAt(c, row);
            if (cell.IsContinuation)
                continue;
            sb.Append(cell.Text);
        }

        return sb.ToString();
    }
}
