using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>Outbound TCP dial with optional TLS. One adapter for relay and direct host targets.</summary>
public sealed class TcpTlsBytePath : IBytePath
{
    private readonly X509Certificate2? _tlsTrust;
    private readonly TlsCertificatePin? _certificatePin;

    public TcpTlsBytePath(X509Certificate2? tlsTrust = null, TlsCertificatePin? certificatePin = null)
    {
        _tlsTrust = tlsTrust;
        _certificatePin = certificatePin;
    }

    public string Provider => BytePathProviders.Tcp;

    public async ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
        BytePathRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Provider, Provider, StringComparison.Ordinal))
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.Internal,
                "byte path provider is not supported");
        }

        if (!BytePathEndpointRules.TryValidate(request.Endpoint, out var invalid)
            || request.Endpoint is null)
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.PeerUnavailable,
                invalid);
        }

        var endpoint = request.Endpoint;
        TcpClient? client = null;
        try
        {
            client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
            var opened = await OpenTransportAsync(
                    client,
                    endpoint,
                    _tlsTrust,
                    _certificatePin,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!opened.Ok || opened.Value is null)
            {
                client.Dispose();
                return ConnectivityOutcome<BytePathHandle>.Failure(
                    opened.Reason ?? ConnectivityReasons.PeerUnavailable,
                    opened.Detail ?? "transport open failed");
            }

            var handle = new BytePathHandle
            {
                Stream = opened.Value,
                Lifetime = new ConnectionLifetime(client, opened.Value),
            };
            client = null;
            return ConnectivityOutcome<BytePathHandle>.Success(handle);
        }
        catch (OperationCanceledException)
        {
            client?.Dispose();
            throw;
        }
        catch (SocketException ex)
        {
            client?.Dispose();
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.PeerUnavailable,
                ex.Message);
        }
        catch (IOException ex)
        {
            client?.Dispose();
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.PeerUnavailable,
                ex.Message);
        }
    }

    public async ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _ = cancellationToken;
        try
        {
            await handle.Lifetime.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal static Task<ConnectivityOutcome<Stream>> OpenTransportAsync(
        TcpClient client,
        BytePathEndpoint endpoint,
        X509Certificate2? tlsTrust,
        CancellationToken cancellationToken) =>
        OpenTransportAsync(client, endpoint, tlsTrust, certificatePin: null, cancellationToken);

    internal static async Task<ConnectivityOutcome<Stream>> OpenTransportAsync(
        TcpClient client,
        BytePathEndpoint endpoint,
        X509Certificate2? tlsTrust,
        TlsCertificatePin? certificatePin,
        CancellationToken cancellationToken)
    {
        Stream inner = client.GetStream();
        if (!endpoint.Tls)
            return ConnectivityOutcome<Stream>.Success(inner);

        var serverName = endpoint.TlsServerName ?? endpoint.Host;
        var wrapped = await RendezvousTls.WrapClientAsync(
                inner,
                serverName,
                tlsTrust,
                certificatePin,
                cancellationToken)
            .ConfigureAwait(false);
        if (!wrapped.Ok || wrapped.Value is null)
        {
            return ConnectivityOutcome<Stream>.Failure(
                wrapped.Reason ?? ConnectivityReasons.PeerUnavailable,
                wrapped.Detail ?? "tls handshake failed");
        }

        return ConnectivityOutcome<Stream>.Success(wrapped.Value);
    }

    private sealed class ConnectionLifetime(TcpClient client, Stream stream) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
        }
    }
}
