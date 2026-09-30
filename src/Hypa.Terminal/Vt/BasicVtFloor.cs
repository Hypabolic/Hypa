namespace Hypa.Terminal.Vt;

/// <summary>
/// Dimension budget for pane VT engines. Wire health uses
/// <c>Hypa.AgentRuntime.Protocol.VtFloorDefaults</c> for cols/rows/max.
/// Tests assert these stay aligned.
/// </summary>
/// <remarks>
/// Dimensions are default pane / floor size, not max-over-live-panes.
/// Upper bounds reject resource-exhaustion sizes at engine resize, screen
/// allocation, and structured snapshot normalize/deserialize.
/// Capability matrix: <c>src/Hypa.AgentRuntime.Protocol/VT-unsupported.md</c>.
/// </remarks>
public static class BasicVtFloor
{
    /// <summary>Default columns for new panes and engine ctor.</summary>
    public const int DefaultCols = 120;

    /// <summary>Default rows for new panes and engine ctor.</summary>
    public const int DefaultRows = 40;

    /// <summary>Maximum columns accepted for resize / snapshot grids.</summary>
    public const int MaxCols = 1024;

    /// <summary>Maximum rows accepted for resize / snapshot grids.</summary>
    public const int MaxRows = 512;

    /// <summary>
    /// Maximum cells (<c>cols * rows</c>) for screen and snapshot allocation.
    /// Caps pathological shapes even when each axis is under its max.
    /// </summary>
    public const int MaxCells = 262_144;

    /// <summary>
    /// Return true when <paramref name="cols"/> and <paramref name="rows"/> are
    /// positive and within <see cref="MaxCols"/> / <see cref="MaxRows"/> / <see cref="MaxCells"/>.
    /// </summary>
    public static bool TryValidateDimensions(int cols, int rows, out string? error)
    {
        if (cols < 1 || rows < 1)
        {
            error = "cols and rows must be positive";
            return false;
        }

        if (cols > MaxCols)
        {
            error = $"cols must be <= {MaxCols} (got {cols})";
            return false;
        }

        if (rows > MaxRows)
        {
            error = $"rows must be <= {MaxRows} (got {rows})";
            return false;
        }

        var cells = (long)cols * rows;
        if (cells > MaxCells)
        {
            error = $"cols*rows must be <= {MaxCells} (got {cols}x{rows}={cells})";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Throw <see cref="ArgumentOutOfRangeException"/> when dimensions exceed the budget.
    /// </summary>
    public static void EnsureValidDimensions(int cols, int rows)
    {
        if (TryValidateDimensions(cols, rows, out var error))
            return;

        // Prefer the axis that is out of range; fall back to cols for cell-budget failures.
        if (cols < 1 || cols > MaxCols)
            throw new ArgumentOutOfRangeException(nameof(cols), cols, error);
        if (rows < 1 || rows > MaxRows)
            throw new ArgumentOutOfRangeException(nameof(rows), rows, error);
        throw new ArgumentOutOfRangeException(nameof(cols), cols, error);
    }
}
