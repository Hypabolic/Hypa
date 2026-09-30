using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>
// / Stamps tab-bar spans into the host frame.
/// <c>src/client/shell/tabs.rs</c> 7-227 paints each tab with its own style.
/// </summary>
internal static class TabBarPainter
{
    public static ThemeColor ContrastForeground(ThemePalette theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return theme.ResolveAccentFg();
    }

    public static void Stamp(
        IHostCellSink sink,
        TabBarModel bar,
        CellRect tabRow,
        ThemePalette theme,
        AttachUiConfig ui)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(ui);
        if (!bar.Visible || tabRow.Cols < 1 || tabRow.Rows < 1)
            return;

        var barFg = theme.ResolveMenuBarFg();
        var barBg = theme.ResolveMenuBarBg();
        Fill(sink, tabRow, barFg, barBg);

        foreach (var tab in bar.Tabs)
        {
            var (fg, bg, bold, dim) = tab.Active
                ? (ContrastForeground(theme), theme.Accent, tab.CustomLabel, false)
                : tab.CustomLabel
                    ? (theme.Overlay1, theme.Chrome.MenuBarBg is null ? theme.Surface0 : barBg, false, false)
                    : (theme.Chrome.MenuBarBg is null ? theme.Overlay0 : barFg,
                        theme.Chrome.MenuBarBg is null ? theme.Surface0 : barBg,
                        false,
                        theme.Chrome.MenuBarBg is null);
            Write(sink, tab.Rect, tab.Display, fg, bg, bold, dim);
        }

        if (bar.OverflowPrev is { } prev)
        {
            var fg = bar.HiddenBefore
                ? (theme.Chrome.MenuBarBg is null ? theme.Overlay1 : barFg)
                : (theme.Chrome.MenuBarBg is null ? theme.Overlay0 : barFg);
            var bg = theme.Chrome.MenuBarBg is null ? theme.Surface0 : barBg;
            Write(sink, prev, " < ", fg, bg);
        }

        if (bar.OverflowNext is { } next)
        {
            var fg = bar.HiddenAfter
                ? (theme.Chrome.MenuBarBg is null ? theme.Overlay1 : barFg)
                : (theme.Chrome.MenuBarBg is null ? theme.Overlay0 : barFg);
            var bg = theme.Chrome.MenuBarBg is null ? theme.Surface0 : barBg;
            Write(sink, next, " > ", fg, bg);
        }

        if (bar.NewTab is { } plus)
            Write(sink, plus, " + ", theme.Chrome.MenuBarBg is null ? theme.Overlay1 : barFg, barBg);

        if (bar.HiddenBefore)
        {
            var col = bar.OverflowPrev is { } left ? left.EndCol : tabRow.Col;
            if (col >= tabRow.Col && col < tabRow.EndCol)
                sink.Write(col, tabRow.Row, "…", theme.Chrome.MenuBarBg is null ? theme.Overlay0 : barFg, barBg, 1);
        }

        if (bar.HiddenAfter)
        {
            var col = bar.OverflowNext is { } right
                ? right.Col - 1
                : tabRow.EndCol - 1;
            if (col >= tabRow.Col && col < tabRow.EndCol)
                sink.Write(col, tabRow.Row, "…", theme.Chrome.MenuBarBg is null ? theme.Overlay0 : barFg, barBg, 1);
        }

        StampRight(sink, bar, tabRow, theme, ui);
    }

    private static void StampRight(
        IHostCellSink sink,
        TabBarModel bar,
        CellRect tabRow,
        ThemePalette theme,
        AttachUiConfig ui)
    {
        var barBg = theme.ResolveMenuBarBg();
        if (bar.RightItems.Count == 0)
            return;
        var sep = SafeDisplayText.Encode(ui.TabBarRightSeparator ?? " ");
        if (sep.Length == 0)
            sep = " ";
        var sepWidth = SafeDisplayText.Width(sep);
        var width = 0;
        for (var i = 0; i < bar.RightItems.Count; i++)
        {
            if (i > 0)
                width += sepWidth;
            width += bar.RightItems[i].Width;
        }

        if (width <= 0)
            return;
        var x = tabRow.EndCol - width;
        if (x < tabRow.Col)
            x = tabRow.Col;
        for (var i = 0; i < bar.RightItems.Count; i++)
        {
            if (i > 0 && sepWidth > 0 && x < tabRow.EndCol)
            {
                sink.Write(x, tabRow.Row, sep, theme.ResolveMenuBarFg(), barBg, sepWidth);
                x += sepWidth;
            }

            var item = bar.RightItems[i];
            if (item.Width <= 0 || x >= tabRow.EndCol)
                continue;
            if (item.Kind is TabBarRightKind.Zoom)
            {
                sink.Write(
                    x,
                    tabRow.Row,
                    item.Text,
                    ContrastForeground(theme),
                    theme.Accent,
                    item.Width,
                    bold: true);
            }
            else
            {
                sink.Write(x, tabRow.Row, item.Text, theme.ResolveMenuBarFg(), barBg, item.Width);
            }

            x += item.Width;
        }
    }

    private static void Write(
        IHostCellSink sink,
        CellRect rect,
        string text,
        ThemeColor fg,
        ThemeColor bg,
        bool bold = false,
        bool dim = false)
    {
        if (rect.Cols < 1)
            return;
        var clipped = SafeDisplayText.PadRight(SafeDisplayText.Clip(text, rect.Cols), rect.Cols);
        sink.Write(rect.Col, rect.Row, clipped, fg, bg, rect.Cols, bold: bold, dim: dim);
    }

    private static void Fill(IHostCellSink sink, CellRect rect, ThemeColor fg, ThemeColor bg)
    {
        var fill = new string(' ', Math.Max(0, rect.Cols));
        for (var r = rect.Row; r < rect.EndRow; r++)
            sink.Write(rect.Col, r, fill, fg, bg, rect.Cols);
    }
}
