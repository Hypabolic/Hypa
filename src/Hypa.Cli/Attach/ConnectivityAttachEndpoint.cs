using Hypa.Connectivity.Application;
using Hypa.Connectivity.Infrastructure;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>
/// Attach client over a joined Connectivity session. Control NDJSON rides
/// framed control. This is not reconnect and not Cubes retarget.
/// </summary>
public sealed class ConnectivityAttachEndpoint : IAttachEndpoint
{
    private readonly IFramedSession _session;

    public ConnectivityAttachEndpoint(IFramedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public string Kind => AttachEndpointKinds.Connectivity;

    public ControlPlaneClient CreateClient()
    {
        var stream = new FramedControlStream(_session, ownsSession: false);
        return ControlPlaneClient.FromConnectedStream(stream);
    }
}
