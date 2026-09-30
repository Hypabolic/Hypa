using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.App;

internal sealed class CellGrid
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public AppCell[] Cells { get; private set; } = [];
    public AppCursor Cursor { get; private set; } = AppCursor.None;
    public string PaneId { get; private set; } = "";
    public long Generation { get; private set; }

    public AppCell At(int col, int row)
    {
        if ((uint)col >= (uint)Cols || (uint)row >= (uint)Rows)
            return AppCell.Blank;
        return Cells[row * Cols + col];
    }

    public void ApplyPayload(JsonElement payload)
    {
        if (!payload.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
            return;
        var kind = kindEl.GetString();
        if (string.Equals(kind, TerminalRenderCellsPayload.KindCells, StringComparison.Ordinal))
        {
            ApplyCells(payload);
            return;
        }

        if (string.Equals(kind, TerminalRenderSnapshotPayload.KindSnapshot, StringComparison.Ordinal))
            ApplySnapshot(payload);
    }

    public void ApplyCells(TerminalRenderCellsPayload payload)
    {
        var cols = Math.Max(1, payload.GridCols);
        var rows = Math.Max(1, payload.GridRows);
        var full = payload.Full || Cells.Length == 0 || Cols != cols || Rows != rows;
        EnsureSize(cols, rows, full);
        if (!string.IsNullOrEmpty(payload.PaneId))
            PaneId = payload.PaneId;
        Generation = payload.Generation;

        if (payload.Rows is { } packed)
        {
            foreach (var row in packed)
                UnpackRow(row);
        }

        if (payload.Cursor is { } c)
        {
            if (c.HasCursor == false)
                Cursor = AppCursor.None;
            else
                Cursor = new AppCursor(c.Col, c.Row, c.Visible, HasCursor: true);
        }
        else if (full)
        {
            Cursor = AppCursor.None;
        }
    }

    private void ApplyCells(JsonElement payload)
    {
        var model = JsonSerializer.Deserialize(
            payload.GetRawText(),
            AppJsonContext.Default.TerminalRenderCellsPayload);
        if (model is null)
            return;
        ApplyCells(model);
    }

    private void ApplySnapshot(JsonElement payload)
    {
        if (!payload.TryGetProperty("snapshot", out var snap) || snap.ValueKind != JsonValueKind.Object)
            return;
        var cols = snap.TryGetProperty("cols", out var cEl) && cEl.TryGetInt32(out var c) ? Math.Max(1, c) : 80;
        var rows = snap.TryGetProperty("rows", out var rEl) && rEl.TryGetInt32(out var r) ? Math.Max(1, r) : 24;
        EnsureSize(cols, rows, fillBlank: true);
        if (payload.TryGetProperty("pane_id", out var pid) && pid.ValueKind == JsonValueKind.String)
            PaneId = pid.GetString() ?? PaneId;

        if (snap.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
        {
            var rowIndex = 0;
            foreach (var row in cells.EnumerateArray())
            {
                if (rowIndex >= Rows)
                    break;
                if (row.ValueKind != JsonValueKind.Array)
                {
                    rowIndex++;
                    continue;
                }

                var col = 0;
                foreach (var cell in row.EnumerateArray())
                {
                    if (col >= Cols)
                        break;
                    var text = " ";
                    if (cell.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        text = t.GetString() is { Length: > 0 } s ? s : " ";
                    var width = 1;
                    if (cell.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) && wi > 0)
                        width = wi;
                    var cont = cell.TryGetProperty("is_continuation", out var contEl)
                        && contEl.ValueKind == JsonValueKind.True;
                    Cells[rowIndex * Cols + col] = new AppCell(text, width, cont, AppCellStyle.Default);
                    col++;
                }

                rowIndex++;
            }
        }

        if (snap.TryGetProperty("cursor", out var cur) && cur.ValueKind == JsonValueKind.Object)
        {
            var cc = cur.TryGetProperty("col", out var colEl) && colEl.TryGetInt32(out var cv) ? cv : 0;
            var cr = cur.TryGetProperty("row", out var rowEl) && rowEl.TryGetInt32(out var rv) ? rv : 0;
            var vis = !cur.TryGetProperty("visible", out var visEl) || visEl.ValueKind != JsonValueKind.False;
            Cursor = new AppCursor(cc, cr, vis, HasCursor: true);
        }
    }

    private void EnsureSize(int cols, int rows, bool fillBlank)
    {
        if (Cols == cols && Rows == rows && Cells.Length == cols * rows)
        {
            if (fillBlank)
                Array.Fill(Cells, AppCell.Blank);
            return;
        }

        var next = new AppCell[cols * rows];
        Array.Fill(next, AppCell.Blank);
        if (!fillBlank && Cells.Length > 0)
        {
            var copyRows = Math.Min(Rows, rows);
            var copyCols = Math.Min(Cols, cols);
            for (var r = 0; r < copyRows; r++)
            {
                for (var c = 0; c < copyCols; c++)
                    next[r * cols + c] = Cells[r * Cols + c];
            }
        }

        Cells = next;
        Cols = cols;
        Rows = rows;
    }

    private void UnpackRow(TerminalRenderCellRow packed)
    {
        if ((uint)packed.I >= (uint)Rows)
            return;
        var origin = packed.I * Cols;
        // vector. A partial write touches only its own cells.
        var start = packed.C ?? 0;
        var replaceWholeRow = packed.C is null;
        if (replaceWholeRow)
        {
            for (var c = 0; c < Cols; c++)
                Cells[origin + c] = AppCell.Blank;
        }

        var text = packed.T ?? "";
        var widths = packed.W;
        var glyphs = packed.G;
        var runs = packed.S;
        var col = start;
        var primary = 0;
        var offset = 0;
        while (col < Cols && offset < text.Length)
        {
            int glen;
            string glyph;
            if (glyphs is not null && primary < glyphs.Count)
            {
                glen = Math.Max(1, glyphs[primary]);
                if (offset + glen > text.Length)
                    break;
                glyph = text.Substring(offset, glen);
                offset += glen;
            }
            else
            {
                if (!Rune.TryGetRuneAt(text, offset, out var rune))
                {
                    glyph = text.Substring(offset, 1);
                    offset++;
                }
                else
                {
                    glyph = rune.ToString();
                    offset += rune.Utf16SequenceLength;
                }
            }

            var width = 1;
            if (widths is not null && primary < widths.Count && widths[primary] > 0)
                width = widths[primary];
            var style = StyleAt(runs, col);
            Cells[origin + col] = new AppCell(glyph, width, false, style);
            for (var k = 1; k < width && col + k < Cols; k++)
                Cells[origin + col + k] = new AppCell("", 1, true, style);
            col += width;
            primary++;
        }
    }

    private static AppCellStyle StyleAt(IReadOnlyList<TerminalRenderStyleRun>? runs, int col)
    {
        if (runs is null)
            return AppCellStyle.Default;
        foreach (var run in runs)
        {
            var n = Math.Max(1, run.N);
            if (col >= run.C && col < run.C + n)
            {
                return new AppCellStyle(
                    run.Fg,
                    run.Bg,
                    run.Bold == true,
                    run.Dim == true,
                    run.Italic == true,
                    run.Underline == true,
                    run.Inverse == true,
                    run.Invisible == true);
            }
        }

        return AppCellStyle.Default;
    }
}

internal readonly record struct AppCell(string Glyph, int Width, bool IsContinuation, AppCellStyle Style)
{
    public static AppCell Blank { get; } = new(" ", 1, false, AppCellStyle.Default);
}

internal readonly record struct AppCellStyle(
    uint Fg,
    uint Bg,
    bool Bold,
    bool Dim,
    bool Italic,
    bool Underline,
    bool Inverse,
    bool Invisible)
{
    public static AppCellStyle Default { get; } = new(0, 0, false, false, false, false, false, false);
}

internal readonly record struct AppCursor(int Col, int Row, bool Visible, bool HasCursor)
{
    public static AppCursor None { get; } = new(0, 0, false, false);
}
