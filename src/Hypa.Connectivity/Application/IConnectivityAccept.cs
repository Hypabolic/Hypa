using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Host-side TLS/TCP accept helper. It is not the mux and not mux --listen.
/// </summary>
public interface IConnectivityAccept : IAsyncDisposable, IActiveJoinCloser
{
    int Port { get; }

    bool UsesTls { get; }

    /// <summary>True when a QUIC listener is active on the accept port.</summary>
    bool QuicListening { get; }

    BytePathEndpoint DialEndpoint { get; }

    void Run();
}
