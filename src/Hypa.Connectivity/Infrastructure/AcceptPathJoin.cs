using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Completes the mux join leg on the accept helper. It does not dial a rendezvous URL.
/// </summary>
public sealed class AcceptPathJoin : IRendezvousJoin
{
    private readonly AcceptJoinCoordinator _coordinator;

    public AcceptPathJoin(AcceptJoinCoordinator coordinator) =>
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public ValueTask<ConnectivityOutcome<JoinBinding>> JoinAsync(
        JoinBootstrap bootstrap,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var matched = _coordinator.CompleteMuxLeg(bootstrap);
        if (!matched.Ok || matched.Value is null)
        {
            return ValueTask.FromResult(ConnectivityOutcome<JoinBinding>.Failure(
                matched.Reason ?? ConnectivityReasons.JoinDenied,
                matched.Detail ?? "join denied"));
        }

        var binding = matched.Value.First.Role == JoinRole.Mux
            ? matched.Value.First
            : matched.Value.Second;
        return ValueTask.FromResult(ConnectivityOutcome<JoinBinding>.Success(binding));
    }
}
