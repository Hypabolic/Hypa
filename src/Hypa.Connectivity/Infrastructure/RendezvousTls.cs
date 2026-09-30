using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// TLS wrap for the rendezvous hop. Application encryption is a separate gate.
/// </summary>
public static class RendezvousTls
{
    public static bool UsesTls(RendezvousUrl url)
    {
        if (!Uri.TryCreate(url.Value, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme is "https";
    }

    public static Task<ConnectivityOutcome<SslStream>> WrapClientAsync(
        Stream inner,
        string targetHost,
        X509Certificate2? trust,
        CancellationToken cancellationToken) =>
        WrapClientAsync(inner, targetHost, trust, certificatePin: null, cancellationToken);

    public static async Task<ConnectivityOutcome<SslStream>> WrapClientAsync(
        Stream inner,
        string targetHost,
        X509Certificate2? trust,
        TlsCertificatePin? certificatePin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (string.IsNullOrWhiteSpace(targetHost))
        {
            return ConnectivityOutcome<SslStream>.Failure(
                ConnectivityReasons.RendezvousUrlInvalid,
                "tls target host is required");
        }

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };
            if (certificatePin is { } pin)
            {
                options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    pin.Matches(certificate);
            }
            else if (trust is not null)
            {
                var thumbprint = trust.Thumbprint;
                options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && string.Equals(certificate.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ssl.AuthenticateAsClientAsync(options, timeout.Token).ConfigureAwait(false);
            return ConnectivityOutcome<SslStream>.Success(ssl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return ConnectivityOutcome<SslStream>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "tls handshake timed out");
        }
        catch (AuthenticationException ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return ConnectivityOutcome<SslStream>.Failure(ConnectivityReasons.PeerUnavailable, ex.Message);
        }
        catch (IOException ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return ConnectivityOutcome<SslStream>.Failure(ConnectivityReasons.PeerUnavailable, ex.Message);
        }
        catch (ObjectDisposedException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return ConnectivityOutcome<SslStream>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "tls handshake closed");
        }
    }

    public static async Task<ConnectivityOutcome<SslStream>> WrapServerAsync(
        Stream inner,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(certificate);
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    },
                    timeout.Token)
                .ConfigureAwait(false);
            return ConnectivityOutcome<SslStream>.Success(ssl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return ConnectivityOutcome<SslStream>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "tls handshake timed out");
        }
        catch (AuthenticationException ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return ConnectivityOutcome<SslStream>.Failure(ConnectivityReasons.PeerUnavailable, ex.Message);
        }
        catch (IOException ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return ConnectivityOutcome<SslStream>.Failure(ConnectivityReasons.PeerUnavailable, ex.Message);
        }
        catch (ObjectDisposedException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            return ConnectivityOutcome<SslStream>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "tls handshake closed");
        }
    }
}
