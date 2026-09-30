using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>Local uid-private Unix socket. Existing <see cref="ControlPlaneClient"/> path.</summary>
public sealed class UnixAttachEndpoint : IAttachEndpoint
{
    public UnixAttachEndpoint(string socketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        SocketPath = socketPath;
    }

    public string Kind => AttachEndpointKinds.Unix;

    public string SocketPath { get; }

    public ControlPlaneClient CreateClient() => new(SocketPath);
}
