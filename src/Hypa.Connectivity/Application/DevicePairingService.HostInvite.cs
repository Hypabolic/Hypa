using System.Security.Cryptography;
using System.Text;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

public sealed partial class DevicePairingService
{
    public async ValueTask<ConnectivityOutcome<HostInvite>> IssueHostInviteAsync(
        OperatorIdentity operatorId,
        HostInviteIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var local = RequireLocalSelfHosted(operatorId);
        if (!local.Ok)
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                local.Reason ?? ConnectivityReasons.Unauthorized,
                local.Detail ?? "self-hosted pairing uses local-operator identity");
        }

        if (!TryCollectHosts(request, out var hosts, out var hostError))
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                ConnectivityReasons.InviteInvalid,
                hostError ?? "invite host is invalid");
        }

        if (!HostInviteFormat.TryNormalizeFingerprint(request.CertificateSha256, out var fingerprint, out var pinError))
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                ConnectivityReasons.InviteInvalid,
                pinError ?? "certificate fingerprint is invalid");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var secretBytes = new byte[HostInviteLimits.SecretBytes];
            RandomNumberGenerator.Fill(secretBytes);
            var secret = Convert.ToHexString(secretBytes).ToLowerInvariant();
            var inviteId = NewInviteId();
            var expiresAt = now.Add(PairingCodeLifetime.Max);
            var invite = new HostInvite
            {
                InviteId = inviteId,
                Secret = secret,
                Host = hosts[0],
                Hosts = hosts,
                Port = request.Port,
                CertificateSha256 = fingerprint,
                ExpiresAt = expiresAt,
                Session = string.IsNullOrWhiteSpace(request.Session) ? null : request.Session.Trim(),
                Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
                TransferValue = "",
            };
            invite = invite with { TransferValue = HostInviteCodec.Encode(invite) };

            var invites = loaded.HostInvites.ToList();
            invites.Add(new HostInviteRecord
            {
                Id = inviteId,
                SecretSha256 = HashSecret(secret),
                ExpiresAt = expiresAt,
                Consumed = false,
            });

            await _store.SaveAsync(
                    loaded with
                    {
                        OperatorId = OperatorIdentity.LocalSelfHosted,
                        HostInvites = invites,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return ConnectivityOutcome<HostInvite>.Success(invite);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ConnectivityOutcome<DeviceRecord>> RedeemAsync(
        HostInviteRedeemRequest request,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var invites = loaded.HostInvites.ToList();
            var index = invites.FindIndex(i =>
                string.Equals(i.Id.Value, request.InviteId.Value, StringComparison.Ordinal));
            if (index < 0)
            {
                return ConnectivityOutcome<DeviceRecord>.Failure(
                    ConnectivityReasons.InviteInvalid,
                    "invite is invalid");
            }

            var invite = invites[index];
            if (invite.FailedAttempts >= HostInviteLimits.MaxFailedAttempts)
            {
                return ConnectivityOutcome<DeviceRecord>.Failure(
                    ConnectivityReasons.InviteRateLimited,
                    "invite redeem is locked");
            }

            var proof = HostInviteAuthenticator.Verify(request);
            if (!proof.Ok)
            {
                return await RejectRedeemAttemptAsync(
                        loaded,
                        invites,
                        index,
                        proof.Reason ?? ConnectivityReasons.Unauthorized,
                        proof.Detail ?? "device signature is required",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (invite.Consumed)
            {
                if (invite.RedeemedBy is { } redeemed
                    && string.Equals(redeemed.Value, request.DeviceId.Value, StringComparison.Ordinal))
                {
                    var existing = loaded.Devices.FirstOrDefault(d =>
                        d.Id.Value == request.DeviceId.Value
                        && d.Status == DeviceTrustStatus.Trusted);
                    if (existing is not null)
                        return ConnectivityOutcome<DeviceRecord>.Success(existing);
                }

                return ConnectivityOutcome<DeviceRecord>.Failure(
                    ConnectivityReasons.InviteConsumed,
                    "invite already used");
            }

            if (invite.ExpiresAt <= utcNow)
            {
                invites[index] = invite with { Consumed = true };
                await _store.SaveAsync(
                        loaded with { HostInvites = invites },
                        cancellationToken)
                    .ConfigureAwait(false);
                return ConnectivityOutcome<DeviceRecord>.Failure(
                    ConnectivityReasons.InviteExpired,
                    "invite expired");
            }

            if (!FixedTimeSecretEquals(invite.SecretSha256, request.Secret))
            {
                return await RejectRedeemAttemptAsync(
                        loaded,
                        invites,
                        index,
                        ConnectivityReasons.InviteInvalid,
                        "invite is invalid",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var devices = loaded.Devices.ToList();
            var deviceIndex = devices.FindIndex(d => d.Id.Value == request.DeviceId.Value);
            DeviceRecord trusted;
            if (deviceIndex >= 0)
            {
                var device = devices[deviceIndex];
                if (!string.Equals(
                        device.PublicKeySpkiBase64,
                        request.DevicePublicKeySpkiBase64,
                        StringComparison.Ordinal))
                {
                    return ConnectivityOutcome<DeviceRecord>.Failure(
                        ConnectivityReasons.Unauthorized,
                        "device public key does not match");
                }

                if (device.Status == DeviceTrustStatus.Revoked)
                {
                    return ConnectivityOutcome<DeviceRecord>.Failure(
                        ConnectivityReasons.Unauthorized,
                        "device is revoked");
                }

                trusted = device with
                {
                    Status = DeviceTrustStatus.Trusted,
                    ApprovedAt = utcNow,
                    ApprovedBy = PairingApproverKind.InviteRedeem,
                };
                devices[deviceIndex] = trusted;
            }
            else
            {
                trusted = new DeviceRecord
                {
                    Id = request.DeviceId,
                    PublicKeySpkiBase64 = request.DevicePublicKeySpkiBase64,
                    Status = DeviceTrustStatus.Trusted,
                    CreatedAt = utcNow,
                    ApprovedAt = utcNow,
                    ApprovedBy = PairingApproverKind.InviteRedeem,
                };
                devices.Add(trusted);
            }

            invites[index] = invite with
            {
                Consumed = true,
                RedeemedBy = request.DeviceId,
            };

            await _store.SaveAsync(
                    loaded with
                    {
                        OperatorId = OperatorIdentity.LocalSelfHosted,
                        Devices = devices,
                        HostInvites = invites,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return ConnectivityOutcome<DeviceRecord>.Success(trusted);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ConnectivityOutcome> RevokeEnrolledAsync(
        HostInviteRevokeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var proof = HostInviteAuthenticator.Verify(request);
        if (!proof.Ok)
        {
            return ConnectivityOutcome.Failure(
                proof.Reason ?? ConnectivityReasons.Unauthorized,
                proof.Detail ?? "device signature is required");
        }

        return await RevokeAsync(OperatorIdentity.LocalSelfHosted, request.DeviceId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ConnectivityOutcome> RecordFailedRedeemAttemptAsync(
        InviteId inviteId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var invites = loaded.HostInvites.ToList();
            var index = invites.FindIndex(i =>
                string.Equals(i.Id.Value, inviteId.Value, StringComparison.Ordinal));
            if (index < 0)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.InviteInvalid,
                    "invite is invalid");
            }

            if (invites[index].FailedAttempts >= HostInviteLimits.MaxFailedAttempts)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.InviteRateLimited,
                    "invite redeem is locked");
            }

            var rejected = await RejectRedeemAttemptAsync(
                    loaded,
                    invites,
                    index,
                    ConnectivityReasons.InviteInvalid,
                    "invite is invalid",
                    cancellationToken)
                .ConfigureAwait(false);
            return rejected.WithoutValue();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ConnectivityOutcome<DeviceRecord>> EnsureLocalDeviceAsync(
        OperatorIdentity operatorId,
        CancellationToken cancellationToken = default)
    {
        var local = RequireLocalSelfHosted(operatorId);
        if (!local.Ok)
        {
            return ConnectivityOutcome<DeviceRecord>.Failure(
                local.Reason ?? ConnectivityReasons.Unauthorized,
                local.Detail ?? "self-hosted pairing uses local-operator identity");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var device in loaded.Devices)
            {
                if (device.Status == DeviceTrustStatus.Revoked)
                    continue;
                var key = await _keys.LoadPrivateKeyAsync(device.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (key is { Length: > 0 })
                    return ConnectivityOutcome<DeviceRecord>.Success(device);
            }

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
            var spki = ecdsa.ExportSubjectPublicKeyInfo();
            var deviceId = DeviceIdFromPublicKey(spki);
            await _keys.StorePrivateKeyAsync(deviceId, pkcs8, cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var created = new DeviceRecord
            {
                Id = deviceId,
                PublicKeySpkiBase64 = Convert.ToBase64String(spki),
                Status = DeviceTrustStatus.Pending,
                CreatedAt = now,
            };
            var devices = loaded.Devices.ToList();
            devices.Add(created);
            await _store.SaveAsync(
                    loaded with
                    {
                        OperatorId = OperatorIdentity.LocalSelfHosted,
                        Devices = devices,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return ConnectivityOutcome<DeviceRecord>.Success(created);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ConnectivityOutcome<DeviceRecord>> RecordLocalInviteEnrollmentAsync(
        OperatorIdentity operatorId,
        DeviceId deviceId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        var local = RequireLocalSelfHosted(operatorId);
        if (!local.Ok)
        {
            return ConnectivityOutcome<DeviceRecord>.Failure(
                local.Reason ?? ConnectivityReasons.Unauthorized,
                local.Detail ?? "self-hosted pairing uses local-operator identity");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var devices = loaded.Devices.ToList();
            var index = devices.FindIndex(d => d.Id.Value == deviceId.Value);
            if (index < 0)
            {
                return ConnectivityOutcome<DeviceRecord>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "device is missing");
            }

            var trusted = devices[index] with
            {
                Status = DeviceTrustStatus.Trusted,
                ApprovedAt = utcNow,
                ApprovedBy = PairingApproverKind.InviteRedeem,
            };
            devices[index] = trusted;
            await _store.SaveAsync(
                    loaded with { Devices = devices },
                    cancellationToken)
                .ConfigureAwait(false);
            return ConnectivityOutcome<DeviceRecord>.Success(trusted);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ConnectivityOutcome<DeviceRecord>> RejectRedeemAttemptAsync(
        DevicePairingSnapshot loaded,
        List<HostInviteRecord> invites,
        int index,
        string reason,
        string detail,
        CancellationToken cancellationToken)
    {
        var attempts = invites[index].FailedAttempts + 1;
        invites[index] = invites[index] with { FailedAttempts = attempts };
        await _store.SaveAsync(
                loaded with { HostInvites = invites },
                cancellationToken)
            .ConfigureAwait(false);
        return ConnectivityOutcome<DeviceRecord>.Failure(
            attempts >= HostInviteLimits.MaxFailedAttempts
                ? ConnectivityReasons.InviteRateLimited
                : reason,
            attempts >= HostInviteLimits.MaxFailedAttempts
                ? "invite redeem is locked"
                : detail);
    }

    private static bool TryCollectHosts(
        HostInviteIssueRequest request,
        out IReadOnlyList<string> hosts,
        out string? error)
    {
        var collected = new List<string>();
        error = null;
        if (request.Hosts is { Count: > 0 })
        {
            if (request.Hosts.Count > HostInviteReach.MaxHosts)
            {
                hosts = [];
                error = "invite lists too many hosts";
                return false;
            }

            foreach (var candidate in request.Hosts)
            {
                if (!HostInviteReach.TryValidate(candidate, request.Port, out var normalized, out error))
                {
                    hosts = [];
                    return false;
                }

                if (!collected.Exists(existing =>
                        string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    collected.Add(normalized);
                }
            }
        }
        else if (!HostInviteReach.TryValidate(request.Host, request.Port, out var host, out error))
        {
            hosts = [];
            return false;
        }
        else
        {
            collected.Add(host);
        }

        if (collected.Count == 0)
        {
            hosts = [];
            error = "invite host is required";
            return false;
        }

        hosts = collected;
        return true;
    }

    internal static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static bool FixedTimeSecretEquals(string storedHash, string secret)
    {
        var computed = HashSecret(secret);
        var left = Encoding.UTF8.GetBytes(storedHash);
        var right = Encoding.UTF8.GetBytes(computed);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static InviteId NewInviteId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var id = "inv_" + Convert.ToHexString(bytes).ToLowerInvariant();
        if (!InviteId.TryParse(id, out var inviteId))
            throw new InvalidOperationException("generated invite id is invalid");
        return inviteId;
    }
}
