using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Theme;

namespace Hypa.Cli.Attach.Popup;

/// <summary>Overlays the session-modal popup without mutating the split tree.</summary>
public static class PopupPainter
{
    public const string Title = "popup";

    public static string Paint(
        LayoutChromeGeometry geometry,
        AssembledSnapshot? snapshot,
        ThemePalette? theme = null,
        bool fillInner = false)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.PopupFrame is not { } frame)
            return "";
        return Paint(frame, snapshot, theme, fillInner);
    }

    public static string Paint(
        PopupChromeFrame frame,
        AssembledSnapshot? snapshot,
        ThemePalette? theme = null,
        bool fillInner = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        theme ??= ThemePalette.Catppuccin;
        var sb = new StringBuilder();
        var outer = frame.Outer;
        var inner = frame.Inner;
        if (outer.Cols < 1 || outer.Rows < 1)
            return "";

        PaintBorder(sb, outer, theme);
        var titleWidth = Math.Max(0, outer.Cols - 2);
        var title = SafeDisplayText.Clip(" " + Title + " ", titleWidth);
        if (titleWidth > 0)
        {
            sb.Append(SnapshotPainter.CursorAddress(outer.Col + 1, outer.Row));
            ThemeSgr.WriteStyled(sb, theme.ResolveAccentFg(), theme.Accent, SafeDisplayText.PadRight(title, titleWidth));
        }

        PaintScrollbar(sb, frame, theme);

        if (inner.Cols > 0 && inner.Rows > 0)
        {
            if (fillInner)
            {
                for (var r = inner.Row; r < inner.EndRow; r++)
                {
                    sb.Append(SnapshotPainter.CursorAddress(inner.Col, r));
                    ThemeSgr.WriteStyled(sb, theme.Text, theme.PanelBg, new string(' ', inner.Cols));
                }
            }

            if (snapshot is not null)
            {
                sb.Append(SnapshotPainter.Paint(
                    snapshot,
                    ttyOnAlt: false,
                    inner.Col,
                    inner.Row,
                    inner.Cols,
                    inner.Rows,
                    eraseDisplay: false,
                    addressCursor: false));
            }
        }

        return sb.ToString();
    }

    internal static CellRect? ScrollbarRect(PopupChromeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var gutterCol = frame.Inner.EndCol;
        var rightBorderCol = frame.Outer.EndCol - 1;
        if (gutterCol >= rightBorderCol || gutterCol < frame.Outer.Col + 1)
            return null;
        if (frame.Inner.Rows < 1)
            return null;
        return new CellRect(gutterCol, frame.Inner.Row, 1, frame.Inner.Rows);
    }

    private static void PaintScrollbar(StringBuilder sb, PopupChromeFrame frame, ThemePalette theme)
    {
        if (ScrollbarRect(frame) is not { } bar)
            return;
        for (var r = bar.Row; r < bar.EndRow; r++)
        {
            sb.Append(SnapshotPainter.CursorAddress(bar.Col, r));
            ThemeSgr.WriteStyled(sb, theme.Overlay0, theme.Surface0, "│");
        }
    }

    private static void PaintBorder(StringBuilder sb, CellRect outer, ThemePalette theme)
    {
        if (outer.Cols < 2 || outer.Rows < 2)
            return;

        var glyphs = theme.ResolveGlyphs(ChromeGlyphSet.Unicode);
        var border = theme.ResolveFocusBorder();
        WriteBorderRow(sb, outer.Col, outer.Row, outer.Cols, glyphs.Tl, glyphs.H, glyphs.Tr, theme, border);
        for (var r = outer.Row + 1; r < outer.EndRow - 1; r++)
        {
            sb.Append(SnapshotPainter.CursorAddress(outer.Col, r));
            ThemeSgr.WriteStyled(sb, border, theme.Surface0, glyphs.V.ToString());
            sb.Append(SnapshotPainter.CursorAddress(outer.EndCol - 1, r));
            ThemeSgr.WriteStyled(sb, border, theme.Surface0, glyphs.V.ToString());
        }

        WriteBorderRow(sb, outer.Col, outer.EndRow - 1, outer.Cols, glyphs.Bl, glyphs.H, glyphs.Br, theme, border);
    }

    private static void WriteBorderRow(
        StringBuilder sb,
        int col,
        int row,
        int cols,
        char left,
        char mid,
        char right,
        ThemePalette theme,
        ThemeColor? border = null)
    {
        sb.Append(SnapshotPainter.CursorAddress(col, row));
        var line = cols <= 1
            ? left.ToString()
            : left + new string(mid, cols - 2) + right;
        ThemeSgr.WriteStyled(sb, border ?? theme.ResolveFocusBorder(), theme.Surface0, line);
    }

    public static PopupChromeFrame? FrameFromGeometry(
        CellRect content,
        PopupGeometryResult? geometry)
    {
        if (geometry is null)
            return null;
        var outer = new CellRect(
            content.Col + geometry.OuterCol,
            content.Row + geometry.OuterRow,
            geometry.OuterCols,
            geometry.OuterRows);
        var inner = new CellRect(
            content.Col + geometry.InnerCol,
            content.Row + geometry.InnerRow,
            geometry.InnerCols,
            geometry.InnerRows);
        return new PopupChromeFrame(outer, inner);
    }
}
