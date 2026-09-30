using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Cli.Attach.Chrome;

[Flags]
public enum PaneChromeBorders
{
    None = 0,
    Top = 1,
    Right = 2,
    Bottom = 4,
    Left = 8,
    All = Top | Right | Bottom | Left,
}

internal readonly record struct PaneChromeSpec(
    string PaneId,
    string Label,
    CellRect Rect,
    PaneChromeBorders Borders,
    bool Focused);

/// <summary>
/// <c>add_pane_border_cells</c> (588-627), <c>add_split_border_cells</c>
/// (531-587), <c>line_touches_pane</c> (629-651), and
/// <c>line_cell_symbol</c> (706-724).
/// </summary>
internal static class PaneLineCells
{
    private readonly record struct LineCell(bool Up, bool Down, bool Left, bool Right);

    public static CellRect InnerRect(CellRect area, PaneChromeBorders borders)
    {
        if (borders is PaneChromeBorders.None)
            return area;
        var col = area.Col;
        var row = area.Row;
        var cols = area.Cols;
        var rows = area.Rows;
        if ((borders & PaneChromeBorders.Left) != 0)
        {
            col++;
            cols--;
        }

        if ((borders & PaneChromeBorders.Right) != 0)
            cols--;
        if ((borders & PaneChromeBorders.Top) != 0)
        {
            row++;
            rows--;
        }

        if ((borders & PaneChromeBorders.Bottom) != 0)
            rows--;
        if (cols < 0)
            cols = 0;
        if (rows < 0)
            rows = 0;
        return new CellRect(col, row, cols, rows);
    }

    public static List<PaneChromeSpec> Apply(
        IReadOnlyList<PaneChromeSpec> panes,
        bool paneBorders,
        bool paneGaps,
        bool paneOuterBorders,
        bool singlePaneFrame = false)
    {
        ArgumentNullException.ThrowIfNull(panes);
        var multiPane = panes.Count > 1;
        var outerLeft = int.MaxValue;
        var outerTop = int.MaxValue;
        var outerRight = 0;
        var outerBottom = 0;
        foreach (var info in panes)
        {
            if (info.Rect.Col < outerLeft)
                outerLeft = info.Rect.Col;
            if (info.Rect.Row < outerTop)
                outerTop = info.Rect.Row;
            if (info.Rect.EndCol > outerRight)
                outerRight = info.Rect.EndCol;
            if (info.Rect.EndRow > outerBottom)
                outerBottom = info.Rect.EndRow;
        }

        if (panes.Count == 0)
        {
            outerLeft = 0;
            outerTop = 0;
        }

        var result = new List<PaneChromeSpec>(panes.Count);
        foreach (var info in panes)
        {
            var rightNeighbor = multiPane && HasPaneToRight(info, panes);
            var belowNeighbor = multiPane && HasPaneBelow(info, panes);
            var rect = info.Rect;
            if (multiPane && paneGaps && !paneBorders)
            {
                if (rightNeighbor)
                    rect = new CellRect(rect.Col, rect.Row, ShrinkForGap(rect.Cols), rect.Rows);
                if (belowNeighbor)
                    rect = new CellRect(rect.Col, rect.Row, rect.Cols, ShrinkForGap(rect.Rows));
            }

            var borders = PaneChromeBorders.None;
            // A single pane stays flush unless the theme asks for a window frame.
            if (paneBorders && (multiPane || (paneOuterBorders && singlePaneFrame)))
            {
                borders = PaneChromeBorders.All;
                if (multiPane && !paneGaps)
                {
                    if (rightNeighbor)
                        borders &= ~PaneChromeBorders.Right;
                    if (belowNeighbor)
                        borders &= ~PaneChromeBorders.Bottom;
                }

                if (!paneOuterBorders)
                {
                    if (rect.Col == outerLeft)
                        borders &= ~PaneChromeBorders.Left;
                    if (rect.Row == outerTop)
                        borders &= ~PaneChromeBorders.Top;
                    if (rect.EndCol == outerRight)
                        borders &= ~PaneChromeBorders.Right;
                    if (rect.EndRow == outerBottom)
                        borders &= ~PaneChromeBorders.Bottom;
                }
            }

            result.Add(info with { Rect = rect, Borders = borders });
        }

        return result;
    }

    public static List<CellRect> GapsAfterShrink(
        IReadOnlyList<PaneChromeSpec> original,
        IReadOnlyList<PaneChromeSpec> applied)
    {
        var gaps = new List<CellRect>();
        if (original.Count != applied.Count)
            return gaps;
        for (var i = 0; i < original.Count; i++)
        {
            var before = original[i].Rect;
            var after = applied[i].Rect;
            if (after.Cols < before.Cols)
                gaps.Add(new CellRect(after.EndCol, after.Row, before.Cols - after.Cols, after.Rows));
            if (after.Rows < before.Rows)
                gaps.Add(new CellRect(after.Col, after.EndRow, after.Cols, before.Rows - after.Rows));
        }

        return gaps;
    }

    public static PaneChromeBorders ZoomedBorders(
        int layoutPaneCount,
        bool paneBorders,
        bool paneOuterBorders,
        bool singlePaneFrame = false) =>
        paneBorders && paneOuterBorders && (layoutPaneCount > 1 || (singlePaneFrame && layoutPaneCount >= 1))
            ? PaneChromeBorders.All
            : PaneChromeBorders.None;

    public static void Stamp(
        IHostCellSink sink,
        IReadOnlyList<ChromePaneFrame> panes,
        IReadOnlyList<ChromeSplitHit> splits,
        bool paneGaps,
        ChromeGlyphSet glyphs,
        ThemePalette theme,
        int cols,
        int rows)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(panes);
        ArgumentNullException.ThrowIfNull(splits);
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(theme);
        if (panes.Count == 0)
            return;
        var allEmpty = true;
        foreach (var pane in panes)
        {
            if (pane.Borders != PaneChromeBorders.None)
            {
                allEmpty = false;
                break;
            }
        }

        if (allEmpty)
            return;

        var cells = new Dictionary<(int Col, int Row), LineCell>();
        foreach (var pane in panes)
            AddPaneBorderCells(cells, pane);
        AddSplitBorderCells(paneGaps, splits, cells);

        foreach (var ((x, y), line) in cells)
        {
            if (x < 0 || x >= cols || y < 0 || y >= rows)
                continue;
            var focused = false;
            foreach (var pane in panes)
            {
                if (pane.Focused && LineTouchesPane(x, y, pane, paneGaps))
                {
                    focused = true;
                    break;
                }
            }

            var symbol = glyphs.Resolve(line.Up, line.Down, line.Left, line.Right);
            if (symbol == '\0')
                continue;
            var fg = focused ? theme.ResolveFocusBorder() : theme.Overlay1;
            // Keep border glyphs on the theme desktop, not the host default bg.
            sink.Write(x, y, char.ToString(symbol), fg, theme.PanelBg, 1);
        }

        StampTitles(sink, panes, glyphs, theme, cols, rows);
        if (theme.Chrome.DropShadow)
        {
            // Keep the desktop status strip clear of window shadows.
            var shadowRows = theme.Chrome.DesktopStatusBar && rows > 0 ? rows - 1 : rows;
            foreach (var paneFrame in panes)
            {
                if (paneFrame.Borders == PaneChromeBorders.None)
                    continue;
                ChromeShadow.Stamp(sink, paneFrame.Frame, theme, cols, shadowRows);
            }
        }
    }

    private static void StampTitles(
        IHostCellSink sink,
        IReadOnlyList<ChromePaneFrame> panes,
        ChromeGlyphSet glyphs,
        ThemePalette theme,
        int cols,
        int rows)
    {
        foreach (var pane in panes)
        {
            if ((pane.Borders & PaneChromeBorders.Top) == 0)
                continue;
            var rect = pane.Frame;
            if (rect.Cols < 5 || rect.Rows < 1)
                continue;
            if (rect.Row < 0 || rect.Row >= rows)
                continue;

            var fg = pane.Focused ? theme.ResolveFocusBorder() : theme.Overlay1;
            var bg = theme.PanelBg;
            var label = string.IsNullOrWhiteSpace(pane.Label) ? pane.PaneId : pane.Label;
            label = label.Trim();
            if (label.Length == 0)
                continue;

            var title = SafeDisplayText.Clip(" " + label + " ", Math.Max(0, rect.Cols - 4));
            var titleCols = SafeDisplayText.Width(title);
            if (titleCols <= 0)
                continue;
            var start = rect.Col + Math.Max(1, (rect.Cols - titleCols) / 2);
            if (start + titleCols > rect.EndCol - 1)
                start = rect.Col + 1;
            if (start < cols)
                sink.Write(start, rect.Row, title, fg, bg, titleCols);

            // Turbo Vision close widget on the top-left border.
            if (rect.Cols >= 5 && glyphs.Close != '\0')
            {
                var close = "[" + glyphs.Close + "]";
                if (rect.Col + 1 < cols)
                    sink.Write(rect.Col + 1, rect.Row, close, fg, bg, SafeDisplayText.Width(close));
            }
        }
    }

    private static void AddPaneBorderCells(
        Dictionary<(int Col, int Row), LineCell> cells,
        ChromePaneFrame info)
    {
        var rect = info.Frame;
        if (rect.Cols == 0 || rect.Rows == 0)
            return;
        var right = rect.EndCol - 1;
        var bottom = rect.EndRow - 1;
        if ((info.Borders & PaneChromeBorders.Top) != 0)
        {
            for (var x = rect.Col; x <= right; x++)
            {
                var cell = Get(cells, x, rect.Row);
                cells[(x, rect.Row)] = cell with
                {
                    Left = cell.Left || x > rect.Col,
                    Right = cell.Right || x < right,
                };
            }
        }

        if ((info.Borders & PaneChromeBorders.Bottom) != 0)
        {
            for (var x = rect.Col; x <= right; x++)
            {
                var cell = Get(cells, x, bottom);
                cells[(x, bottom)] = cell with
                {
                    Left = cell.Left || x > rect.Col,
                    Right = cell.Right || x < right,
                };
            }
        }

        if ((info.Borders & PaneChromeBorders.Left) != 0)
        {
            for (var y = rect.Row; y <= bottom; y++)
            {
                var cell = Get(cells, rect.Col, y);
                cells[(rect.Col, y)] = cell with
                {
                    Up = cell.Up || y > rect.Row,
                    Down = cell.Down || y < bottom,
                };
            }
        }

        if ((info.Borders & PaneChromeBorders.Right) != 0)
        {
            for (var y = rect.Row; y <= bottom; y++)
            {
                var cell = Get(cells, right, y);
                cells[(right, y)] = cell with
                {
                    Up = cell.Up || y > rect.Row,
                    Down = cell.Down || y < bottom,
                };
            }
        }
    }

    private static void AddSplitBorderCells(
        bool paneGaps,
        IReadOnlyList<ChromeSplitHit> splits,
        Dictionary<(int Col, int Row), LineCell> cells)
    {
        if (paneGaps)
            return;

        foreach (var split in splits)
        {
            if (LayoutRectAllocator.IsHorizontalSplit(split.Direction))
            {
                var x = split.Rect.Col;
                var end = split.Rect.EndRow;
                for (var y = split.Rect.Row; y <= end; y++)
                {
                    if (!cells.ContainsKey((x, y)))
                        continue;
                    var left = x > 0
                        && cells.TryGetValue((x - 1, y), out var leftCell)
                        && (leftCell.Left || leftCell.Right);
                    var right = cells.TryGetValue((x + 1, y), out var rightCell)
                        && (rightCell.Left || rightCell.Right);
                    var cell = Get(cells, x, y);
                    cells[(x, y)] = cell with
                    {
                        Up = cell.Up || y > split.Rect.Row,
                        Down = cell.Down || y + 1 < end,
                        Left = cell.Left || left,
                        Right = cell.Right || right,
                    };
                }
            }
            else
            {
                var y = split.Rect.Row;
                var end = split.Rect.EndCol;
                for (var x = split.Rect.Col; x <= end; x++)
                {
                    if (!cells.ContainsKey((x, y)))
                        continue;
                    var up = y > 0
                        && cells.TryGetValue((x, y - 1), out var upCell)
                        && (upCell.Up || upCell.Down);
                    var down = cells.TryGetValue((x, y + 1), out var downCell)
                        && (downCell.Up || downCell.Down);
                    var cell = Get(cells, x, y);
                    cells[(x, y)] = cell with
                    {
                        Left = cell.Left || x > split.Rect.Col,
                        Right = cell.Right || x + 1 < end,
                        Up = cell.Up || up,
                        Down = cell.Down || down,
                    };
                }
            }
        }
    }

    private static bool LineTouchesPane(int x, int y, ChromePaneFrame info, bool paneGaps)
    {
        var rect = info.Frame;
        if (rect.Cols == 0 || rect.Rows == 0)
            return false;
        var right = rect.EndCol - 1;
        var bottom = rect.EndRow - 1;
        var inRows = y >= rect.Row && y <= bottom;
        var inCols = x >= rect.Col && x <= right;
        var ownBorder =
            (inRows && (x == rect.Col || x == right))
            || (inCols && (y == rect.Row || y == bottom));
        if (paneGaps)
            return ownBorder;

        var sharedRight = rect.EndCol;
        var sharedBottom = rect.EndRow;
        return ownBorder
            || (inRows && x == sharedRight)
            || (inCols && y == sharedBottom)
            || (x == sharedRight && y == sharedBottom);
    }

    private static bool HasPaneToRight(PaneChromeSpec info, IReadOnlyList<PaneChromeSpec> panes)
    {
        var right = info.Rect.EndCol;
        foreach (var other in panes)
        {
            if (other.PaneId == info.PaneId)
                continue;
            if (other.Rect.Col == right && RangesOverlap(info.Rect.Row, info.Rect.Rows, other.Rect.Row, other.Rect.Rows))
                return true;
        }

        return false;
    }

    private static bool HasPaneBelow(PaneChromeSpec info, IReadOnlyList<PaneChromeSpec> panes)
    {
        var bottom = info.Rect.EndRow;
        foreach (var other in panes)
        {
            if (other.PaneId == info.PaneId)
                continue;
            if (other.Rect.Row == bottom && RangesOverlap(info.Rect.Col, info.Rect.Cols, other.Rect.Col, other.Rect.Cols))
                return true;
        }

        return false;
    }

    private static bool RangesOverlap(int aStart, int aLen, int bStart, int bLen) =>
        aStart < bStart + bLen && bStart < aStart + aLen;

    private static int ShrinkForGap(int size) => size > 1 ? size - 1 : size;

    private static LineCell Get(Dictionary<(int Col, int Row), LineCell> cells, int col, int row) =>
        cells.TryGetValue((col, row), out var cell) ? cell : default;
}
