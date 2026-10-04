using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach.Cubes;

/// <summary>
/// Issue a host invite against a listener that already owns the accept
/// port and presents the session <c>accept.pfx</c>.
/// </summary>
internal static class ExistingAcceptShare
{
    internal const string NotListeningDetail = "accept is not listening";

    internal static bool ShouldStartHelper(
        ConnectivityOutcome<ConnectivityAcceptListenDocument> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Ok)
            return false;
        return IsNotListening(outcome) || IsMissingCertificate(outcome);
    }

    internal static bool IsNotListening(
        ConnectivityOutcome<ConnectivityAcceptListenDocument> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return string.Equals(outcome.Detail, NotListeningDetail, StringComparison.Ordinal);
    }

    internal static bool IsMissingCertificate(
        ConnectivityOutcome<ConnectivityAcceptListenDocument> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Detail is { } detail
            && (detail.Contains("accept certificate", StringComparison.OrdinalIgnoreCase)
                || string.Equals(detail, "certificate file is missing", StringComparison.Ordinal));
    }

    internal static async Task<ConnectivityOutcome<ConnectivityAcceptListenDocument>> TryIssueAsync(
        string session,
        string bindHost,
        int port,
        IReadOnlyList<string> advertiseHosts,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session))
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "home mux session is missing");
        }

        string socketPath;
        try
        {
            socketPath = UnixSocketServer.ResolveSocketPath(session, honorEnvironment: false);
        }
        catch (ArgumentException ex)
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                ex.Message);
        }

        var sessionDir = Path.GetDirectoryName(socketPath);
        var pfxPath = string.IsNullOrEmpty(sessionDir)
            ? null
            : Path.Combine(sessionDir, "accept.pfx");
        if (string.IsNullOrEmpty(pfxPath))
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "accept certificate path is missing");
        }

        var loaded = AcceptListenCertificate.LoadPfx(pfxPath);
        if (!loaded.Ok || loaded.Value is null)
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                loaded.Detail ?? "accept certificate is missing");
        }

        using var certificate = loaded.Value;
        var expected = AcceptListenCertificate.Sha256Fingerprint(certificate);
        string? presented;
        try
        {
            presented = await ReadListenerFingerprintAsync(port, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException ex) when (IsNotListeningSocket(ex))
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.PeerUnavailable,
                NotListeningDetail);
        }
        catch (SocketException)
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                $"port {port} is already in use");
        }
        catch (AuthenticationException)
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                $"port {port} is already in use");
        }
        catch (IOException)
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                $"port {port} is already in use");
        }

        if (!string.Equals(presented, expected, StringComparison.OrdinalIgnoreCase))
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                $"port {port} is already in use");
        }

        if (!ListenerBindMatches(bindHost, port))
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "listener bind differs");
        }

        DevicePairingService pairing;
        try
        {
            var directory = DevicePairingStatePaths.ResolveFromEnvironment();
            Directory.CreateDirectory(directory);
            pairing = new DevicePairingService(
                new FileDevicePairingStore(directory),
                PlatformDeviceKeyStore.CreateOrFallback(
                    DevicePairingStatePaths.FallbackKeyDirectory(directory)));
        }
        catch (InvalidDataException ex)
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                ex.Message);
        }

        if (advertiseHosts is not { Count: > 0 })
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                ConnectivityReasons.InviteInvalid,
                "no reachable address");
        }

        var issued = await pairing.IssueHostInviteAsync(
                OperatorIdentity.LocalSelfHosted,
                new HostInviteIssueRequest
                {
                    Host = advertiseHosts[0],
                    Hosts = advertiseHosts,
                    Port = port,
                    CertificateSha256 = expected,
                    Session = session,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!issued.Ok || issued.Value is null)
        {
            return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
                issued.Reason ?? ConnectivityReasons.InviteInvalid,
                issued.Detail ?? "host invite was not issued");
        }

        return ConnectivityOutcome<ConnectivityAcceptListenDocument>.Success(
            new ConnectivityAcceptListenDocument
            {
                Ok = true,
                Bind = bindHost,
                Port = port,
                Tls = true,
                CertificateSha256 = expected,
                Invite = issued.Value.TransferValue,
            });
    }

    internal static async Task<string?> ReadListenerFingerprintAsync(
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cancellationToken)
            .ConfigureAwait(false);
        string? fingerprint = null;
        await using var stream = client.GetStream();
        using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        if (certificate is X509Certificate2 typed)
                            fingerprint = AcceptListenCertificate.Sha256Fingerprint(typed);
                        else if (certificate is not null)
                            fingerprint = AcceptListenCertificate.Sha256Fingerprint(
                                new X509Certificate2(certificate));
                        return true;
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);
        return fingerprint;
    }

    internal static bool ListenerBindMatches(string bindHost, int port)
    {
        if (!TryParseBindAddress(bindHost, out var requested))
            return false;

        IPEndPoint[] listeners;
        try
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch (NetworkInformationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }

        foreach (var listener in listeners)
        {
            if (listener.Port != port)
                continue;
            if (SameBindAddress(listener.Address, requested))
                return true;
        }

        return false;
    }

    private static bool TryParseBindAddress(string bindHost, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(bindHost))
            return false;

        var trimmed = bindHost.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            trimmed = trimmed[1..^1];
        var zone = trimmed.IndexOf('%');
        if (zone >= 0)
            trimmed = trimmed[..zone];
        return IPAddress.TryParse(trimmed, out address!);
    }

    private static bool SameBindAddress(IPAddress actual, IPAddress requested)
    {
        if (actual.Equals(requested))
            return true;
        if (actual.IsIPv4MappedToIPv6 && actual.MapToIPv4().Equals(requested))
            return true;
        if (requested.IsIPv4MappedToIPv6 && requested.MapToIPv4().Equals(actual))
            return true;
        return false;
    }

    internal static bool IsNotListeningSocket(SocketException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.SocketErrorCode is SocketError.ConnectionRefused
            or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable
            or SocketError.TimedOut
            or SocketError.AddressNotAvailable;
    }
}
