using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Copy;
using Hypa.Cli.Attach.Theme;

namespace Hypa.Cli.Attach.Keys;

/// <summary>Writes the reserved tab-row slot while a chrome mode is active.</summary>
public static class ModeBarPainter
{
    public static string Paint(AttachClientMode mode, ModeBarSlot slot) =>
        Paint(mode, slot, cols: 80, rows: 24, table: null);

    public static string Paint(
        AttachClientMode mode,
        ModeBarSlot slot,
        int cols,
        int rows,
        KeyBindingTable? table = null,
        string? text = null,
        CopyModeSession? copy = null,
        int? row = null,
        ThemePalette? theme = null)
    {
        var line = text;
        if (line is null)
        {
            var model = ModeBarModel.For(mode, table, slot, copy);
            if (!model.Visible)
                return "";
            line = model.Text;
        }

        if (row is { } explicitRow)
            return PaintLine(explicitRow, cols, line, theme);
        return PaintLine(slot, cols, rows, line, theme);
    }

    public static string PaintLine(int row, int cols, string line, ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (cols < 1)
            cols = 1;
        if (row < 0)
            row = 0;
        line = SafeDisplayText.PadRight(line, cols);
        theme ??= ThemePalette.Catppuccin;

        var sb = new StringBuilder();
        sb.Append('\u001b').Append('7');
        sb.Append("\u001b[").Append(row + 1).Append(";1H");
        ThemeSgr.WriteStyled(sb, theme.Accent, theme.PanelBg, line);
        sb.Append('\u001b').Append('8');
        return sb.ToString();
    }

    public static string PaintLine(ModeBarSlot slot, int cols, int rows, string line, ThemePalette? theme = null)
    {
        if (rows < 1)
            rows = 1;
        var row = slot is ModeBarSlot.Top ? 0 : rows - 1;
        return PaintLine(row, cols, line, theme);
    }

    /// <summary>
    /// the same host frame as chrome.
    /// </summary>
    internal static void Stamp(
        IHostCellSink sink,
        AttachClientMode mode,
        ModeBarSlot slot,
        int cols,
        int rows,
        KeyBindingTable? table = null,
        string? text = null,
        CopyModeSession? copy = null,
        int? row = null,
        ThemePalette? theme = null,
        int originCol = 0,
        string? endpointError = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        theme ??= ThemePalette.Catppuccin;
        var paintRow = row ?? (slot is ModeBarSlot.Top ? 0 : Math.Max(0, rows - 1));
        if (!string.IsNullOrWhiteSpace(endpointError))
        {
            StampError(sink, paintRow, cols, endpointError, theme, originCol);
            return;
        }

        if (mode is AttachClientMode.Terminal)
            return;

        var line = text;
        if (line is null)
        {
            var model = ModeBarModel.For(mode, table, slot, copy);
            if (!model.Visible)
                return;
            line = model.Text;
        }

        StampLine(sink, paintRow, cols, line, theme, originCol);
    }

    /// <summary>
    /// the message on the mode-bar row.
    /// </summary>
    internal static void StampError(
        IHostCellSink sink,
        int row,
        int cols,
        string error,
        ThemePalette? theme = null,
        int originCol = 0)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(error);
        if (cols < 1)
            cols = 1;
        if (row < 0)
            row = 0;
        if (originCol < 0)
            originCol = 0;
        theme ??= ThemePalette.Catppuccin;
        sink.Write(originCol, row, new string(' ', cols), theme.Overlay0, theme.PanelBg, cols);
        const string chip = " ERROR ";
        var chipWidth = Math.Min(cols, SafeDisplayText.Width(chip));
        if (chipWidth > 0)
        {
            sink.Write(
                originCol,
                row,
                SafeDisplayText.Clip(chip, chipWidth),
                TabBarPainter.ContrastForeground(theme),
                theme.Accent,
                chipWidth,
                bold: true);
        }

        var restCols = cols - chipWidth;
        if (restCols <= 0)
            return;
        var message = SafeDisplayText.PadRight(
            SafeDisplayText.Clip(" " + error.Trim(), restCols),
            restCols);
        sink.Write(originCol + chipWidth, row, message, theme.Overlay0, theme.PanelBg, restCols);
    }

    internal static void StampLine(
        IHostCellSink sink,
        int row,
        int cols,
        string line,
        ThemePalette? theme = null,
        int originCol = 0)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(line);
        if (cols < 1)
            cols = 1;
        if (row < 0)
            row = 0;
        if (originCol < 0)
            originCol = 0;
        theme ??= ThemePalette.Catppuccin;
        sink.Write(originCol, row, SafeDisplayText.PadRight(line, cols), theme.Accent, theme.PanelBg, cols);
    }
}
