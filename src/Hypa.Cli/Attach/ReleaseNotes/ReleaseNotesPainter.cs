using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;

namespace Hypa.Cli.Attach.ReleaseNotes;

/// <summary>
// / Paints the pack-notes modal.
/// <c>src/client/shell/overlays.rs:296-406</c> dims the host, then
/// <c>panel</c> plus <c>modal_stack_areas</c>.
/// </summary>
public static class ReleaseNotesPainter
{
    public const int HeaderRows = 2;
    public const int MinBodyRows = 4;

    public static ReleaseNotesLayout Measure(ReleaseNotesOverlayModel model, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(model);
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var panelCols = Math.Min(cols, Math.Max(4, TargetWidth(cols)));
        var panelRows = Math.Min(rows, Math.Max(4, TargetHeight(rows)));
        var panelCol = Math.Max(0, (cols - panelCols) / 2);
        var panelRow = Math.Max(0, (rows - panelRows) / 2);
        var panel = new CellRect(panelCol, panelRow, panelCols, panelRows);
        var inner = panel.Cols >= 2 && panel.Rows >= 2
            ? new CellRect(panelCol + 1, panelRow + 1, panelCols - 2, panelRows - 2)
            : panel;
        var title = HeaderLine(inner, 0);
        var subtitle = HeaderLine(inner, 1);
        var closeWidth = SafeDisplayText.Width(SafeDisplayText.Encode(ReleaseNotesOverlayModel.CloseLabel));
        var closeCol = Math.Max(title.Col, title.EndCol - closeWidth);
        var close = new CellRect(closeCol, title.Row, Math.Min(closeWidth, title.Cols), title.Rows);
        var footer = inner.Rows >= 4
            ? new CellRect(inner.Col, inner.EndRow - 1, inner.Cols, 1)
            : default;
        var bodyRow = inner.Row + HeaderRows + 1;
        var bodyEnd = footer.Rows > 0 ? footer.Row : inner.EndRow;
        var bodyRows = Math.Max(0, bodyEnd - bodyRow);
        if (bodyRows < MinBodyRows && inner.Rows > HeaderRows)
        {
            bodyRow = inner.Row + HeaderRows;
            bodyEnd = footer.Rows > 0 ? footer.Row : inner.EndRow;
            bodyRows = Math.Max(0, bodyEnd - bodyRow);
        }

        var wrapped = ReleaseNotesMarkdownLines.WrappedLineCount(model.DisplayLines, Math.Max(1, inner.Cols));
        var maxScroll = Math.Max(0, wrapped - Math.Max(1, bodyRows));
        var showTrack = maxScroll > 0 && inner.Cols > 2 && bodyRows > 0;
        var track = showTrack
            ? new CellRect(inner.EndCol - 1, bodyRow, 1, bodyRows)
            : (CellRect?)null;
        var bodyCols = showTrack ? Math.Max(1, inner.Cols - 1) : Math.Max(1, inner.Cols);
        var body = new CellRect(inner.Col, bodyRow, bodyCols, Math.Max(0, bodyRows));
        return new ReleaseNotesLayout(panel, title, subtitle, close, body, track, maxScroll, footer);
    }

    internal static void Stamp(
        IHostCellSink sink,
        ReleaseNotesOverlayModel model,
        int cols,
        int rows,
        ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(model);
        theme ??= ThemePalette.Catppuccin;
        var layout = Measure(model, cols, rows);
        model.Layout = layout;

        sink.DimAll();
        OverlayModalPanel.Stamp(sink, layout.Panel, theme);

        var wrapped = ReleaseNotesMarkdownLines.VisibleWrappedLines(
            model.DisplayLines,
            Math.Max(1, layout.Body.Cols),
            model.Scroll,
            Math.Max(1, layout.Body.Rows)).ToArray();

        StampHeader(sink, model, layout, theme);
        StampBody(sink, layout, theme, wrapped);
        StampFooter(sink, layout, theme);
        if (layout.ScrollTrack is { } track)
            StampScrollTrack(sink, layout, model, track, theme);
    }

    private static void StampHeader(
        IHostCellSink sink,
        ReleaseNotesOverlayModel model,
        ReleaseNotesLayout layout,
        ThemePalette theme)
    {
        var title = layout.Title;
        if (title.Cols > 0 && title.Rows > 0)
        {
            sink.Write(
                title.Col,
                title.Row,
                new string(' ', title.Cols),
                theme.Text,
                theme.PanelBg,
                title.Cols);
            sink.Write(
                title.Col,
                title.Row,
                SafeDisplayText.Clip(
                    ReleaseNotesOverlayModel.TitlePrefix + model.Version,
                    title.Cols),
                theme.Text,
                theme.PanelBg,
                title.Cols,
                bold: true);
            StampCloseButton(sink, layout.Close, theme);
        }

        var subtitle = layout.Subtitle;
        if (subtitle.Cols > 0 && subtitle.Rows > 0)
        {
            sink.Write(
                subtitle.Col,
                subtitle.Row,
                SafeDisplayText.PadRight(ReleaseNotesOverlayModel.Subtitle, subtitle.Cols),
                theme.Overlay1,
                theme.PanelBg,
                subtitle.Cols);
        }
    }

    private static void StampBody(
        IHostCellSink sink,
        ReleaseNotesLayout layout,
        ThemePalette theme,
        string[] wrapped)
    {
        var body = layout.Body;
        if (body.Cols <= 0 || body.Rows <= 0)
            return;
        for (var i = 0; i < body.Rows; i++)
        {
            var text = i < wrapped.Length ? wrapped[i] : "";
            sink.Write(
                body.Col,
                body.Row + i,
                SafeDisplayText.PadRight(text, body.Cols),
                theme.Text,
                theme.PanelBg,
                body.Cols);
        }
    }

    private static void StampFooter(IHostCellSink sink, ReleaseNotesLayout layout, ThemePalette theme)
    {
        var footer = layout.Footer;
        if (footer.Cols <= 0 || footer.Rows <= 0)
            return;
        sink.Write(footer.Col, footer.Row, new string(' ', footer.Cols), theme.Overlay0, theme.PanelBg, footer.Cols);
        var col = footer.Col;
        col += WriteHint(sink, col, footer.Row, footer.EndCol, " " + ReleaseNotesOverlayModel.FooterScrollHint + " ", theme.Overlay0, theme.PanelBg);
        col += WriteHint(sink, col, footer.Row, footer.EndCol, ReleaseNotesOverlayModel.FooterScrollKeys, theme.Text, theme.PanelBg);
        col += WriteHint(sink, col, footer.Row, footer.EndCol, "  ·  ", theme.Overlay0, theme.PanelBg);
        col += WriteHint(sink, col, footer.Row, footer.EndCol, ReleaseNotesOverlayModel.FooterCloseHint, theme.Overlay0, theme.PanelBg);
        WriteHint(sink, col, footer.Row, footer.EndCol, " " + ReleaseNotesOverlayModel.FooterCloseKeys + " ", theme.Text, theme.PanelBg);
    }

    private static int WriteHint(
        IHostCellSink sink,
        int col,
        int row,
        int endCol,
        string text,
        ThemeColor fg,
        ThemeColor bg)
    {
        var budget = endCol - col;
        if (budget <= 0)
            return 0;
        var clipped = SafeDisplayText.Clip(text, budget);
        var width = SafeDisplayText.Width(clipped);
        if (width <= 0)
            return 0;
        sink.Write(col, row, clipped, fg, bg, width);
        return width;
    }

    private static void StampCloseButton(IHostCellSink sink, CellRect close, ThemePalette theme)
    {
        if (close.Cols <= 0)
            return;
        var label = SafeDisplayText.Clip(ReleaseNotesOverlayModel.CloseLabel, close.Cols);
        sink.Write(
            close.Col,
            close.Row,
            SafeDisplayText.PadRight(label, close.Cols),
            theme.PanelBg,
            theme.Accent,
            close.Cols,
            bold: true);
    }

    private static void StampScrollTrack(
        IHostCellSink sink,
        ReleaseNotesLayout layout,
        ReleaseNotesOverlayModel model,
        CellRect track,
        ThemePalette theme)
    {
        if (track.Rows <= 0)
            return;

        var viewport = layout.Body.Rows;
        var total = viewport + layout.MaxScroll;
        var thumbRows = total <= 0
            ? track.Rows
            : Math.Max(1, (int)Math.Round((double)viewport / total * track.Rows));
        var thumbStart = layout.MaxScroll <= 0
            ? 0
            : (int)Math.Round((double)model.Scroll / layout.MaxScroll * Math.Max(0, track.Rows - thumbRows));

        for (var r = 0; r < track.Rows; r++)
        {
            var glyph = r >= thumbStart && r < thumbStart + thumbRows ? '█' : '│';
            sink.Write(track.Col, track.Row + r, glyph.ToString(), theme.Overlay1, theme.PanelBg, 1);
        }
    }

    private static CellRect HeaderLine(CellRect inner, int offset)
    {
        if (inner.Cols <= 0 || inner.Rows <= offset)
            return default;
        var col = inner.Col + (inner.Cols > 2 ? 1 : 0);
        var cols = Math.Max(0, inner.EndCol - 1 - col);
        return new CellRect(col, inner.Row + offset, cols, 1);
    }

    private static int TargetWidth(int cols) =>
        Math.Min(
            ReleaseNotesOverlayModel.TargetCols,
            Math.Max(4, cols - 4));

    private static int TargetHeight(int rows) =>
        Math.Min(
            ReleaseNotesOverlayModel.TargetRows,
            Math.Max(4, rows - 2));
}
