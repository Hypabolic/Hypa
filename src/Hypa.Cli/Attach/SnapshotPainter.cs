using System.Globalization;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Mouse;

namespace Hypa.Cli.Attach;

/// <summary>
/// Paints an assembled snapshot. SGR and alt-screen come only from the snapshot.
/// Basic VT invents no colour.
/// </summary>
public static class SnapshotPainter
{
    public const string EnterAltScreen = "\u001b[?1049h";
    public const string LeaveAltScreen = "\u001b[?1049l";
    /// <summary>Delimits composed attach paints only; live PTY bytes stay unwrapped.</summary>
    public const string BeginSynchronizedOutput = "\u001b[?2026h";
    public const string EndSynchronizedOutput = "\u001b[?2026l";
    public const string HideCursor = "\u001b[?25l";
    public const string ShowCursor = "\u001b[?25h";
    // / <summary>DECSCUSR default.
    public const string ResetCursorShape = "\u001b[0 q";
    public const string Home = "\u001b[H";
    public const string EraseDisplay = "\u001b[2J";
    public const string ResetSgr = "\u001b[0m";
    /// <summary>Host TTY enable for DECSET 1004 outer focus reports.</summary>
    public const string EnableFocusReport = "\u001b[?1004h";
    /// <summary>Outer TTY focus-in report (DECSET 1004).</summary>
    public const string FocusInReport = "\u001b[I";
    /// <summary>Outer TTY focus-out report (DECSET 1004).</summary>
    public const string FocusOutReport = "\u001b[O";
    public const string DisableFocusReport = "\u001b[?1004l";
    // / <summary>DEC autowrap off.
    public const string EnableBracketedPaste = "\u001b[?2004h";

    public const string DisableBracketedPaste = "\u001b[?2004l";

    public const string DisableLineWrap = "\u001b[?7l";
    /// <summary>DEC autowrap on. Host restore must send this after every attach.</summary>
    public const string EnableLineWrap = "\u001b[?7h";

    /// <summary>
    /// Always leave alt-screen, drop focus reports and bracketed paste, re-enable wrap,
    /// and reset SGR on detach. Live PTY bytes can enter 1049h after the first snapshot.
    /// </summary>
    public const string RestoreSequence =
        MouseCapture.DisableSequence + DisableFocusReport + DisableBracketedPaste
        + Hypa.AgentRuntime.Domain.Theme.HostThemeParser.DisableReports
        + LeaveAltScreen + EnableLineWrap + ResetSgr + ShowCursor + ResetCursorShape
        + EndSynchronizedOutput;

    public const string ResetScrollRegion = "\u001b[r";

    public static string CursorAddress(int col, int row)
    {
        var r = row + 1;
        var c = col + 1;
        return string.Create(CultureInfo.InvariantCulture, $"\u001b[{r};{c}H");
    }

    /// <summary>DECSCUSR. Parameter 0 is the terminal default.</summary>
    public static string CursorShapeSequence(int shape)
    {
        var ps = Math.Clamp(shape, 0, 6);
        return string.Create(CultureInfo.InvariantCulture, $"\u001b[{ps} q");
    }

    public static string SetScrollRegion(int topRow, int bottomRow)
    {
        var top = Math.Max(1, topRow + 1);
        var bottom = Math.Max(top, bottomRow + 1);
        return string.Create(CultureInfo.InvariantCulture, $"\u001b[{top};{bottom}r");
    }

    public static string SetScrollRegion(CellRect box) =>
        SetScrollRegion(box.Row, box.EndRow - 1);

    public static string EraseRect(int col, int row, int cols, int rows) =>
        EraseRect(col, row, cols, rows, occlude: null);

    public static string EraseRect(int col, int row, int cols, int rows, CellRect? occlude)
    {
        cols = Math.Max(0, cols);
        rows = Math.Max(0, rows);
        if (cols == 0 || rows == 0)
            return "";
        var sb = new StringBuilder();
        sb.Append(HideCursor);
        for (var r = 0; r < rows; r++)
        {
            var y = row + r;
            var x0 = col;
            var x1 = col + cols;
            if (occlude is { } occ && y >= occ.Row && y < occ.EndRow)
            {
                if (x0 < occ.Col)
                {
                    var left = Math.Min(x1, occ.Col) - x0;
                    if (left > 0)
                    {
                        sb.Append(CursorAddress(x0, y));
                        sb.Append(' ', left);
                    }
                }

                if (x1 > occ.EndCol)
                {
                    var start = Math.Max(x0, occ.EndCol);
                    var right = x1 - start;
                    if (right > 0)
                    {
                        sb.Append(CursorAddress(start, y));
                        sb.Append(' ', right);
                    }
                }

                continue;
            }

            sb.Append(CursorAddress(col, y));
            sb.Append(' ', cols);
        }

        return sb.ToString();
    }

    public static string Paint(
        AssembledSnapshot snapshot,
        bool ttyOnAlt = false,
        int originCol = 0,
        int originRow = 0,
        int clipCols = 0,
        int clipRows = 0,
        bool eraseDisplay = true,
        bool addressCursor = true,
        CellRect? occlude = null,
        AssembledSnapshot? previous = null,
        bool forceFullRedraw = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sb = new StringBuilder();
        var ghostty = string.Equals(snapshot.Provider, "ghostty", StringComparison.OrdinalIgnoreCase);
        var alt = ghostty && IsAlternateScreen(snapshot.ActiveScreen);
        if (alt)
            sb.Append(EnterAltScreen);
        else if (ttyOnAlt)
            sb.Append(LeaveAltScreen);

        sb.Append(HideCursor);
        var clipW = clipCols > 0 ? clipCols : snapshot.Cols;
        var clipH = clipRows > 0 ? clipRows : snapshot.Rows;
        var sizeChanged = previous is not null
            && (previous.Cols != snapshot.Cols || previous.Rows != snapshot.Rows);
        var occupantChanged = previous is not null
            && previous.OccupantGeneration != snapshot.OccupantGeneration
            && snapshot.OccupantGeneration > 0;
        var fullRedraw = forceFullRedraw
            || previous is null
            || sizeChanged
            || occupantChanged;
        if (fullRedraw)
        {
            if (eraseDisplay && occlude is null)
                sb.Append(EraseDisplay);
            else
                sb.Append(EraseRect(originCol, originRow, clipW, clipH, occlude));
        }

        var emitStyle = ghostty;
        var maxRows = Math.Min(snapshot.Rows, clipH);
        var maxCols = Math.Min(snapshot.Cols, clipW);
        string? activeHyperlink = null;
        if (fullRedraw)
            AppendAllCells(sb, snapshot, previous: null, originCol, originRow, maxRows, maxCols, emitStyle, occlude, ref activeHyperlink);
        else
            AppendChangedCells(sb, snapshot, previous!, originCol, originRow, maxRows, maxCols, emitStyle, occlude, ref activeHyperlink);

        if (emitStyle)
        {
            CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
            sb.Append(ResetSgr);
        }

        if (addressCursor)
        {
            var cursor = snapshot.Cursor ?? AssembledCursor.Default;
            var cursorCol = cursor.Col;
            var cursorRow = cursor.Row;
            if (clipCols > 0)
                cursorCol = Math.Clamp(cursorCol, 0, Math.Max(0, clipW - 1));
            if (clipRows > 0)
                cursorRow = Math.Clamp(cursorRow, 0, Math.Max(0, clipH - 1));
            sb.Append(CursorAddress(cursorCol + originCol, cursorRow + originRow));
            sb.Append(CursorShapeSequence(cursor.Shape));
            sb.Append(cursor.HasCursor && cursor.Visible ? ShowCursor : HideCursor);
        }

        return sb.ToString();
    }

    private static void AppendAllCells(
        StringBuilder sb,
        AssembledSnapshot snapshot,
        AssembledSnapshot? previous,
        int originCol,
        int originRow,
        int maxRows,
        int maxCols,
        bool emitStyle,
        CellRect? occlude,
        ref string? activeHyperlink)
    {
        _ = previous;
        AssembledStyle? current = null;
        for (var r = 0; r < maxRows && r < snapshot.Cells.Count; r++)
        {
            var row = snapshot.Cells[r];
            sb.Append(CursorAddress(originCol, originRow + r));
            current = null;
            var needCup = false;
            for (var c = 0; c < maxCols && c < row.Count; c++)
            {
                var cell = row[c];
                if (cell.IsContinuation)
                    continue;
                if (cell.Width > 0 && c + cell.Width > maxCols)
                    continue;
                if (occlude is { } occ && occ.Contains(originCol + c, originRow + r))
                {
                    needCup = true;
                    current = null;
                    continue;
                }

                if (needCup)
                {
                    sb.Append(CursorAddress(originCol + c, originRow + r));
                    needCup = false;
                    current = null;
                }

                AppendCell(sb, cell, emitStyle, ref current, ref activeHyperlink);
            }
        }
    }

    private static void AppendChangedCells(
        StringBuilder sb,
        AssembledSnapshot snapshot,
        AssembledSnapshot previous,
        int originCol,
        int originRow,
        int maxRows,
        int maxCols,
        bool emitStyle,
        CellRect? occlude,
        ref string? activeHyperlink)
    {
        AssembledStyle? current = null;
        for (var r = 0; r < maxRows && r < snapshot.Cells.Count; r++)
        {
            var row = snapshot.Cells[r];
            var prevRow = r < previous.Cells.Count ? previous.Cells[r] : null;
            var invalidated = 0;
            var toSkip = 0;
            for (var c = 0; c < maxCols; c++)
            {
                var cell = c < row.Count ? row[c] : AssembledCell.Blank;
                var prevCell = prevRow is not null && c < prevRow.Count
                    ? prevRow[c]
                    : AssembledCell.Blank;
                var occluded = occlude is { } occ && occ.Contains(originCol + c, originRow + r);
                var skip = cell.IsContinuation
                    || occluded
                    || (cell.Width > 0 && c + cell.Width > maxCols);
                if (!skip
                    && toSkip == 0
                    && (invalidated > 0 || !CellsEqual(cell, prevCell)))
                {
                    sb.Append(CursorAddress(originCol + c, originRow + r));
                    current = null;
                    AppendCell(sb, cell, emitStyle, ref current, ref activeHyperlink);
                }
                else if (skip || toSkip > 0)
                    current = null;

                toSkip = Math.Max(0, VisualWidth(cell) - 1);
                var affected = Math.Max(VisualWidth(cell), VisualWidth(prevCell));
                invalidated = Math.Max(affected, invalidated) - 1;
                if (invalidated < 0)
                    invalidated = 0;
            }
        }
    }

    private static int VisualWidth(AssembledCell cell) =>
        cell.Width > 0 ? cell.Width : 1;

    private static void AppendCell(
        StringBuilder sb,
        AssembledCell cell,
        bool emitStyle,
        ref AssembledStyle? current,
        ref string? activeHyperlink)
    {
        if (emitStyle)
        {
            if (current is not { } liveStyle || !StylesEqual(liveStyle, cell.Style))
            {
                CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
                sb.Append(ResetSgr);
                CellSgrEncoder.AppendSgr(sb, cell.Style.ToSgr());
                current = cell.Style;
            }

            CellSgrEncoder.WriteHyperlinkIfChanged(sb, ref activeHyperlink, cell.Style.Hyperlink);
        }

        sb.Append(cell.Text);
    }

    private static bool CellsEqual(AssembledCell a, AssembledCell b) =>
        a.Text == b.Text
        && a.Width == b.Width
        && a.IsContinuation == b.IsContinuation
        && StylesEqual(a.Style, b.Style);

    public static bool UsesAltScreen(AssembledSnapshot snapshot) =>
        string.Equals(snapshot.Provider, "ghostty", StringComparison.OrdinalIgnoreCase)
        && IsAlternateScreen(snapshot.ActiveScreen);

    public static bool EmitsStyle(AssembledSnapshot snapshot) =>
        string.Equals(snapshot.Provider, "ghostty", StringComparison.OrdinalIgnoreCase);

    private static bool IsAlternateScreen(string? screen) =>
        string.Equals(screen, "alternate", StringComparison.OrdinalIgnoreCase)
        || string.Equals(screen, "alt", StringComparison.OrdinalIgnoreCase);

    private static bool StylesEqual(AssembledStyle a, AssembledStyle b) =>
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
        && a.Hyperlink == b.Hyperlink;
}
