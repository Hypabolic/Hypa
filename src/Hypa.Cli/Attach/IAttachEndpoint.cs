using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>
/// Attach client transport. Unix is local. Connectivity is a joined session.
/// Connect does not spawn <c>hypa attach</c> in a pane.
/// </summary>
public interface IAttachEndpoint
{
    string Kind { get; }

    ControlPlaneClient CreateClient();
}

public static class AttachEndpointKinds
{
    public const string Unix = "unix";
    public const string Connectivity = "connectivity";
    public const string Ssh = "ssh";
}
