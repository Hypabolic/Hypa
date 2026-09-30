namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Visual occupancy. A pane id is in exactly one of: a layout leaf, or
/// <see cref="TabState.HiddenPaneIds"/>.
/// </summary>
public enum PanePlacement
{
    Tiled = 0,
    Hidden = 1,
}

public static class PanePlacementWire
{
    public const string Tiled = "tiled";
    public const string Hidden = "hidden";

    public static string ToWire(PanePlacement placement) =>
        placement == PanePlacement.Hidden ? Hidden : Tiled;

    public static bool TryParse(string? raw, out PanePlacement placement)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, Tiled, StringComparison.OrdinalIgnoreCase))
        {
            placement = PanePlacement.Tiled;
            return true;
        }

        if (string.Equals(raw, Hidden, StringComparison.OrdinalIgnoreCase))
        {
            placement = PanePlacement.Hidden;
            return true;
        }

        placement = PanePlacement.Tiled;
        return false;
    }
}
