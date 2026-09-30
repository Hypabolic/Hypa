using System.Globalization;
using System.Text;

namespace Hypa.AgentRuntime.Application;

/// <summary>
// <see cref="Encode"/> is pure: it does
/// not mutate <paramref name="baseline"/>. Commit is the caller's
/// LastAdmittedFrame swap after writer admission. Live mux paint is cells,
/// not this encoder. Dirty-row Prepare is deleted.
/// </summary>
public static class VtBlitEncoder
{
    public static VtBlitEncodeResult Encode(VtFrame current, VtFrame? baseline)
    {
        ArgumentNullException.ThrowIfNull(current);
        var full = RequiresFull(current, baseline);
        return EncodeRows(current, full ? null : baseline, full);
    }

    public static bool CellsVisuallyEqual(VtCellView a, VtCellView b) => a.VisuallyEquals(b);

    public static bool RequiresFull(VtFrame current, VtFrame? baseline)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (baseline is null)
            return true;
        if (baseline.Cols != current.Cols || baseline.Rows != current.Rows)
            return true;
        if (baseline.ViewportOrigin != current.ViewportOrigin)
            return true;
        if (baseline.OccupantGeneration != current.OccupantGeneration)
            return true;
        if (baseline.Generation > 0
            && current.Generation > 0
            && current.Generation > baseline.Generation + 1)
        {
            return true;
        }

        if (ModesRequireFull(baseline.Modes, current.Modes))
            return true;
        if (!string.Equals(baseline.ActiveScreen, current.ActiveScreen, StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>
    /// Mouse tracking/encoding do not change cells. A cells patch carries the
    // / token (VtCellPacker mouseChanged).
    /// </summary>
    private static bool ModesRequireFull(VtFrameModes a, VtFrameModes b) =>
        a.Origin != b.Origin
        || a.AutoWrap != b.AutoWrap
        || a.Insert != b.Insert
        || a.BracketedPaste != b.BracketedPaste
        || a.FocusReporting != b.FocusReporting
        || a.SynchronizedOutput != b.SynchronizedOutput;

    private static VtBlitEncodeResult EncodeRows(
        VtFrame current,
        VtFrame? baseline,
        bool full)
    {
        var sb = new StringBuilder(full ? current.Cols * current.Rows + 32 : 64);
        sb.Append("[?25l");
        var changed = 0;
        VtCellView? style = null;
        string? activeHyperlink = null;
        var lastCol = -2;
        var lastRow = -2;
        if (full)
        {
            sb.Append("\u001b[H\u001b[2J");
            lastCol = 0;
            lastRow = 0;
        }

        for (var r = 0; r < current.Rows; r++)
            EncodeRow(current, baseline, r, full, sb, ref style, ref activeHyperlink, ref lastCol, ref lastRow, ref changed);

        CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
        if (current.Cursor.HasCursor && current.Cursor.Visible)
        {
            sb.Append(CursorAddress(current.Cursor.Col, current.Cursor.Row));
            if (current.Cursor.Shape is >= 0 and <= 6)
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"\u001b[{current.Cursor.Shape} q"));
            sb.Append("\u001b[?25h");
        }

        var ansi = sb.ToString();
        return new VtBlitEncodeResult(ansi, full, changed, Encoding.UTF8.GetByteCount(ansi));
    }

    private static void EncodeRow(
        VtFrame current,
        VtFrame? baseline,
        int r,
        bool full,
        StringBuilder sb,
        ref VtCellView? style,
        ref string? activeHyperlink,
        ref int lastCol,
        ref int lastRow,
        ref int changed)
    {
        var col = 0;
        while (col < current.Cols)
        {
            var cell = current.CellAt(r, col);
            var prev = !full && baseline is not null
                ? baseline.CellAt(r, col)
                : VtCellView.Blank;
            var width = cell.Width > 0 ? cell.Width : 1;
            if (cell.IsContinuation)
            {
                col++;
                continue;
            }

            if (!full && cell.VisuallyEquals(prev))
            {
                col += width;
                continue;
            }

            if (lastCol != col || lastRow != r)
                sb.Append(CursorAddress(col, r));
            if (style is null || !SameStyle(style.Value, cell))
            {
                sb.Append("\u001b[0m");
                CellSgrEncoder.AppendSgr(sb, CellSgrEncoder.From(in cell));
                style = cell;
            }

            CellSgrEncoder.WriteHyperlinkIfChanged(sb, ref activeHyperlink, cell.Hyperlink);
            sb.Append(string.IsNullOrEmpty(cell.Text) ? " " : cell.Text);
            lastCol = col + width;
            lastRow = r;
            changed++;
            col += width;
        }
    }

    private static bool SameStyle(in VtCellView a, in VtCellView b) =>
        a.FgPacked == b.FgPacked
        && a.BgPacked == b.BgPacked
        && a.Modifier == b.Modifier
        && a.UnderlineColorPacked == b.UnderlineColorPacked
        && a.UnderlineStylePacked == b.UnderlineStylePacked
        && a.Hyperlink == b.Hyperlink;

    private static string CursorAddress(int col, int row)
    {
        var r = row + 1;
        var c = col + 1;
        return string.Create(CultureInfo.InvariantCulture, $"\u001b[{r};{c}H");
    }
}
