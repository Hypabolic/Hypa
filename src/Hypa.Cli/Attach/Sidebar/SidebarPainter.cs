using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Theme;

namespace Hypa.Cli.Attach.Sidebar;

internal static class SidebarPainter
{
    public static string Paint(
        SidebarFrame frame,
        int col,
        int row,
        int cols,
        int rows,
        ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Display is SidebarCollapseDisplay.Hidden || cols <= 0 || rows <= 0)
            return "";

        theme ??= ThemePalette.Catppuccin;
        var sb = new StringBuilder();
        var r = row;
        var compact = frame.Display is SidebarCollapseDisplay.Compact;
        foreach (var section in frame.Sections)
        {
            if (!section.Visible || r >= row + rows)
                break;
            if (!compact)
            {
                Write(sb, col, r, section.Title, cols, theme.Overlay1, theme.SidebarBg);
                r++;
            }

            if (section.Collapsed)
                continue;
            if (section.Rows.Count == 0 && !compact)
            {
                Write(sb, col, r, section.EmptyText, cols, theme.Overlay0, theme.SidebarBg);
                r++;
                continue;
            }

            foreach (var painted in section.Rows)
            {
                if (r >= row + rows)
                    break;
                var label = compact ? painted.CompactLabel : painted.Label;
                var fg = painted.Selected ? theme.ResolveSelectionFg() : theme.Subtext0;
                var bg = painted.Selected ? theme.ActiveRowBg : theme.SidebarBg;
                Write(sb, col, r, label, cols, fg, bg);
                r++;
            }
        }

        if (!compact && frame.Footer.Count > 0)
        {
            var footerCount = Math.Min(frame.Footer.Count, rows);
            var footerRow = row + rows - footerCount;
            foreach (var footer in frame.Footer)
            {
                if (footerRow >= row + rows)
                    break;
                Write(sb, col, footerRow, footer.Label, cols, theme.Overlay1, theme.SidebarBg);
                footerRow++;
            }
        }

        return sb.ToString();
    }

    private static void Write(
        StringBuilder sb,
        int col,
        int row,
        string text,
        int cols,
        ThemeColor fg,
        ThemeColor bg)
    {
        sb.Append(SnapshotPainter.CursorAddress(col, row));
        ThemeSgr.WriteStyled(sb, fg, bg, SafeDisplayText.PadRight(text, cols));
    }
}
