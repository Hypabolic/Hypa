using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Pairing admission for one join leg. Field-shape checks stay on JoinMatcher.
/// </summary>
public interface IJoinTrustGate
{
    ValueTask<ConnectivityOutcome> AdmitAsync(
        JoinBootstrap bootstrap,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    ValueTask<bool> IsJoinStillValidAsync(
        JoinBootstrap bootstrap,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Trust for a bridged join. Ignore capability expiry.
    /// Honour revoke, spend state, device trust, operator, nonce, and pairing removal.
    /// </summary>
    ValueTask<bool> IsEstablishedJoinStillValidAsync(
        JoinBootstrap bootstrap,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);
}

/// <summary>Close in-flight joins for one revoked device.</summary>
public interface IActiveJoinCloser
{
    void CloseJoinsForDevice(DeviceId deviceId);
}
