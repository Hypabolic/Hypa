using Hypa.Connectivity.Application;
using Hypa.Continuity.Application;
using Hypa.Placement.Application;

namespace Hypa.Cli;

/// <summary>
/// Cli composes Connectivity, Placement, and Continuity. This type does not join.
/// </summary>
public static class ConnectivityLayers
{
    public static string ConnectivityAssembly => typeof(IRendezvousJoin).Assembly.GetName().Name!;

    public static string PlacementAssembly => typeof(IPlacementDirectory).Assembly.GetName().Name!;

    public static string ContinuityAssembly => typeof(HandoffService).Assembly.GetName().Name!;
}
