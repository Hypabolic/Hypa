using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Theme;

namespace Hypa.Cli.Attach.Copy;

/// <summary>Paints the pinned copy grid into a content rect. Selection uses theme.SelectionBg.</summary>
public static class CopyModePainter
{
    public static string Paint(
        CopyModeSession session,
        int originCol,
        int originRow,
        int cols,
        int rows,
        ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Paint(originCol, originRow, cols, rows, theme);
    }

    /// <summary>
    /// <c>inner_rect</c> then the mode bar. Selection uses theme.SelectionBg
    /// or reverse, matching <see cref="PaintUnlocked"/>.
    /// </summary>
    internal static void Stamp(HostFrame host, CopyModeSession session, CellRect box, ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(session);
        if (box.Cols < 1 || box.Rows < 1)
            return;
        var view = session.PreparePaintSnapshot(box.Rows);
        host.StampCopyView(view, box);
        if (!session.TryGetSelectionUnlocked(out var aRow, out var aCol, out var bRow, out var bCol))
            return;
        CopyModeBuffer.Normalize(aRow, aCol, bRow, bCol, out aRow, out aCol, out bRow, out bCol);
        var selected = theme is not null && !theme.SelectionBg.IsReset
            ? HostFrameCellSink.StyleOf(theme.ResolveSelectionFg(), theme.SelectionBg)
            : (AssembledStyle?)null;
        var top = Math.Max(0, view.ViewportTop);
        for (var i = 0; i < box.Rows; i++)
        {
            var srcRow = top + i;
            var hostRow = box.Row + i;
            if ((uint)hostRow >= (uint)host.Rows || srcRow >= view.RowCount || srcRow >= view.Lines.Count)
                continue;
            var line = view.Lines[srcRow];
            var written = 0;
            for (var c = 0; c < line.Count && written < box.Cols; c++)
            {
                var cell = line[c];
                if (cell.IsContinuation)
                    continue;
                var width = cell.Width > 0 ? cell.Width : 1;
                if (width > 1 && written + width > box.Cols)
                    break;
                if (InSelection(srcRow, c, aRow, aCol, bRow, bCol))
                {
                    var style = selected ?? cell.Style with { Inverse = true };
                    host.Stamp(
                        box.Col + written,
                        hostRow,
                        new AssembledCell(cell.Text, width, false, style));
                    for (var k = 1; k < width; k++)
                    {
                        host.Stamp(
                            box.Col + written + k,
                            hostRow,
                            new AssembledCell("", 1, true, style));
                    }
                }

                written += width;
            }
        }
    }

    internal static string PaintUnlocked(
        CopyModeSession session,
        int originCol,
        int originRow,
        int cols,
        int rows,
        ThemePalette? theme = null)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var view = session.PreparePaintSnapshot(rows);

        var hasSel = session.TryGetSelectionUnlocked(out var aRow, out var aCol, out var bRow, out var bCol);
        if (hasSel)
            CopyModeBuffer.Normalize(aRow, aCol, bRow, bCol, out aRow, out aCol, out bRow, out bCol);

        var sb = new StringBuilder();
        sb.Append(SnapshotPainter.HideCursor);
        sb.Append(SnapshotPainter.EraseRect(originCol, originRow, cols, rows));
        var top = view.ViewportTop;
        AssembledStyle? current = null;
        string? activeHyperlink = null;
        for (var i = 0; i < rows; i++)
        {
            var row = top + i;
            sb.Append(SnapshotPainter.CursorAddress(originCol, originRow + i));
            if (row >= view.RowCount)
            {
                CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
                sb.Append(SnapshotPainter.ResetSgr);
                current = null;
                sb.Append(' ', cols);
                continue;
            }

            var written = 0;
            var line = view.Lines[row];
            for (var c = 0; c < line.Count && written < cols; c++)
            {
                var cell = line[c];
                if (cell.IsContinuation)
                    continue;
                var width = cell.Width > 0 ? cell.Width : SafeDisplayText.Width(cell.Text);
                if (width <= 0)
                    width = 1;
                if (written + width > cols)
                    break;
                var selected = hasSel && InSelection(row, c, aRow, aCol, bRow, bCol);
                if (selected)
                {
                    CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
                    AppendSelectionOpen(sb, theme);
                    current = null;
                }
                else if (current is not { } liveStyle || !StylesEqual(liveStyle, cell.Style))
                {
                    CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
                    sb.Append(SnapshotPainter.ResetSgr);
                    CellSgrEncoder.AppendSgr(sb, cell.Style.ToSgr());
                    current = cell.Style;
                }

                CellSgrEncoder.WriteHyperlinkIfChanged(sb, ref activeHyperlink, cell.Style.Hyperlink);
                var text = SafeDisplayText.Encode(cell.Text);
                sb.Append(text.Length > 0 ? text : " ");
                if (selected)
                {
                    sb.Append(SnapshotPainter.ResetSgr);
                    current = null;
                }

                written += width;
            }

            if (written < cols)
                sb.Append(' ', cols - written);
        }

        CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
        sb.Append(SnapshotPainter.ResetSgr);

        var cursorRow = view.CursorRow - top;
        var cursorCol = Math.Clamp(view.CursorCol, 0, cols - 1);
        cursorRow = Math.Clamp(cursorRow, 0, rows - 1);
        if (view.CursorRow >= top && view.CursorRow < top + rows)
        {
            sb.Append(SnapshotPainter.CursorAddress(originCol + cursorCol, originRow + cursorRow));
            sb.Append(SnapshotPainter.ShowCursor);
        }
        else
            sb.Append(SnapshotPainter.HideCursor);

        return sb.ToString();
    }

    private static void AppendSelectionOpen(StringBuilder sb, ThemePalette? theme)
    {
        if (theme is not null && !theme.SelectionBg.IsReset)
        {
            ThemeSgr.AppendPair(sb, theme.ResolveSelectionFg(), theme.SelectionBg);
            return;
        }

        sb.Append("\u001b[7m");
    }

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

    private static bool InSelection(int row, int col, int r1, int c1, int r2, int c2)
    {
        if (row < r1 || row > r2)
            return false;
        if (row == r1 && col < c1)
            return false;
        if (row == r2 && col > c2)
            return false;
        return true;
    }
}
