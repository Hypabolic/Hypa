namespace Hypa.Cli.Attach.Chrome;

/// <summary>
/// Fold point for the attach-client narrow layout. Width at or below the
/// configured threshold uses the phone chrome.
/// </summary>
public static class NarrowLayout
{
    public const int DefaultThreshold = 64;

    public const int MinContentCols = 8;

    public const int MinRows = 3;

    public const int HeaderRows = 2;

    public const int SwitchButtonWidth = 10;

    public static bool IsNarrow(int cols, int threshold) =>
        threshold > 0 && cols > 0 && cols <= threshold;

    public static bool IsTooSmall(int cols, int rows) =>
        cols < MinContentCols || rows < MinRows;

    public static int SwitchWidth(int cols) =>
        Math.Min(SwitchButtonWidth, Math.Max(0, cols));

    public static int HeaderRowsFor(int rows)
    {
        if (rows < 1)
            return 1;
        return rows < 5 ? 1 : Math.Min(HeaderRows, rows);
    }
}
