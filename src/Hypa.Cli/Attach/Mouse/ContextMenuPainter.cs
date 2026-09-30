using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Theme;

namespace Hypa.Cli.Attach.Mouse;

public static class ContextMenuPainter
{
    public static string Paint(ContextMenuModel menu, ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(menu);
        theme ??= ThemePalette.Catppuccin;
        var sb = new StringBuilder();
        sb.Append('\u001b').Append('7');
        for (var i = 0; i < menu.Items.Count && i < menu.Rect.Rows; i++)
        {
            var item = menu.Items[i];
            var label = SafeDisplayText.PadRight(" " + item.Label, menu.Rect.Cols);
            sb.Append(SnapshotPainter.CursorAddress(menu.Rect.Col, menu.Rect.Row + i));
            var selected = i == menu.Selected;
            var fg = !item.Enabled
                ? theme.Overlay0
                : selected ? theme.ResolveSelectionFg() : theme.ResolveMenuBarFg();
            var bg = selected && item.Enabled ? theme.SelectionBg : theme.ResolveMenuBarBg();
            if (selected && item.Enabled && theme.SelectionBg.IsReset)
            {
                sb.Append("\u001b[7m");
                sb.Append(label);
                sb.Append(SnapshotPainter.ResetSgr);
            }
            else
                ThemeSgr.WriteStyled(sb, fg, bg, label);
        }

        sb.Append('\u001b').Append('8');
        return sb.ToString();
    }

    /// <summary>
    /// same host <c>Frame</c>.
    /// </summary>
    internal static void Stamp(
        IHostCellSink sink,
        ContextMenuModel menu,
        ThemePalette? theme = null,
        int hostCols = 0,
        int hostRows = 0)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(menu);
        theme ??= ThemePalette.Catppuccin;
        if (hostCols > 0 && hostRows > 0)
            ChromeShadow.Stamp(sink, menu.Rect, theme, hostCols, hostRows);
        for (var i = 0; i < menu.Items.Count && i < menu.Rect.Rows; i++)
        {
            var item = menu.Items[i];
            var label = SafeDisplayText.PadRight(" " + item.Label, menu.Rect.Cols);
            var selected = i == menu.Selected;
            var fg = !item.Enabled
                ? theme.Overlay0
                : selected ? theme.ResolveSelectionFg() : theme.ResolveMenuBarFg();
            var bg = selected && item.Enabled ? theme.SelectionBg : theme.ResolveMenuBarBg();
            // Terminal SelectionBg is Reset so reverse matches CopyModePainter.Stamp and Paint().
            var inverse = selected && item.Enabled && theme.SelectionBg.IsReset;
            sink.Write(menu.Rect.Col, menu.Rect.Row + i, label, fg, bg, menu.Rect.Cols, inverse);
        }
    }
}
