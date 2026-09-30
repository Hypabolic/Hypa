using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>Inclusive origin, exclusive end. Col and row are 0-based.</summary>
public readonly record struct CellRect(int Col, int Row, int Cols, int Rows)
{
    public int EndCol => Col + Cols;

    public int EndRow => Row + Rows;

    public int Area => Math.Max(0, Cols) * Math.Max(0, Rows);

    public bool Contains(int col, int row) =>
        col >= Col && col < EndCol && row >= Row && row < EndRow;

    public bool Intersects(CellRect other) =>
        Col < other.EndCol && EndCol > other.Col && Row < other.EndRow && EndRow > other.Row;

    public IEnumerable<(int Col, int Row)> Cells()
    {
        for (var r = Row; r < EndRow; r++)
        {
            for (var c = Col; c < EndCol; c++)
                yield return (c, r);
        }
    }
}

public sealed record AllocatedPaneRect(string PaneId, CellRect Rect);

public sealed record AllocatedSplitBorder(
    CellRect Rect,
    IReadOnlyList<int> Path,
    string Direction,
    CellRect Parent = default,
    double Ratio = 0.5);

public sealed record LayoutRectAllocation(
    CellRect Content,
    IReadOnlyList<AllocatedPaneRect> Panes,
    IReadOnlyList<AllocatedSplitBorder> Borders);

/// <summary>
/// Pure BSP rect split. No IO. First child gets floor(size * ratio); second gets the remainder.
/// </summary>
public static class LayoutRectAllocator
{
    public static LayoutRectAllocation Allocate(
        LayoutNode? root,
        int cols,
        int rows,
        bool zoomed = false,
        string? zoomedPaneId = null)
    {
        var content = new CellRect(0, 0, Math.Max(0, cols), Math.Max(0, rows));
        if (root is null || content.Cols <= 0 || content.Rows <= 0)
            return new LayoutRectAllocation(content, [], []);

        if (zoomed)
        {
            var id = zoomedPaneId;
            if (string.IsNullOrWhiteSpace(id))
                id = LayoutTreeOperations.Leaves(root).FirstOrDefault()?.PaneId?.Value;
            if (string.IsNullOrWhiteSpace(id))
                return new LayoutRectAllocation(content, [], []);
            return new LayoutRectAllocation(content, [new AllocatedPaneRect(id, content)], []);
        }

        var panes = new List<AllocatedPaneRect>();
        var borders = new List<AllocatedSplitBorder>();
        Walk(root, content, [], panes, borders);
        return new LayoutRectAllocation(content, panes, borders);
    }

    public static bool IsHorizontalSplit(string? direction) =>
        direction is LayoutNode.DirectionRight or LayoutNode.DirectionLeft or "horizontal";

    public static bool IsVerticalSplit(string? direction) =>
        direction is LayoutNode.DirectionDown or LayoutNode.DirectionUp or "vertical";

    private static void Walk(
        LayoutNode node,
        CellRect rect,
        List<int> path,
        List<AllocatedPaneRect> panes,
        List<AllocatedSplitBorder> borders)
    {
        if (node is LayoutPaneNode pane)
        {
            if (pane.PaneId is { } id)
                panes.Add(new AllocatedPaneRect(id.Value, rect));
            return;
        }

        if (node is not LayoutSplitNode split)
            return;

        var horizontal = IsHorizontalSplit(split.Direction);
        var size = horizontal ? rect.Cols : rect.Rows;
        var firstSize = (int)Math.Floor(size * split.Ratio);
        if (firstSize < 0)
            firstSize = 0;
        if (firstSize > size)
            firstSize = size;
        var secondSize = size - firstSize;

        CellRect firstRect;
        CellRect secondRect;
        if (horizontal)
        {
            firstRect = new CellRect(rect.Col, rect.Row, firstSize, rect.Rows);
            secondRect = new CellRect(rect.Col + firstSize, rect.Row, secondSize, rect.Rows);
        }
        else
        {
            firstRect = new CellRect(rect.Col, rect.Row, rect.Cols, firstSize);
            secondRect = new CellRect(rect.Col, rect.Row + firstSize, rect.Cols, secondSize);
        }

        if (firstSize > 0 && secondSize > 0)
        {
            var border = horizontal
                ? new CellRect(rect.Col + firstSize, rect.Row, 1, rect.Rows)
                : new CellRect(rect.Col, rect.Row + firstSize, rect.Cols, 1);
            borders.Add(new AllocatedSplitBorder(border, [.. path], split.Direction, rect, split.Ratio));
        }

        var downFirst = new List<int>(path) { 0 };
        var downSecond = new List<int>(path) { 1 };
        Walk(split.First, firstRect, downFirst, panes, borders);
        Walk(split.Second, secondRect, downSecond, panes, borders);
    }
}
