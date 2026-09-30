using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>
/// <c>src/ui/scrollbar.rs:28-193</c>. One stable gutter. No close cell.
/// </summary>
public readonly record struct PaneChromeScrollState(
    bool AlternateScreen = false,
    bool MetricsKnown = false,
    int OffsetFromBottom = 0,
    int MaxOffsetFromBottom = 0,
    int ViewportRows = 0)
{
    public static PaneChromeScrollState Unknown { get; } = default;

    public bool ShowsScrollbar => MetricsKnown && MaxOffsetFromBottom > 0;
}

public readonly record struct PaneScrollbarThumb(int Top, int Length);

internal static class PaneChromeGutter
{
    public const string TrackGlyph = "▕";
    public const string FocusedThumbGlyph = "▐";

    public static bool ReservesGutter(
        AttachUiConfig ui,
        bool hideChrome,
        CellRect inner,
        PaneChromeScrollState scroll)
    {
        ArgumentNullException.ThrowIfNull(ui);
        return ui.PaneScrollbars
            && !hideChrome
            && inner.Cols > 4
            && !scroll.AlternateScreen;
    }

    public static ChromePaneFrame Frame(
        string id,
        string label,
        CellRect frame,
        CellRect inner,
        AttachUiConfig ui,
        bool hideChrome,
        PaneChromeBorders borders,
        bool focused,
        PaneChromeScrollState scroll)
    {
        ArgumentNullException.ThrowIfNull(ui);
        var cols = Math.Max(0, inner.Cols);
        var rows = Math.Max(0, inner.Rows);
        var content = new CellRect(inner.Col, inner.Row, cols, rows);
        CellRect? gutter = null;
        CellRect? scrollbar = null;
        if (ReservesGutter(ui, hideChrome, inner, scroll))
        {
            content = new CellRect(inner.Col, inner.Row, cols - 1, rows);
            gutter = new CellRect(inner.Col + cols - 1, inner.Row, 1, rows);
            if (scroll.ShowsScrollbar)
                scrollbar = gutter;
        }

        return new ChromePaneFrame(
            id,
            label,
            frame,
            content,
            scrollbar,
            Close: null,
            borders,
            focused,
            gutter,
            scroll,
            hideChrome);
    }

    public static LayoutChromeGeometry Bind(
        LayoutChromeGeometry geometry,
        AttachUiConfig ui,
        IReadOnlyDictionary<string, PaneChromeScrollState>? states)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(ui);
        if (geometry.Panes.Count == 0)
            return geometry;

        var panes = new List<ChromePaneFrame>(geometry.Panes.Count);
        foreach (var pane in geometry.Panes)
        {
            var scroll = states is not null && states.TryGetValue(pane.PaneId, out var bound)
                ? bound
                : pane.Scroll;
            var inner = PaneLineCells.InnerRect(pane.Frame, pane.Borders);
            panes.Add(Frame(
                pane.PaneId,
                pane.Label,
                pane.Frame,
                inner,
                ui,
                pane.HideChrome,
                pane.Borders,
                pane.Focused,
                scroll));
        }

        return geometry with { Panes = panes };
    }

    public static PaneScrollbarThumb? Thumb(PaneChromeScrollState metrics, CellRect track)
    {
        if (metrics.MaxOffsetFromBottom <= 0 || track.Rows <= 0)
            return null;

        var trackHeight = track.Rows;
        var totalRows = metrics.MaxOffsetFromBottom + metrics.ViewportRows;
        if (totalRows <= 0)
            return null;

        var thumbLen = RoundHerdr(metrics.ViewportRows * (float)trackHeight / totalRows);
        thumbLen = Math.Clamp(Math.Max(1, thumbLen), 1, trackHeight);
        var maxThumbTop = trackHeight - thumbLen;
        var scrolledFromTop = metrics.MaxOffsetFromBottom - metrics.OffsetFromBottom;
        if (scrolledFromTop < 0)
            scrolledFromTop = 0;
        var thumbTop = 0;
        if (maxThumbTop > 0 && metrics.MaxOffsetFromBottom > 0)
        {
            thumbTop = RoundHerdr(scrolledFromTop * (float)maxThumbTop / metrics.MaxOffsetFromBottom);
            thumbTop = Math.Clamp(thumbTop, 0, maxThumbTop);
        }

        return new PaneScrollbarThumb(track.Row + thumbTop, thumbLen);
    }

    public static int? ThumbGrabOffset(PaneChromeScrollState metrics, CellRect track, int row)
    {
        var thumb = Thumb(metrics, track);
        if (thumb is not { } bar)
            return null;
        if (row < bar.Top || row >= bar.Top + bar.Length)
            return null;
        return row - bar.Top;
    }

    public static int OffsetFromRow(PaneChromeScrollState metrics, CellRect track, int row)
    {
        var thumb = Thumb(metrics, track);
        if (thumb is null)
            return 0;
        var clamped = Math.Clamp(row, track.Row, track.Row + Math.Max(0, track.Rows - 1));
        var rowOffset = clamped - track.Row;
        var desiredTop = rowOffset - (thumb.Value.Length / 2);
        if (desiredTop < 0)
            desiredTop = 0;
        return OffsetFromThumbTop(metrics, track, desiredTop);
    }

    public static int OffsetFromDragRow(
        PaneChromeScrollState metrics,
        CellRect track,
        int row,
        int grabRowOffset)
    {
        var clamped = Math.Clamp(row, track.Row, track.Row + Math.Max(0, track.Rows - 1));
        var rowOffset = clamped - track.Row;
        var desiredTop = rowOffset - Math.Max(0, grabRowOffset);
        if (desiredTop < 0)
            desiredTop = 0;
        return OffsetFromThumbTop(metrics, track, desiredTop);
    }

    public static void Stamp(IHostCellSink sink, ChromePaneFrame pane, ThemePalette theme)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(theme);
        if (pane.Scrollbar is { } track && pane.Scroll.ShowsScrollbar)
        {
            StampScrollbar(sink, pane.Scroll, track, theme, pane.Focused);
            return;
        }

        if (pane.Gutter is { } gutter)
            Fill(sink, gutter, " ", fg: null, bg: null);
    }

    internal static void StampScrollbar(
        IHostCellSink sink,
        PaneChromeScrollState metrics,
        CellRect track,
        ThemePalette theme,
        bool focused)
    {
        if (metrics.MaxOffsetFromBottom <= 0)
            return;
        var thumb = Thumb(metrics, track);
        if (thumb is null)
            return;

        var trackFg = focused ? theme.Overlay0 : theme.SurfaceDim;
        var thumbFg = focused ? theme.Overlay1 : theme.Overlay0;
        var glyphs = theme.ResolveGlyphs(ChromeGlyphSet.Unicode);
        var trackGlyph = theme.Chrome.ScrollbarArrows ? glyphs.Scrollbar.ToString() : TrackGlyph;
        var thumbGlyph = theme.Chrome.ScrollbarArrows
            ? glyphs.ScrollThumb.ToString()
            : (focused ? FocusedThumbGlyph : TrackGlyph);
        Fill(sink, track, trackGlyph, trackFg, bg: null);
        if (theme.Chrome.ScrollbarArrows && track.Rows >= 2)
        {
            sink.Write(track.Col, track.Row, glyphs.ScrollUp.ToString(), thumbFg, bg: null, 1);
            sink.Write(track.Col, track.EndRow - 1, glyphs.ScrollDown.ToString(), thumbFg, bg: null, 1);
        }
        var thumbTop = thumb.Value.Top;
        var thumbEnd = thumb.Value.Top + thumb.Value.Length;
        if (theme.Chrome.ScrollbarArrows && track.Rows >= 2)
        {
            thumbTop = Math.Max(thumbTop, track.Row + 1);
            thumbEnd = Math.Min(thumbEnd, track.EndRow - 1);
        }
        for (var y = thumbTop; y < thumbEnd; y++)
            sink.Write(track.Col, y, thumbGlyph, thumbFg, bg: null, 1);
    }

    private static int OffsetFromThumbTop(
        PaneChromeScrollState metrics,
        CellRect track,
        int thumbTop)
    {
        if (metrics.MaxOffsetFromBottom <= 0)
            return 0;

        var thumbLen = Thumb(metrics, track)?.Length ?? 1;
        var maxThumbTop = track.Rows - Math.Min(thumbLen, track.Rows);
        if (maxThumbTop <= 0)
            return 0;

        var desiredTop = Math.Min(thumbTop, maxThumbTop);
        var scrolledFromTop = RoundHerdr(desiredTop * (float)metrics.MaxOffsetFromBottom / maxThumbTop);
        return Math.Max(0, metrics.MaxOffsetFromBottom - scrolledFromTop);
    }

    private static int RoundHerdr(float value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static void Fill(
        IHostCellSink sink,
        CellRect rect,
        string glyph,
        ThemeColor? fg,
        ThemeColor? bg)
    {
        for (var r = rect.Row; r < rect.EndRow; r++)
            sink.Write(rect.Col, r, glyph, fg, bg, rect.Cols);
    }
}
