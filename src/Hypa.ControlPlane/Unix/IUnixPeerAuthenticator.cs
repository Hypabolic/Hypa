using System.Net.Sockets;

namespace Hypa.ControlPlane.Unix;

/// <summary>
/// Strategy: accept or reject an AF_UNIX peer. The mux socket is uid-private.
/// </summary>
public interface IUnixPeerAuthenticator
{
    /// <summary>
    /// Throw <see cref="UnauthorizedAccessException"/> when the peer uid is not the server euid.
    /// Windows: no-op success. Mux peer credentials are Unix-only.
    /// </summary>
    void Authenticate(Socket connected);
}
