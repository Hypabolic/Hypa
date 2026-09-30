using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>
// Fill
/// <c>panel_bg</c>, then stamp an accent box. Inner is inset by one cell.
/// </summary>
internal static class OverlayModalPanel
{
    public static CellRect? Stamp(IHostCellSink sink, CellRect outer, ThemePalette theme)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(theme);
        if (outer.Cols < 2 || outer.Rows < 2)
            return null;

        for (var r = outer.Row; r < outer.EndRow; r++)
            sink.Write(outer.Col, r, new string(' ', outer.Cols), theme.Text, theme.PanelBg, outer.Cols);

        var top = '┌' + new string('─', Math.Max(0, outer.Cols - 2)) + '┐';
        var bottom = '└' + new string('─', Math.Max(0, outer.Cols - 2)) + '┘';

        sink.Write(outer.Col, outer.Row, top, theme.Accent, theme.PanelBg, outer.Cols);
        sink.Write(outer.Col, outer.EndRow - 1, bottom, theme.Accent, theme.PanelBg, outer.Cols);
        for (var r = outer.Row + 1; r < outer.EndRow - 1; r++)
        {
            sink.Write(outer.Col, r, "│", theme.Accent, theme.PanelBg, 1);
            sink.Write(outer.EndCol - 1, r, "│", theme.Accent, theme.PanelBg, 1);
        }

        return new CellRect(outer.Col + 1, outer.Row + 1, outer.Cols - 2, outer.Rows - 2);
    }
}
