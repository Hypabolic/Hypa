using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.Cli.Attach.Mouse;

public sealed record ToastHit(string PaneId, CellRect Rect, string? Text = null)
{
    /// <summary>
    /// Place a toast inside the content area. Inset one row and column from
    /// tab and status chrome. <paramref name="stackIndex"/> shifts later
    /// toasts off the same cells (down from top, up from bottom).
    /// </summary>
    public static CellRect Place(
        ToastPosition position,
        int cols,
        int rows,
        int width = 16,
        int height = 1,
        int stackIndex = 0)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var insetX = cols > 2 ? 1 : 0;
        var insetY = rows > 2 ? 1 : 0;
        var innerCols = Math.Max(1, cols - (2 * insetX));
        var innerRows = Math.Max(1, rows - (2 * insetY));
        width = Math.Clamp(width, 1, innerCols);
        height = Math.Clamp(height, 1, innerRows);
        stackIndex = Math.Max(0, stackIndex);
        var col = position switch
        {
            ToastPosition.TopLeft or ToastPosition.BottomLeft => 0,
            ToastPosition.TopCenter or ToastPosition.BottomCenter => Math.Max(0, (innerCols - width) / 2),
            _ => Math.Max(0, innerCols - width),
        };
        var top = position is ToastPosition.TopLeft or ToastPosition.TopCenter or ToastPosition.TopRight;
        var maxStack = Math.Max(0, innerRows - height);
        var stacked = Math.Min(stackIndex, maxStack);
        var row = top
            ? stacked
            : Math.Max(0, innerRows - height - stacked);
        return new CellRect(col + insetX, row + insetY, width, height);
    }

    /// <summary>
    /// Full-width one-row banner on the last content row. Used when the
    /// attach client is at or below <c>ui.mobile_width_threshold</c>.
    /// </summary>
    public static CellRect PlaceBanner(CellRect content, bool offsetForWarning = false)
    {
        if (content.Cols < 1 || content.Rows < 1)
            return default;

        var row = content.EndRow - 1;
        if (offsetForWarning && content.Rows > 1)
            row--;
        return new CellRect(content.Col, row, content.Cols, 1);
    }
}
