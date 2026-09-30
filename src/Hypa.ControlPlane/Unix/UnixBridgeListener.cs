using System.Net.Sockets;

namespace Hypa.ControlPlane.Unix;

/// <summary>
/// Private local listener for the SSH stdio bridge.
/// </summary>
internal static class UnixBridgeListener
{
    public static (Socket Listener, UnixSocketFileIdentity? Identity) Bind(
        string socketPath,
        bool freshDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "SSH stdio bridge requires a Unix local socket listener.");
        }

        var dir = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrEmpty(dir))
        {
            throw new InvalidOperationException(
                "Bridge socket path has no parent directory.");
        }

        if (freshDirectory)
        {
            UnixPrivatePathGuard.EnsureBridgeDirectory(dir);
            if (Path.Exists(socketPath))
            {
                throw new InvalidOperationException(
                    $"Bridge socket path '{socketPath}' already exists.");
            }
        }
        else
        {
            var guard = new UnixSocketOwnerGuard();
            guard.EnsurePrivateDirectory(dir);
            UnixSocketServer.PrepareSocketPath(socketPath);
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        UnixSocketFileIdentity? identity = null;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            var harden = new UnixSocketOwnerGuard();
            harden.HardenSocket(socketPath);
            if (!UnixSocketPathIdentity.TryRead(socketPath, out var captured))
            {
                throw new InvalidOperationException(
                    $"Could not read bridge socket identity at '{socketPath}'.");
            }

            identity = captured;
            listener.Listen(8);
            listener.Blocking = false;
        }
        catch
        {
            listener.Dispose();
            if (identity is { } owned)
                UnixSocketPathIdentity.RemoveIfOwned(socketPath, owned);
            throw;
        }

        return (listener, identity);
    }

    public static void Stop(string socketPath, UnixSocketFileIdentity? identity, Socket? listener)
    {
        try
        {
            listener?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        if (identity is { } owned)
            UnixSocketPathIdentity.RemoveIfOwned(socketPath, owned);
    }
}
