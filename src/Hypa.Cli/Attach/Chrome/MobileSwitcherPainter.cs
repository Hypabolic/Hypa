using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Theme;

namespace Hypa.Cli.Attach.Chrome;

public static class MobileSwitcherPainter
{
    public static string Paint(
        MobileSwitcherModel switcher,
        MobileHeaderModel? header,
        int cols,
        int rows,
        ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(switcher);
        if (!switcher.Open)
            return "";

        theme ??= ThemePalette.Catppuccin;
        var sb = new StringBuilder();
        var width = Math.Max(1, cols);
        var height = Math.Max(1, rows);
        sb.Append(SnapshotPainter.HideCursor);
        sb.Append(SnapshotPainter.EraseDisplay);
        sb.Append(SnapshotPainter.Home);
        for (var r = 0; r < height; r++)
            Write(sb, 0, r, "", width, theme.Text, theme.PanelBg);

        if (header is not null)
        {
            Write(sb, 0, 0, " switch", header.Status.Cols > 0 ? header.Status.Cols : width, theme.Text, theme.PanelBg);
            Fill(sb, switcher.Close, ' ', theme.Accent, theme.PanelBg);
            Write(
                sb,
                switcher.Close.Col,
                switcher.Close.Row,
                Center("close", switcher.Close.Cols),
                switcher.Close.Cols,
                theme.Accent,
                theme.PanelBg);
            if (switcher.Close.Rows > 1)
            {
                Write(
                    sb,
                    switcher.Close.Col,
                    switcher.Close.Row + 1,
                    Center("x", switcher.Close.Cols),
                    switcher.Close.Cols,
                    theme.Accent,
                    theme.PanelBg);
            }
        }

        // Build drops the rule when header + viewport + toast already fill MinRows.
        var headerRows = header?.Rect.Rows ?? NarrowLayout.HeaderRowsFor(height);
        if (switcher.Viewport.Row > headerRows && switcher.Viewport.Row < height)
            Write(sb, 0, switcher.Viewport.Row - 1, new string('─', width), width, theme.Overlay0, theme.PanelBg);

        foreach (var row in switcher.Visible)
        {
            var mark = row.Selected && row.Hit ? ">" : row.Hit ? " " : "";
            var bodyCols = row.CloseRect.Cols > 0
                ? Math.Max(0, row.Rect.Cols - row.CloseRect.Cols)
                : row.Rect.Cols;
            var text = mark.Length > 0 ? mark + row.Label : row.Label;
            var selected = row.Selected && row.Hit;
            var fg = selected ? theme.ResolveSelectionFg() : theme.Subtext0;
            var bg = selected ? theme.SelectionBg : theme.PanelBg;
            Write(sb, row.Rect.Col, row.Rect.Row, text, bodyCols, fg, bg);
            if (row.CloseRect.Cols > 0)
                Write(sb, row.CloseRect.Col, row.CloseRect.Row, "x", row.CloseRect.Cols, theme.Overlay1, bg);
        }

        return sb.ToString();
    }

    /// <summary>
    /// same host <c>Frame</c>.
    /// </summary>
    internal static void Stamp(
        IHostCellSink sink,
        MobileSwitcherModel switcher,
        MobileHeaderModel? header,
        int cols,
        int rows,
        ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(switcher);
        if (!switcher.Open)
            return;
        theme ??= ThemePalette.Catppuccin;
        var width = Math.Max(1, cols);
        var height = Math.Max(1, rows);
        for (var r = 0; r < height; r++)
            sink.Write(0, r, new string(' ', width), theme.Text, theme.PanelBg, width);

        if (header is not null)
        {
            sink.Write(
                0,
                0,
                SafeDisplayText.PadRight(" switch", header.Status.Cols > 0 ? header.Status.Cols : width),
                theme.Text,
                theme.PanelBg,
                header.Status.Cols > 0 ? header.Status.Cols : width);
            Fill(sink, switcher.Close, ' ', theme.Accent, theme.PanelBg);
            sink.Write(
                switcher.Close.Col,
                switcher.Close.Row,
                Center("close", switcher.Close.Cols),
                theme.Accent,
                theme.PanelBg,
                switcher.Close.Cols);
            if (switcher.Close.Rows > 1)
            {
                sink.Write(
                    switcher.Close.Col,
                    switcher.Close.Row + 1,
                    Center("x", switcher.Close.Cols),
                    theme.Accent,
                    theme.PanelBg,
                    switcher.Close.Cols);
            }
        }

        var headerRows = header?.Rect.Rows ?? NarrowLayout.HeaderRowsFor(height);
        if (switcher.Viewport.Row > headerRows && switcher.Viewport.Row < height)
            sink.Write(0, switcher.Viewport.Row - 1, new string('─', width), theme.Overlay0, theme.PanelBg, width);

        foreach (var row in switcher.Visible)
        {
            var mark = row.Selected && row.Hit ? ">" : row.Hit ? " " : "";
            var bodyCols = row.CloseRect.Cols > 0
                ? Math.Max(0, row.Rect.Cols - row.CloseRect.Cols)
                : row.Rect.Cols;
            var text = mark.Length > 0 ? mark + row.Label : row.Label;
            var selected = row.Selected && row.Hit;
            var fg = selected ? theme.ResolveSelectionFg() : theme.Subtext0;
            var bg = selected ? theme.SelectionBg : theme.PanelBg;
            sink.Write(row.Rect.Col, row.Rect.Row, text, fg, bg, bodyCols);
            if (row.CloseRect.Cols > 0)
                sink.Write(row.CloseRect.Col, row.CloseRect.Row, "x", theme.Overlay1, bg, row.CloseRect.Cols);
        }
    }

    private static void Fill(IHostCellSink sink, CellRect rect, char ch, ThemeColor? fg, ThemeColor? bg)
    {
        var fill = new string(ch, Math.Max(0, rect.Cols));
        for (var r = rect.Row; r < rect.EndRow; r++)
            sink.Write(rect.Col, r, fill, fg, bg, rect.Cols);
    }

    private static string Center(string text, int cols)
    {
        text = SafeDisplayText.Clip(text, Math.Max(1, cols));
        var width = SafeDisplayText.Width(text);
        if (width >= cols)
            return SafeDisplayText.PadRight(text, cols);
        var pad = (cols - width) / 2;
        return new string(' ', pad) + SafeDisplayText.PadRight(text, cols - pad);
    }

    private static void Write(
        StringBuilder sb,
        int col,
        int row,
        string text,
        int cols,
        ThemeColor? fg,
        ThemeColor? bg)
    {
        sb.Append(SnapshotPainter.CursorAddress(col, row));
        ThemeSgr.WriteStyled(sb, fg, bg, SafeDisplayText.PadRight(text, cols));
    }

    private static void Fill(StringBuilder sb, CellRect rect, char ch, ThemeColor? fg, ThemeColor? bg)
    {
        for (var r = rect.Row; r < rect.EndRow; r++)
        {
            sb.Append("\u001b[").Append(r + 1).Append(';').Append(rect.Col + 1).Append('H');
            ThemeSgr.WriteStyled(sb, fg, bg, new string(ch, Math.Max(0, rect.Cols)));
        }
    }
}
