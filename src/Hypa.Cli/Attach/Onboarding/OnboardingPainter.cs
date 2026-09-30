using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Theme;

namespace Hypa.Cli.Attach.Onboarding;

/// <summary>Paints the first-run overlay. Title is hypa. Continue does not install.</summary>
public static class OnboardingPainter
{
    public const int TargetCols = 64;
    public const int TargetRows = 16;

    public static OnboardingLayout Measure(OnboardingOverlayModel model, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(model);
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var panelCols = Math.Min(cols, Math.Max(1, TargetCols));
        var panelRows = Math.Min(rows, Math.Max(1, TargetRows));
        var panelCol = Math.Max(0, (cols - panelCols) / 2);
        var panelRow = Math.Max(0, (rows - panelRows) / 2);
        var panel = new CellRect(panelCol, panelRow, panelCols, panelRows);
        var label = SafeDisplayText.Encode(OnboardingOverlayModel.ContinueLabel);
        var labelWidth = SafeDisplayText.Width(label);
        var innerStart = panelCol + Math.Min(2, Math.Max(0, panelCols - 1));
        var innerEnd = panel.EndCol - 1;
        var width = Math.Max(0, Math.Min(labelWidth, innerEnd - innerStart));
        var continueRow = panel.EndRow - 1;
        var cont = width > 0
            ? new CellRect(innerStart, continueRow, width, 1)
            : default;
        return new OnboardingLayout(panel, cont);
    }

    public static string Paint(
        OnboardingOverlayModel model,
        int cols,
        int rows,
        ThemePalette? theme = null,
        string? statusError = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        theme ??= ThemePalette.Catppuccin;
        var layout = Measure(model, cols, rows);
        model.Layout = layout;
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);

        var sb = new StringBuilder();
        sb.Append(SnapshotPainter.HideCursor);
        sb.Append(SnapshotPainter.EraseDisplay);
        sb.Append(SnapshotPainter.Home);
        for (var r = 0; r < rows; r++)
        {
            sb.Append(SnapshotPainter.CursorAddress(0, r));
            ThemeSgr.WriteStyled(sb, theme.Overlay0, theme.PanelBg, new string(' ', cols));
            if (r >= layout.Panel.Row && r < layout.Panel.EndRow)
            {
                sb.Append(SnapshotPainter.CursorAddress(layout.Panel.Col, r));
                PaintPanelRow(sb, model, layout, r, theme, statusError);
            }
        }

        sb.Append(SnapshotPainter.HideCursor);
        return sb.ToString();
    }

    /// <summary>
    /// first-run panel into the same host <c>Frame</c>.
    /// </summary>
    internal static void Stamp(
        IHostCellSink sink,
        OnboardingOverlayModel model,
        int cols,
        int rows,
        ThemePalette? theme = null,
        string? statusError = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(model);
        theme ??= ThemePalette.Catppuccin;
        var layout = Measure(model, cols, rows);
        model.Layout = layout;
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        for (var r = 0; r < rows; r++)
        {
            sink.Write(0, r, new string(' ', cols), theme.Overlay0, theme.PanelBg, cols);
            if (r >= layout.Panel.Row && r < layout.Panel.EndRow)
                StampPanelRow(sink, model, layout, r, theme, statusError);
        }
    }

    private static void StampPanelRow(
        IHostCellSink sink,
        OnboardingOverlayModel model,
        OnboardingLayout layout,
        int row,
        ThemePalette theme,
        string? statusError)
    {
        var panel = layout.Panel;
        var rel = row - panel.Row;
        if (row == layout.Continue.Row && layout.Continue.Cols > 0)
        {
            StampContinueRow(sink, layout, theme);
            return;
        }

        if (!string.IsNullOrWhiteSpace(statusError)
            && row == layout.Continue.Row - 1
            && row >= panel.Row)
        {
            var text = SafeDisplayText.Clip("  " + statusError.Trim(), panel.Cols);
            sink.Write(panel.Col, row, SafeDisplayText.PadRight(text, panel.Cols), theme.Red, theme.Surface0, panel.Cols);
            return;
        }

        var fg = rel == 0 ? theme.Accent : theme.Overlay1;
        var line = SafeDisplayText.PadRight(ContentLine(model, rel, panel.Rows), panel.Cols);
        sink.Write(panel.Col, row, line, fg, theme.Surface0, panel.Cols);
    }

    private static void StampContinueRow(IHostCellSink sink, OnboardingLayout layout, ThemePalette theme)
    {
        var panel = layout.Panel;
        var pad = Math.Max(0, layout.Continue.Col - panel.Col);
        if (pad > 0)
            sink.Write(panel.Col, layout.Continue.Row, new string(' ', pad), theme.Text, theme.Surface0, pad);
        var label = SafeDisplayText.Clip(
            OnboardingOverlayModel.ContinueLabel,
            layout.Continue.Cols);
        sink.Write(
            layout.Continue.Col,
            layout.Continue.Row,
            SafeDisplayText.PadRight(label, layout.Continue.Cols),
            theme.PanelBg,
            theme.Accent,
            layout.Continue.Cols);
        var written = pad + layout.Continue.Cols;
        if (written < panel.Cols)
        {
            sink.Write(
                panel.Col + written,
                layout.Continue.Row,
                new string(' ', panel.Cols - written),
                theme.Text,
                theme.Surface0,
                panel.Cols - written);
        }
    }

    private static void PaintPanelRow(
        StringBuilder sb,
        OnboardingOverlayModel model,
        OnboardingLayout layout,
        int row,
        ThemePalette theme,
        string? statusError)
    {
        var panel = layout.Panel;
        var rel = row - panel.Row;
        if (row == layout.Continue.Row && layout.Continue.Cols > 0)
        {
            PaintContinueRow(sb, layout, theme, padRight: panel.Cols);
            return;
        }

        if (!string.IsNullOrWhiteSpace(statusError)
            && row == layout.Continue.Row - 1
            && row >= panel.Row)
        {
            var text = SafeDisplayText.Clip("  " + statusError.Trim(), panel.Cols);
            ThemeSgr.WriteStyled(sb, theme.Red, theme.Surface0, SafeDisplayText.PadRight(text, panel.Cols));
            return;
        }

        ThemeColor fg = theme.Text;
        ThemeColor bg = theme.Surface0;
        var textLine = ContentLine(model, rel, panel.Rows);
        if (rel == 0)
            fg = theme.Accent;
        else
            fg = theme.Overlay1;

        var line = SafeDisplayText.PadRight(textLine, panel.Cols);
        ThemeSgr.WriteStyled(sb, fg, bg, line);
    }

    private static void PaintContinueRow(
        StringBuilder sb,
        OnboardingLayout layout,
        ThemePalette theme,
        int padRight)
    {
        var panel = layout.Panel;
        var written = 0;
        var pad = Math.Max(0, layout.Continue.Col - panel.Col);
        if (pad > 0)
        {
            ThemeSgr.WriteStyled(sb, theme.Text, theme.Surface0, new string(' ', pad));
            written += pad;
        }

        var label = SafeDisplayText.Clip(
            OnboardingOverlayModel.ContinueLabel,
            layout.Continue.Cols);
        ThemeSgr.WriteStyled(
            sb,
            theme.PanelBg,
            theme.Accent,
            SafeDisplayText.PadRight(label, layout.Continue.Cols));
        written += layout.Continue.Cols;
        if (written < padRight)
            ThemeSgr.WriteStyled(sb, theme.Text, theme.Surface0, new string(' ', padRight - written));
    }

    private static string ContentLine(OnboardingOverlayModel model, int rel, int panelRows)
    {
        var last = panelRows - 1;
        if (rel == last)
            return "";
        return rel switch
        {
            0 => "  " + OnboardingOverlayModel.Title,
            1 => "  " + OnboardingOverlayModel.Tagline,
            3 => "  " + OnboardingOverlayModel.MouseLine1,
            4 => "  " + OnboardingOverlayModel.MouseLine2,
            5 => "  " + OnboardingOverlayModel.MouseLine3,
            7 => PrefixLine(model),
            9 => "  " + OnboardingOverlayModel.NextLine,
            10 => "  " + OnboardingOverlayModel.NextLine2,
            _ => "",
        };
    }

    private static string PrefixLine(OnboardingOverlayModel model) =>
        "  " + model.PrefixLabel
            + OnboardingOverlayModel.PrefixHint
            + "?"
            + OnboardingOverlayModel.HelpHint;
}
