using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;

namespace Hypa.Cli.Commands.Work;

/// <summary>
/// Outbound dest-worker join. Mux and client both dial. Dest HOME is absent.
/// </summary>
public sealed class ConnectivityDestWorkSessionOpener : IDestWorkSessionOpener
{
    private readonly RendezvousUrl? _url;
    private readonly JoinBootstrap? _bootstrap;

    public ConnectivityDestWorkSessionOpener()
    {
    }

    public ConnectivityDestWorkSessionOpener(RendezvousUrl url, JoinBootstrap bootstrap)
    {
        _url = url;
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
    }

    public RendezvousUrl? Url => _url;

    public JoinBootstrap? Bootstrap => _bootstrap;

    public static ConnectivityOutcome<ConnectivityDestWorkSessionOpener> Create(
        string? relayUrl,
        string destPlacementId,
        string? nonce,
        string? deviceId,
        string? joinSecret,
        JoinRole role)
    {
        if (string.IsNullOrWhiteSpace(relayUrl)
            || string.IsNullOrWhiteSpace(destPlacementId)
            || string.IsNullOrWhiteSpace(nonce)
            || string.IsNullOrWhiteSpace(deviceId)
            || string.IsNullOrWhiteSpace(joinSecret))
        {
            return ConnectivityOutcome<ConnectivityDestWorkSessionOpener>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "dest worker session is not joined");
        }

        var parsedUrl = RendezvousUrl.ParseOutcome(relayUrl);
        if (!parsedUrl.Ok)
        {
            return ConnectivityOutcome<ConnectivityDestWorkSessionOpener>.Failure(
                parsedUrl.Reason ?? ConnectivityReasons.RendezvousUrlInvalid,
                parsedUrl.Detail ?? "relay url invalid");
        }

        if (!PlacementId.TryParse(destPlacementId, out var placement))
        {
            return ConnectivityOutcome<ConnectivityDestWorkSessionOpener>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "dest placement id is invalid");
        }

        if (!JoinNonce.TryParse(nonce, out var joinNonce))
        {
            return ConnectivityOutcome<ConnectivityDestWorkSessionOpener>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "join nonce is invalid");
        }

        if (!DeviceId.TryParse(deviceId, out var device))
        {
            return ConnectivityOutcome<ConnectivityDestWorkSessionOpener>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "device id is invalid");
        }

        if (!JoinSecret.TryParse(joinSecret, out var secret) || secret.IsEmpty)
        {
            return ConnectivityOutcome<ConnectivityDestWorkSessionOpener>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "join secret is required");
        }

        var now = DateTimeOffset.UtcNow;
        var issued = new DeveloperJoinCapabilityIssuer().Issue(
            placement,
            device,
            role,
            joinNonce,
            now.AddMinutes(1),
            now,
            secret);
        if (!issued.Ok || issued.Value is null)
        {
            return ConnectivityOutcome<ConnectivityDestWorkSessionOpener>.Failure(
                issued.Reason ?? ConnectivityReasons.BootstrapInvalid,
                issued.Detail ?? "dest worker join capability failed");
        }

        var bootstrap = new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = role,
            PlacementId = placement,
            Nonce = joinNonce,
            StreamClass = StreamClass.Control,
            Capability = issued.Value,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
        };
        return ConnectivityOutcome<ConnectivityDestWorkSessionOpener>.Success(
            new ConnectivityDestWorkSessionOpener(parsedUrl.Value, bootstrap));
    }

    public async ValueTask<ConnectivityOutcome<IFramedSession>> OpenAsync(
        string destPlacementId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_url is null || _bootstrap is null)
        {
            return ConnectivityOutcome<IFramedSession>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "dest worker session is not joined");
        }

        if (!string.IsNullOrWhiteSpace(destPlacementId)
            && !string.Equals(destPlacementId.Trim(), _bootstrap.PlacementId.Value, StringComparison.Ordinal))
        {
            return ConnectivityOutcome<IFramedSession>.Failure(
                ConnectivityReasons.JoinDenied,
                "dest placement does not match join");
        }

        var connected = await OutboundFramedSession.ConnectAsync(
                _url.Value,
                _bootstrap,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!connected.Ok || connected.Value is null)
        {
            return ConnectivityOutcome<IFramedSession>.Failure(
                connected.Reason ?? ConnectivityReasons.PeerUnavailable,
                connected.Detail ?? "dest worker session is not joined");
        }

        return ConnectivityOutcome<IFramedSession>.Success(connected.Value);
    }
}
