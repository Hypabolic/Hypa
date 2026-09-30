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
    public static AssembledSnapshot Apply(TerminalRenderCellsPayload payload, AssembledSnapshot? previous)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var cols = Math.Max(1, payload.GridCols);
        var rows = Math.Max(1, payload.GridRows);
        var full = payload.Full || previous is null || previous.Cols != cols || previous.Rows != rows;
        var storage = PaneFrameBuffer.Rent(
            cols,
            rows,
            previous?.Storage is { Length: > 0 } prev ? prev : null,
            previous?.Cols ?? 0,
            previous?.Rows ?? 0,
            fillBlank: full);
        if (!full
            && previous is not null
            && previous.Cols == cols
            && previous.Rows == rows
            && !ReferenceEquals(previous.Storage, storage))
        {
            CopyPrevious(previous, storage, cols, rows);
        }

        var rowViews = previous is not null
            && ReferenceEquals(previous.Storage, storage)
            && previous.Cells.Count == rows
                ? previous.Cells
                : PaneFrameBuffer.Rows(storage, cols, rows);

        if (payload.Rows is { } packed)
        {
            foreach (var row in packed)
            {
                if ((uint)row.I >= (uint)rows)
                    continue;
                UnpackRow(storage, cols, row, payload.Hl);
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
            ApplicationCursor: payload.ApplicationCursor)
        {
            Storage = storage,
        };
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

    private static void UnpackRow(AssembledCell[] storage, int cols, TerminalRenderCellRow packed, IReadOnlyList<string>? linkTable = null)
    {
        var rowIndex = packed.I;
        var origin = rowIndex * cols;
        if ((uint)origin >= (uint)storage.Length)
            return;
        // vector. A partial write touches only its own cells.
        var start = packed.C ?? 0;
        var replaceWholeRow = packed.C is null;
        if (replaceWholeRow)
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
