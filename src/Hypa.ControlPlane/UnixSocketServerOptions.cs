using Hypa.ControlPlane.Unix;

namespace Hypa.ControlPlane;

/// <summary>
/// Limits and strategies for the uid-private control-plane socket.
/// </summary>
public sealed record UnixSocketServerOptions
{
    public const int DefaultMaxLineBytes = 1_048_576;
    public const int DefaultMaxConcurrentConnections = 32;
    public const int DefaultListenBacklog = 32;

    public static UnixSocketServerOptions Default { get; } = new();

    /// <summary>Hard max NDJSON line size in bytes (design §8.1). Default 1 MiB.</summary>
    public int MaxLineBytes { get; init; } = DefaultMaxLineBytes;

    /// <summary>Hard max live handler slots. Excess peers are rejected on the accept thread.</summary>
    public int MaxConcurrentConnections { get; init; } = DefaultMaxConcurrentConnections;

    /// <summary>Kernel listen backlog. This is not the userspace connection cap.</summary>
    public int ListenBacklog { get; init; } = DefaultListenBacklog;

    public IUnixPeerAuthenticator PeerAuthenticator { get; init; } = new EuidPeerAuthenticator();

    public IUnixSocketModeGuard ModeGuard { get; init; } = new UnixSocketOwnerGuard();
}
