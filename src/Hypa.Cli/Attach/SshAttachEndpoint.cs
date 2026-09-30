using Hypa.ControlPlane;
using Hypa.Placement.Application;

namespace Hypa.Cli.Attach;

/// <summary>
/// Local SSH stdio bridge endpoint. Accepted connections spawn SSH to
/// <c>hypa remote-client-bridge</c> on the remote host.
/// The remote mux owns panes, PTYs, and VT state.
/// </summary>
public sealed class SshAttachEndpoint : IAttachEndpoint, IAsyncDisposable
{
    private readonly RemoteMuxPath _path;

    public SshAttachEndpoint(RemoteMuxPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrEmpty(path.LocalSocketPath);
        _path = path;
    }

    public string Kind => AttachEndpointKinds.Ssh;

    public string SocketPath => _path.LocalSocketPath;

    public ulong Generation => _path.Generation;

    public ControlPlaneClient CreateClient() => new(_path.LocalSocketPath, validatePrivatePath: true);

    public ValueTask DisposeAsync() => _path.DisposeAsync();
}
