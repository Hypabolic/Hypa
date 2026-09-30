using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>Outbound QUIC dial with one bidirectional stream as the byte path.</summary>
public sealed class QuicBytePath : IBytePath
{
    private readonly X509Certificate2? _tlsTrust;
    private readonly TlsCertificatePin? _certificatePin;
    private readonly IQuicTransportCapabilityProbe _probe;

    public QuicBytePath(
        X509Certificate2? tlsTrust = null,
        IQuicTransportCapabilityProbe? probe = null,
        TlsCertificatePin? certificatePin = null)
    {
        _tlsTrust = tlsTrust;
        _certificatePin = certificatePin;
        _probe = probe ?? new QuicTransportCapabilityProbe();
    }

    public string Provider => BytePathProviders.Quic;

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

        if (!QuicTransportRuntime.IsUsable(_probe))
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.QuicUnsupported,
                "quic transport is not supported on this host");
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.QuicUnsupported,
                "quic transport is not supported on this host");
        }

        var endpoint = request.Endpoint!;
        QuicConnection? connection = null;
        QuicStream? stream = null;
#pragma warning disable CA1416
        try
        {
            if (!IPAddress.TryParse(endpoint.Host, out var address))
            {
                try
                {
                    // Same-network dial: pick the first IPv4 or IPv6 address returned.
                    // Dual-stack hosts may need a literal IP when both families are reachable.
                    var resolved = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken)
                        .ConfigureAwait(false);
                    address = resolved.FirstOrDefault(static candidate =>
                        candidate.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork
                            or System.Net.Sockets.AddressFamily.InterNetworkV6);
                    if (address is null)
                    {
                        return ConnectivityOutcome<BytePathHandle>.Failure(
                            ConnectivityReasons.PeerUnavailable,
                            "quic host did not resolve");
                    }
                }
                catch (SocketException ex)
                {
                    return ConnectivityOutcome<BytePathHandle>.Failure(
                        ConnectivityReasons.PeerUnavailable,
                        ex.Message);
                }
            }

            var targetHost = QuicTransportConstants.ClientTargetHost(
                endpoint.TlsServerName,
                endpoint.Host);
            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ApplicationProtocols = [QuicTransportConstants.ApplicationProtocol],
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };
            if (_certificatePin is { } pin)
            {
                sslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    pin.Matches(certificate);
            }
            else if (_tlsTrust is not null)
            {
                var thumbprint = _tlsTrust.Thumbprint;
                sslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && string.Equals(
                        certificate.GetCertHashString(),
                        thumbprint,
                        StringComparison.OrdinalIgnoreCase);
            }

            connection = await QuicConnection.ConnectAsync(
                    new QuicClientConnectionOptions
                    {
                        RemoteEndPoint = new IPEndPoint(address, endpoint.Port),
                        DefaultCloseErrorCode = QuicTransportConstants.DefaultCloseErrorCode,
                        DefaultStreamErrorCode = QuicTransportConstants.DefaultStreamErrorCode,
                        ClientAuthenticationOptions = sslOptions,
                        MaxInboundBidirectionalStreams = 0,
                        MaxInboundUnidirectionalStreams = 0,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            stream = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    cancellationToken)
                .ConfigureAwait(false);

            var handle = new BytePathHandle
            {
                Stream = stream,
                Lifetime = new ConnectionLifetime(connection, stream),
            };
            connection = null;
            stream = null;
            return ConnectivityOutcome<BytePathHandle>.Success(handle);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException ex)
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.PeerUnavailable,
                ex.Message);
        }
        catch (AuthenticationException ex)
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.PeerUnavailable,
                ex.Message);
        }
        catch (IOException ex)
        {
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.PeerUnavailable,
                ex.Message);
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning restore CA1416
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

    private sealed class ConnectionLifetime(QuicConnection connection, QuicStream stream) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
#pragma warning disable CA1416
            await stream.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
#pragma warning restore CA1416
        }
    }
}
