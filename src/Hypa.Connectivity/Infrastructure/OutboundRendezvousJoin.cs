using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Outbound join client. Mux and client both dial this path. Neither listens.
/// </summary>
public sealed class OutboundRendezvousJoin : IRendezvousJoin
{
    private readonly RendezvousUrl _url;
    private readonly IBytePath _bytePath;

    public OutboundRendezvousJoin(
        RendezvousUrl url,
        X509Certificate2? tlsTrust = null,
        IBytePath? bytePath = null)
    {
        _url = url;
        _bytePath = bytePath ?? new TcpTlsBytePath(tlsTrust);
    }

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

    /// <summary>
    /// Join and keep the byte path. Dispose the session to mark this client disconnected.
    /// </summary>
    public async ValueTask<ConnectivityOutcome<HeldJoinSession>> JoinHeldAsync(
        JoinBootstrap bootstrap,
        CancellationToken cancellationToken = default)
    {
        if (!BytePathEndpointRules.TryFromRendezvousUrl(_url, out var endpoint, out var endpointDetail))
        {
            return ConnectivityOutcome<HeldJoinSession>.Failure(
                ConnectivityReasons.RendezvousUrlInvalid,
                endpointDetail);
        }

        return await OutboundJoinWire.JoinHeldAsync(endpoint, bootstrap, _bytePath, cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool TryGetEndpoint(RendezvousUrl url, out string host, out int port) =>
        TryGetEndpoint(url, out host, out port, out _);

    public static bool TryGetEndpoint(RendezvousUrl url, out string host, out int port, out bool tls)
    {
        host = "";
        port = 0;
        tls = false;
        if (!BytePathEndpointRules.TryFromRendezvousUrl(url, out var endpoint, out _))
            return false;

        host = endpoint.Host;
        port = endpoint.Port;
        tls = endpoint.Tls;
        return true;
    }
}
