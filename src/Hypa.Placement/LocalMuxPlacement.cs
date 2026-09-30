namespace Hypa.Placement;

/// <summary>
/// One local mux placement for the one-machine C-02 gate.
/// Different HOME, socket, and workspace from its peer.
/// </summary>
public sealed record LocalMuxPlacement
{
    public required string MuxId { get; init; }
    public required string Home { get; init; }
    public required string SocketPath { get; init; }
    public required string Workspace { get; init; }
    public PlacementKind Kind { get; init; } = PlacementKind.Local;
}
