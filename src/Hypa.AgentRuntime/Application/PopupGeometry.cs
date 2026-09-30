using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

/// <summary>
// Default half of the area.
/// Outer min is 6×4. Centered. Inner is the outer minus a 2-cell border;
/// if inner width is greater than 4, subtract one scrollbar column.
/// </summary>
public sealed record PopupGeometryResult
{
    public required int OuterCol { get; init; }

    public required int OuterRow { get; init; }

    public required int OuterCols { get; init; }

    public required int OuterRows { get; init; }

    public required int InnerCol { get; init; }

    public required int InnerRow { get; init; }

    public required int InnerCols { get; init; }

    public required int InnerRows { get; init; }
}

public static class PopupGeometry
{
    public const int MinOuterCols = 6;

    public const int MinOuterRows = 4;

    public const int DefaultPercent = 50;

    public static PopupSize DefaultSize => PopupSize.Percent(DefaultPercent);

    public static PopupGeometryResult? TryResolve(
        int areaCols,
        int areaRows,
        PopupSize? width = null,
        PopupSize? height = null)
    {
        if (areaCols < MinOuterCols || areaRows < MinOuterRows)
            return null;

        var outerCols = ClampAxis(ResolveAxis(width, areaCols), MinOuterCols, areaCols);
        var outerRows = ClampAxis(ResolveAxis(height, areaRows), MinOuterRows, areaRows);
        var outerCol = (areaCols - outerCols) / 2;
        var outerRow = (areaRows - outerRows) / 2;
        var innerCols = Math.Max(0, outerCols - 2);
        var innerRows = Math.Max(0, outerRows - 2);
        if (innerCols > 4)
            innerCols -= 1;

        return new PopupGeometryResult
        {
            OuterCol = outerCol,
            OuterRow = outerRow,
            OuterCols = outerCols,
            OuterRows = outerRows,
            InnerCol = outerCol + 1,
            InnerRow = outerRow + 1,
            InnerCols = innerCols,
            InnerRows = innerRows,
        };
    }

    /// <summary>
    /// Rebuild outer from a known inner PTY size (border plus optional scrollbar).
    /// Used when snapshot/lifecycle carries cols/rows but not a requested size.
    /// </summary>
    public static PopupGeometryResult? TryFromInner(
        int areaCols,
        int areaRows,
        int innerCols,
        int innerRows)
    {
        if (areaCols < MinOuterCols || areaRows < MinOuterRows)
            return null;

        innerCols = Math.Max(1, innerCols);
        innerRows = Math.Max(1, innerRows);
        var outerCols = ClampAxis(
            innerCols > 4 ? innerCols + 3 : innerCols + 2,
            MinOuterCols,
            areaCols);
        var outerRows = ClampAxis(innerRows + 2, MinOuterRows, areaRows);
        var outerCol = (areaCols - outerCols) / 2;
        var outerRow = (areaRows - outerRows) / 2;
        var maxInnerCols = Math.Max(0, outerCols - 2);
        if (maxInnerCols > 4)
            maxInnerCols -= 1;
        var maxInnerRows = Math.Max(0, outerRows - 2);

        return new PopupGeometryResult
        {
            OuterCol = outerCol,
            OuterRow = outerRow,
            OuterCols = outerCols,
            OuterRows = outerRows,
            InnerCol = outerCol + 1,
            InnerRow = outerRow + 1,
            InnerCols = Math.Min(innerCols, maxInnerCols),
            InnerRows = Math.Min(innerRows, maxInnerRows),
        };
    }

    /// <summary>
    /// Center a known outer (and inner PTY) in the content area. Used when
    /// lifecycle or <c>popup.resize</c> carries outer_cols/outer_rows.
    /// </summary>
    public static PopupGeometryResult? TryFromOuter(
        int areaCols,
        int areaRows,
        int outerCols,
        int outerRows,
        int innerCols = 0,
        int innerRows = 0)
    {
        if (areaCols < MinOuterCols || areaRows < MinOuterRows)
            return null;
        if (outerCols < MinOuterCols || outerRows < MinOuterRows)
            return TryFromInner(areaCols, areaRows, innerCols, innerRows);

        outerCols = ClampAxis(outerCols, MinOuterCols, areaCols);
        outerRows = ClampAxis(outerRows, MinOuterRows, areaRows);
        var outerCol = (areaCols - outerCols) / 2;
        var outerRow = (areaRows - outerRows) / 2;
        var maxInnerCols = Math.Max(0, outerCols - 2);
        if (maxInnerCols > 4)
            maxInnerCols -= 1;
        var maxInnerRows = Math.Max(0, outerRows - 2);
        if (innerCols < 1)
            innerCols = Math.Max(1, maxInnerCols);
        if (innerRows < 1)
            innerRows = Math.Max(1, maxInnerRows);

        return new PopupGeometryResult
        {
            OuterCol = outerCol,
            OuterRow = outerRow,
            OuterCols = outerCols,
            OuterRows = outerRows,
            InnerCol = outerCol + 1,
            InnerRow = outerRow + 1,
            InnerCols = Math.Min(Math.Max(1, innerCols), Math.Max(1, maxInnerCols)),
            InnerRows = Math.Min(Math.Max(1, innerRows), Math.Max(1, maxInnerRows)),
        };
    }

    private static int ResolveAxis(PopupSize? size, int area)
    {
        if (size is null)
            return Math.Max(1, area / 2);

        if (size.IsPercent)
        {
            var percent = Math.Clamp(size.Value, 1, 100);
            var cells = (int)((long)area * percent / 100);
            return Math.Max(1, cells);
        }

        return Math.Max(1, size.Value);
    }

    private static int ClampAxis(int value, int min, int max) =>
        Math.Clamp(value, min, max);
}
