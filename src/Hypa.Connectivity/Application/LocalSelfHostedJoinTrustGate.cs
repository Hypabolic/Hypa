using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Admits unexpired local-operator capabilities. Pairing replaces this gate.
/// </summary>
public sealed class LocalSelfHostedJoinTrustGate : IJoinTrustGate
{
    public ValueTask<ConnectivityOutcome> AdmitAsync(
        JoinBootstrap bootstrap,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (!bootstrap.Capability.OperatorId.IsLocalSelfHosted)
        {
            return ValueTask.FromResult(ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "self-hosted accept requires local-operator identity"));
        }

        if (bootstrap.Capability.ExpiresAt <= utcNow)
        {
            return ValueTask.FromResult(ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinExpired,
                "join capability expired"));
        }

        return ValueTask.FromResult(ConnectivityOutcome.Success());
    }

    public ValueTask<bool> IsJoinStillValidAsync(
        JoinBootstrap bootstrap,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        var admitted = AdmitAsync(bootstrap, utcNow, cancellationToken);
        return admitted.IsCompletedSuccessfully
            ? ValueTask.FromResult(admitted.Result.Ok)
            : AwaitValidAsync(admitted);
    }

    public ValueTask<bool> IsEstablishedJoinStillValidAsync(
        JoinBootstrap bootstrap,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        _ = utcNow;
        _ = cancellationToken;
        ArgumentNullException.ThrowIfNull(bootstrap);
        return ValueTask.FromResult(bootstrap.Capability.OperatorId.IsLocalSelfHosted);
    }

    private static async ValueTask<bool> AwaitValidAsync(ValueTask<ConnectivityOutcome> admitted) =>
        (await admitted.ConfigureAwait(false)).Ok;
}
