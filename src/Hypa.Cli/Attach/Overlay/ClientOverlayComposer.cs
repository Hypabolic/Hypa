using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;

namespace Hypa.Cli.Attach.Overlay;

/// <summary>
/// Stamps overlay chrome and pane snapshot cells into one host frame.
/// chrome, tiled panes, and notifications.
/// </summary>
internal static class ClientOverlayComposer
{
    internal const string OverlayTitle = " Agent ";

    public static void Stamp(
        HostFrame host,
        AssembledSnapshot snapshot,
        PopupGeometryResult geometry,
        ThemePalette theme,
        string? title = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(theme);
        var sink = new HostFrameCellSink(host);
        var frame = new PopupChromeFrame(
            new CellRect(geometry.OuterCol, geometry.OuterRow, geometry.OuterCols, geometry.OuterRows),
            new CellRect(geometry.InnerCol, geometry.InnerRow, geometry.InnerCols, geometry.InnerRows));
        StampChrome(sink, frame, theme, title);
        host.StampSnapshot(snapshot, frame.Inner);
    }

    public static void StampChrome(
        IHostCellSink sink,
        PopupChromeFrame frame,
        ThemePalette theme,
        string? title = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(theme);
        var outer = frame.Outer;
        if (outer.Cols < 1 || outer.Rows < 1)
            return;
        var glyphs = theme.ResolveGlyphs(ChromeGlyphSet.Unicode);
        var border = theme.ResolveFocusBorder();
        WriteFill(sink, outer.Col, outer.Row, outer.Cols, 1, glyphs.H, border, theme.Surface0);
        WriteFill(sink, outer.Col, outer.EndRow - 1, outer.Cols, 1, glyphs.H, border, theme.Surface0);
        WriteFill(sink, outer.Col, outer.Row, 1, outer.Rows, glyphs.V, border, theme.Surface0);
        WriteFill(sink, outer.EndCol - 1, outer.Row, 1, outer.Rows, glyphs.V, border, theme.Surface0);
        var titleWidth = Math.Max(0, outer.Cols - 2);
        if (titleWidth < 1)
            return;
        var label = string.IsNullOrWhiteSpace(title)
            ? OverlayTitle
            : " " + title.Trim() + " ";
        if (label.Length > titleWidth)
            label = label[..titleWidth];
        sink.Write(outer.Col + 1, outer.Row, label, theme.ResolveAccentFg(), theme.Accent, titleWidth);
    }

    public static EncodedHostBlit EncodeOnce(HostFrame host, HostBlitEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(encoder);
        return encoder.Encode(host);
    }

    private static void WriteFill(
        IHostCellSink sink,
        int col,
        int row,
        int cols,
        int rows,
        char glyph,
        ThemeColor fg,
        ThemeColor bg)
    {
        var text = new string(glyph, Math.Max(1, cols));
        for (var r = 0; r < rows; r++)
            sink.Write(col, row + r, text, fg, bg, cols);
    }
}
