using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Outbound rendezvous join. Mux and client both dial. Neither listens.
/// </summary>
public interface IRendezvousJoin
{
    ValueTask<ConnectivityOutcome<JoinBinding>> JoinAsync(
        JoinBootstrap bootstrap,
        CancellationToken cancellationToken = default);
}
