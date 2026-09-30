namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// F1 VT floor values for <c>runtime.health.vt</c> and default pane geometry.
/// Dimensions are default pane / floor size, not max over live panes.
/// Shipping engine: Ghostty. Dimension budget: <c>BasicVtFloor</c>.
/// Matrix: <c>VT-unsupported.md</c>.
/// </summary>
/// <remarks>
/// Upper bounds (<see cref="MaxCols"/> / <see cref="MaxRows"/> / <see cref="MaxCells"/>)
/// are a hard resource budget for <c>pane.resize</c>, VT screen allocation, and
/// structured snapshot normalize/deserialize. Keep aligned with <c>BasicVtFloor</c>.
/// </remarks>
public static class VtFloorDefaults
{
    /// <summary>Wire name for the shipping VT provider (Ghostty).</summary>
    public const string Provider = "ghostty";

    /// <summary>Default columns for new panes and health floor report.</summary>
    public const int DefaultCols = 120;

    /// <summary>Default rows for new panes and health floor report.</summary>
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

    /// <summary>F1 capability tokens on <c>runtime.health.vt.capabilities</c>.</summary>
    public static readonly string[] Capabilities =
        ["utf8", "cursor", "erase", "sgr", "alt_screen", "scroll_region", "wide_char"];

    /// <summary>
    /// Optional <c>runtime.health.vt.capabilities</c> token for
    /// <c>IPaneVtSnapshot</c> live-cell panes. Attach never Remaps those
    /// panes. Production Ghostty is snapshot-capable even when this token
    /// is absent.
    /// </summary>
    public const string SnapshotCapability = "snapshot";

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
}
