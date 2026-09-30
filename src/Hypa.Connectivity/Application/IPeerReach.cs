using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Reach a remote mux byte path from a peer profile or explicit target.
/// Application port. OpenSSH is one adapter. QUIC later implements IBytePath.
/// </summary>
public interface IPeerReach
{
    Task<ConnectivityOutcome<BytePathHandle>> ReachAsync(
        PeerReachRequest request,
        PeerConnectionGenerationFence fence,
        CancellationToken cancellationToken = default);
}
