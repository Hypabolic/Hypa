using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Outbound host-invite redeem. Uses its own connection. Does not join the mux.
/// </summary>
public sealed class OutboundHostInviteRedeem
{
    /// <summary>
    /// Time one address may take to answer. The next address starts after this.
    /// The path may try QUIC and then fall back to TCP, so this is longer than
    /// twice the QUIC dial time.
    /// </summary>
    public static TimeSpan AddressAttemptTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Least time the whole redeem may spend on address attempts. A longer
    /// list gets <see cref="AddressAttemptTimeout"/> for each address, so a
    /// dead address early in the list never stops the attempts at a later one.
    /// </summary>
    public static TimeSpan TotalAttemptTimeout { get; } = TimeSpan.FromSeconds(60);

    private readonly IBytePath? _bytePath;
    private readonly DevicePairingService _pairing;
    private readonly TimeProvider _clock;

    public OutboundHostInviteRedeem(
        DevicePairingService pairing,
        IBytePath? bytePath = null,
        TimeProvider? clock = null)
    {
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _bytePath = bytePath;
        _clock = clock ?? TimeProvider.System;
    }

    private static TimeSpan TotalBudget(int hostCount)
    {
        var perAddress = AddressAttemptTimeout * hostCount;
        return perAddress > TotalAttemptTimeout ? perAddress : TotalAttemptTimeout;
    }

    public async ValueTask<ConnectivityOutcome<HostInviteRedeemResult>> RedeemAsync(
        HostInvite invite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        if (!TlsCertificatePin.TryParse(invite.CertificateSha256, out var pin))
        {
            return ConnectivityOutcome<HostInviteRedeemResult>.Failure(
                ConnectivityReasons.InviteInvalid,
                "certificate fingerprint is invalid");
        }

        var path = _bytePath ?? new QuicTcpFallbackBytePath(certificatePin: pin);
        var local = await _pairing.EnsureLocalDeviceAsync(
                OperatorIdentity.LocalSelfHosted,
                cancellationToken)
            .ConfigureAwait(false);
        if (!local.Ok || local.Value is null)
        {
            return ConnectivityOutcome<HostInviteRedeemResult>.Failure(
                local.Reason ?? ConnectivityReasons.Unauthorized,
                local.Detail ?? "local device is missing");
        }

        var pkcs8 = await _pairing.KeyStore.LoadPrivateKeyAsync(local.Value.Id, cancellationToken)
            .ConfigureAwait(false);
        if (pkcs8 is null || pkcs8.Length == 0)
        {
            return ConnectivityOutcome<HostInviteRedeemResult>.Failure(
                ConnectivityReasons.Unauthorized,
                "device private key is missing");
        }

        var hosts = invite.Hosts is { Count: > 0 } ? invite.Hosts : [invite.Host];
        string? lastReason = null;
        string? lastDetail = null;
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TotalBudget(hosts.Count));
        foreach (var host in hosts)
        {
            if (total.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                lastReason = ConnectivityReasons.PeerUnavailable;
                lastDetail = "no address answered in time";
                break;
            }

            var endpoint = new BytePathEndpoint
            {
                Host = host,
                Port = invite.Port,
                Tls = true,
                TlsServerName = host,
                QuicListening = null,
            };
            if (!BytePathEndpointRules.TryValidate(endpoint, out var invalid))
            {
                lastReason = ConnectivityReasons.PeerUnavailable;
                lastDetail = invalid;
                continue;
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            attempt.CancelAfter(AddressAttemptTimeout);
            ConnectivityOutcome<BytePathHandle> opened;
            try
            {
                opened = await path.OpenAsync(
                        new BytePathRequest
                        {
                            Provider = path.Provider,
                            Endpoint = endpoint,
                        },
                        attempt.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastReason = ConnectivityReasons.PeerUnavailable;
                lastDetail = "address did not answer";
                continue;
            }

            if (!opened.Ok || opened.Value is null)
            {
                lastReason = opened.Reason ?? ConnectivityReasons.PeerUnavailable;
                lastDetail = opened.Detail ?? "path open failed";
                continue;
            }

            await using var lifetime = opened.Value.Lifetime;
            var unsigned = new HostInviteRedeemRequest
            {
                InviteId = invite.InviteId,
                Secret = invite.Secret,
                DeviceId = local.Value.Id,
                DevicePublicKeySpkiBase64 = local.Value.PublicKeySpkiBase64,
                IssuedAt = _clock.GetUtcNow(),
                Signature = "",
            };
            var stamped = HostInviteAuthenticator.Stamp(unsigned, pkcs8);
            if (!stamped.Ok || stamped.Value is null)
            {
                return ConnectivityOutcome<HostInviteRedeemResult>.Failure(
                    stamped.Reason ?? ConnectivityReasons.Unauthorized,
                    stamped.Detail ?? "device signature is required");
            }

            ConnectivityOutcome<string> line;
            try
            {
                await BoundedJoinLine.WriteAsync(
                        opened.Value.Stream,
                        HostInviteRedeemCodec.Write(stamped.Value),
                        total.Token)
                    .ConfigureAwait(false);
                line = await BoundedJoinLine.ReadAsync(opened.Value.Stream, total.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ConnectivityOutcome<HostInviteRedeemResult>.Failure(
                    ConnectivityReasons.PeerUnavailable,
                    "the host did not answer the redeem in time");
            }

            if (!line.Ok || line.Value is null)
            {
                return ConnectivityOutcome<HostInviteRedeemResult>.Failure(
                    line.Reason ?? ConnectivityReasons.PeerUnavailable,
                    line.Detail ?? "redeem result is missing");
            }

            var result = JoinBootstrapCodec.ReadResult(line.Value);
            if (!result.Ok || result.Value is null || !result.Value.Ok)
            {
                return ConnectivityOutcome<HostInviteRedeemResult>.Failure(
                    result.Value?.Reason ?? result.Reason ?? ConnectivityReasons.InviteInvalid,
                    result.Value?.Detail ?? result.Detail ?? "invite redeem failed");
            }

            var enrolled = await _pairing.RecordLocalInviteEnrollmentAsync(
                    OperatorIdentity.LocalSelfHosted,
                    local.Value.Id,
                    _clock.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!enrolled.Ok || enrolled.Value is null)
            {
                return ConnectivityOutcome<HostInviteRedeemResult>.Failure(
                    enrolled.Reason ?? ConnectivityReasons.Unauthorized,
                    enrolled.Detail ?? "local device is missing");
            }

            return ConnectivityOutcome<HostInviteRedeemResult>.Success(new HostInviteRedeemResult
            {
                Device = enrolled.Value,
                Address = host,
            });
        }

        return ConnectivityOutcome<HostInviteRedeemResult>.Failure(
            lastReason ?? ConnectivityReasons.PeerUnavailable,
            lastDetail ?? "no advertised address answered");
    }
}
