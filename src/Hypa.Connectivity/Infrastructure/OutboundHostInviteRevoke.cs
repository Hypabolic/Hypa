using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Outbound host-invite revoke. Uses its own connection. Does not join the mux.
/// </summary>
public sealed class OutboundHostInviteRevoke
{
    private readonly IBytePath? _bytePath;
    private readonly DevicePairingService _pairing;
    private readonly TimeProvider _clock;

    public OutboundHostInviteRevoke(
        DevicePairingService pairing,
        IBytePath? bytePath = null,
        TimeProvider? clock = null)
    {
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _bytePath = bytePath;
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<ConnectivityOutcome> RevokeAsync(
        HostInviteRevokeReach reach,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reach);
        if (!HostInviteReach.TryValidate(reach.Host, reach.Port, out var host, out var hostError))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.InviteInvalid,
                hostError ?? "invite host is invalid");
        }

        if (!TlsCertificatePin.TryParse(reach.CertificateSha256, out var pin))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.InviteInvalid,
                "certificate fingerprint is invalid");
        }

        var listed = await _pairing.ListAsync(OperatorIdentity.LocalSelfHosted, cancellationToken)
            .ConfigureAwait(false);
        if (!listed.Ok || listed.Value is null)
        {
            return ConnectivityOutcome.Failure(
                listed.Reason ?? ConnectivityReasons.Unauthorized,
                listed.Detail ?? "local device is missing");
        }

        var device = listed.Value.FirstOrDefault(d =>
            string.Equals(d.Id.Value, deviceId.Value, StringComparison.Ordinal));
        if (device is null)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device is not enrolled");
        }

        var pkcs8 = await _pairing.KeyStore.LoadPrivateKeyAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);
        if (pkcs8 is null || pkcs8.Length == 0)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device private key is missing");
        }

        var stamped = HostInviteAuthenticator.Stamp(
            new HostInviteRevokeRequest
            {
                DeviceId = deviceId,
                DevicePublicKeySpkiBase64 = device.PublicKeySpkiBase64,
                IssuedAt = _clock.GetUtcNow(),
                Signature = "",
            },
            pkcs8);
        if (!stamped.Ok || stamped.Value is null)
        {
            return ConnectivityOutcome.Failure(
                stamped.Reason ?? ConnectivityReasons.Unauthorized,
                stamped.Detail ?? "device signature is required");
        }

        var path = _bytePath ?? new QuicTcpFallbackBytePath(certificatePin: pin);
        var endpoint = new BytePathEndpoint
        {
            Host = host,
            Port = reach.Port,
            Tls = true,
            TlsServerName = host,
            QuicListening = null,
        };
        if (!BytePathEndpointRules.TryValidate(endpoint, out var invalid))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                invalid);
        }

        var opened = await path.OpenAsync(
                new BytePathRequest
                {
                    Provider = path.Provider,
                    Endpoint = endpoint,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!opened.Ok || opened.Value is null)
        {
            return ConnectivityOutcome.Failure(
                opened.Reason ?? ConnectivityReasons.PeerUnavailable,
                opened.Detail ?? "path open failed");
        }

        await using var lifetime = opened.Value.Lifetime;
        await BoundedJoinLine.WriteAsync(
                opened.Value.Stream,
                HostInviteRevokeCodec.Write(stamped.Value),
                cancellationToken)
            .ConfigureAwait(false);
        var line = await BoundedJoinLine.ReadAsync(opened.Value.Stream, cancellationToken)
            .ConfigureAwait(false);
        if (!line.Ok || line.Value is null)
        {
            return ConnectivityOutcome.Failure(
                line.Reason ?? ConnectivityReasons.PeerUnavailable,
                line.Detail ?? "revoke result is missing");
        }

        var result = JoinBootstrapCodec.ReadResult(line.Value);
        if (!result.Ok || result.Value is null || !result.Value.Ok)
        {
            return ConnectivityOutcome.Failure(
                result.Value?.Reason ?? result.Reason ?? ConnectivityReasons.Unauthorized,
                result.Value?.Detail ?? result.Detail ?? "invite revoke failed");
        }

        return ConnectivityOutcome.Success();
    }
}
