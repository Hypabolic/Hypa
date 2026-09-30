namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Server-side close-on-exit overlay for <c>type=pane</c> custom commands.
/// Previous focus and zoom are restored when the overlay occupant exits.
/// </summary>
public sealed record CustomCommandOverlayState
{
    public required string OverlayPaneId { get; init; }

    public required string RestorePaneId { get; init; }

    public required string TabId { get; init; }

    public required bool RestoreZoomed { get; init; }

    public string? RestoreZoomedPaneId { get; init; }
}
