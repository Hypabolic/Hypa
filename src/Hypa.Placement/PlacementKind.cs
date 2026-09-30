namespace Hypa.Placement;

/// <summary>
/// Placement kinds for Work handoff destinations.
/// Implementations talk to muxes only through <c>Hypa.AgentRuntime.Protocol</c>.
/// They must not own panes or embed Continuity state machines.
/// </summary>
public enum PlacementKind
{
    Local = 0,
    PeerSsh = 1,
    CubeDocker = 2,
}
