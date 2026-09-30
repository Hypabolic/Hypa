using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Outbound join to a reachable host accept helper. No rendezvous URL is required.
/// </summary>
public sealed class OutboundDirectJoin : IRendezvousJoin
{
    private readonly BytePathEndpoint _endpoint;
    private readonly IBytePath _bytePath;
    private readonly IDeviceKeyStore? _deviceKeys;

    public OutboundDirectJoin(
        BytePathEndpoint endpoint,
        X509Certificate2? tlsTrust = null,
        IBytePath? bytePath = null,
        IDeviceKeyStore? deviceKeys = null)
    {
        if (!BytePathEndpointRules.TryValidate(endpoint, out var invalid))
            throw new ArgumentException(invalid, nameof(endpoint));

        _endpoint = endpoint;
        _bytePath = bytePath ?? new TcpTlsBytePath(tlsTrust);
        _deviceKeys = deviceKeys;
    }

    public BytePathEndpoint Endpoint => _endpoint;

    public async ValueTask<ConnectivityOutcome<JoinBinding>> JoinAsync(
        JoinBootstrap bootstrap,
        CancellationToken cancellationToken = default)
    {
        var held = await JoinHeldAsync(bootstrap, cancellationToken).ConfigureAwait(false);
        if (!held.Ok || held.Value is null)
        {
            return ConnectivityOutcome<JoinBinding>.Failure(
                held.Reason ?? ConnectivityReasons.JoinDenied,
                held.Detail ?? "join denied",
                stage: held.Stage,
                attemptId: held.AttemptId,
                retryable: held.Retryable);
        }

        await held.Value.DisposeAsync().ConfigureAwait(false);
        return ConnectivityOutcome<JoinBinding>.Success(held.Value.Binding);
    }

    public ValueTask<ConnectivityOutcome<HeldJoinSession>> JoinHeldAsync(
        JoinBootstrap bootstrap,
        CancellationToken cancellationToken = default) =>
        OutboundJoinWire.JoinHeldAsync(_endpoint, bootstrap, _bytePath, cancellationToken, _deviceKeys);
}
