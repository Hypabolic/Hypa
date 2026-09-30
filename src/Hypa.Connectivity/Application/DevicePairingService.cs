using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// self-hosted device pairing and join-capability issue. This is not a hosted account.
/// </summary>
public sealed partial class DevicePairingService : IJoinTrustGate, IHostInviteRedeemer
{
    private const string PairingAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly IDevicePairingStore _store;
    private readonly IDeviceKeyStore _keys;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IActiveJoinCloser? _joinCloser;

    public DevicePairingService(
        IDevicePairingStore store,
        IDeviceKeyStore keys,
        TimeProvider? clock = null,
        IActiveJoinCloser? joinCloser = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _clock = clock ?? TimeProvider.System;
        _joinCloser = joinCloser;
    }

    public IDeviceKeyStore KeyStore => _keys;

    public string PairingStorePath => _store.FilePath;

    public void AttachJoinCloser(IActiveJoinCloser closer)
    {
        _joinCloser = closer ?? throw new ArgumentNullException(nameof(closer));
    }

    public async ValueTask<ConnectivityOutcome<PairingOffer>> StartPairingAsync(
        OperatorIdentity operatorId,
        CancellationToken cancellationToken = default)
    {
        var local = RequireLocalSelfHosted(operatorId);
        if (!local.Ok)
        {
            return ConnectivityOutcome<PairingOffer>.Failure(
                local.Reason ?? ConnectivityReasons.Unauthorized,
                local.Detail ?? "self-hosted pairing uses local-operator identity");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
            var spki = ecdsa.ExportSubjectPublicKeyInfo();
            var deviceId = DeviceIdFromPublicKey(spki);
            await _keys.StorePrivateKeyAsync(deviceId, pkcs8, cancellationToken).ConfigureAwait(false);

            var now = _clock.GetUtcNow();
            var code = NewPairingCode();
            var expiresAt = now.Add(PairingCodeLifetime.Max);
            var unix = expiresAt.ToUnixTimeSeconds();
            var spkiUrl = PairingOfferFormat.ToBase64Url(spki);
            var body = PairingOfferFormat.SignedBody(code, unix, spkiUrl);
            var signature = ecdsa.SignData(
                Encoding.UTF8.GetBytes(body),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var offer = new PairingOffer
            {
                DeviceId = deviceId,
                OperatorId = OperatorIdentity.LocalSelfHosted,
                PairingCode = code,
                QrValue = PairingOfferFormat.EncodeQr(
                    code,
                    unix,
                    spkiUrl,
                    PairingOfferFormat.ToBase64Url(signature)),
                ExpiresAt = expiresAt,
                PublicKeyFingerprint = Fingerprint(spki),
                PublicKeySpkiBase64 = Convert.ToBase64String(spki),
            };

            var devices = loaded.Devices.ToList();
            devices.Add(new DeviceRecord
            {
                Id = deviceId,
                PublicKeySpkiBase64 = Convert.ToBase64String(spki),
                Status = DeviceTrustStatus.Pending,
                CreatedAt = now,
            });

            var challenges = loaded.PairingChallenges.ToList();
            challenges.Add(new PairingChallenge
            {
                CodeSha256 = HashPairingCode(code),
                DeviceId = deviceId,
                ExpiresAt = offer.ExpiresAt,
                Consumed = false,
            });

            await _store.SaveAsync(
                    loaded with
                    {
                        OperatorId = OperatorIdentity.LocalSelfHosted,
                        Devices = devices,
                        PairingChallenges = challenges,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return ConnectivityOutcome<PairingOffer>.Success(offer);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ConnectivityOutcome<DeviceRecord>> ApproveAsync(
        OperatorIdentity operatorId,
        string? codeOrQr,
        PairingApprover approver,
        CancellationToken cancellationToken = default)
    {
        var local = RequireLocalSelfHosted(operatorId);
        if (!local.Ok)
        {
            return ConnectivityOutcome<DeviceRecord>.Failure(
                local.Reason ?? ConnectivityReasons.Unauthorized,
                local.Detail ?? "self-hosted pairing uses local-operator identity");
        }

        if (approver is null)
        {
            return ConnectivityOutcome<DeviceRecord>.Failure(
                ConnectivityReasons.Unauthorized,
                "pairing approver is required");
        }

        if (!TryResolvePairingInput(codeOrQr, out var code, out var transferable, out var resolveError))
        {
            return ConnectivityOutcome<DeviceRecord>.Failure(
                ConnectivityReasons.Unauthorized,
                resolveError ?? "pairing code is invalid");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            if (transferable is not null)
            {
                var imported = ImportTransferable(loaded, transferable, now);
                if (!imported.Ok || imported.Value is null)
                {
                    return ConnectivityOutcome<DeviceRecord>.Failure(
                        imported.Reason ?? ConnectivityReasons.Unauthorized,
                        imported.Detail ?? "pairing code is invalid");
                }

                loaded = imported.Value;
            }

            var hash = HashPairingCode(code);
            var challenges = loaded.PairingChallenges.ToList();
            var challengeIndex = challenges.FindIndex(c =>
                string.Equals(c.CodeSha256, hash, StringComparison.Ordinal));
            if (challengeIndex < 0)
            {
                return ConnectivityOutcome<DeviceRecord>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "pairing code is invalid");
            }

            var challenge = challenges[challengeIndex];
            if (challenge.Consumed)
            {
                return ConnectivityOutcome<DeviceRecord>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "pairing code already used");
            }

            if (challenge.ExpiresAt <= now)
            {
                return ConnectivityOutcome<DeviceRecord>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "pairing code expired");
            }

            var approvedBy = await AuthorizeApproverAsync(loaded, approver, challenge.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            if (!approvedBy.Ok)
            {
                return ConnectivityOutcome<DeviceRecord>.Failure(
                    approvedBy.Reason ?? ConnectivityReasons.Unauthorized,
                    approvedBy.Detail ?? "pairing approval denied");
            }

            var devices = loaded.Devices.ToList();
            var deviceIndex = devices.FindIndex(d => d.Id.Value == challenge.DeviceId.Value);
            if (deviceIndex < 0)
            {
                return ConnectivityOutcome<DeviceRecord>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "pairing device is missing");
            }

            var device = devices[deviceIndex];
            if (device.Status == DeviceTrustStatus.Revoked)
            {
                return ConnectivityOutcome<DeviceRecord>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "device is revoked");
            }

            var trusted = device with
            {
                Status = DeviceTrustStatus.Trusted,
                ApprovedAt = now,
                ApprovedBy = approver.Kind,
                ApproverDeviceId = approver.TrustedDeviceId,
            };
            devices[deviceIndex] = trusted;
            challenges[challengeIndex] = challenge with { Consumed = true };

            await _store.SaveAsync(
                    loaded with
                    {
                        Devices = devices,
                        PairingChallenges = challenges,
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

    public async ValueTask<ConnectivityOutcome<IReadOnlyList<DeviceRecord>>> ListAsync(
        OperatorIdentity operatorId,
        CancellationToken cancellationToken = default)
    {
        var local = RequireLocalSelfHosted(operatorId);
        if (!local.Ok)
        {
            return ConnectivityOutcome<IReadOnlyList<DeviceRecord>>.Failure(
                local.Reason ?? ConnectivityReasons.Unauthorized,
                local.Detail ?? "self-hosted pairing uses local-operator identity");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            return ConnectivityOutcome<IReadOnlyList<DeviceRecord>>.Success(loaded.Devices);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ConnectivityOutcome> RevokeAsync(
        OperatorIdentity operatorId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var local = RequireLocalSelfHosted(operatorId);
        if (!local.Ok)
            return local;

        if (string.IsNullOrEmpty(deviceId.Value))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device id is required");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var devices = loaded.Devices.ToList();
            var index = devices.FindIndex(d => d.Id.Value == deviceId.Value);
            if (index < 0)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.Unauthorized,
                    "device is not paired");
            }

            devices[index] = devices[index] with
            {
                Status = DeviceTrustStatus.Revoked,
            };

            var capabilities = loaded.JoinCapabilities
                .Select(c => c.DeviceId.Value == deviceId.Value ? c with { Spent = true } : c)
                .ToList();

            var challenges = loaded.PairingChallenges
                .Select(c => c.DeviceId.Value == deviceId.Value ? c with { Consumed = true } : c)
                .ToList();

            await _store.SaveAsync(
                    loaded with
                    {
                        Devices = devices,
                        JoinCapabilities = capabilities,
                        PairingChallenges = challenges,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _joinCloser?.CloseJoinsForDevice(deviceId);
        }
        finally
        {
            _gate.Release();
        }

        return ConnectivityOutcome.Success();
    }

    public async ValueTask<ConnectivityOutcome<JoinCapability>> IssueJoinCapabilityAsync(
        OperatorIdentity operatorId,
        DeviceId deviceId,
        PlacementId placementId,
        JoinRole role,
        JoinNonce nonce,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        var local = RequireLocalSelfHosted(operatorId);
        if (!local.Ok)
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                local.Reason ?? ConnectivityReasons.Unauthorized,
                local.Detail ?? "self-hosted pairing uses local-operator identity");
        }

        if (string.IsNullOrEmpty(placementId.Value))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "placement id must start with plc_");
        }

        if (string.IsNullOrEmpty(deviceId.Value))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "device id is required");
        }

        if (!JoinRoleRules.IsDefined(role))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "role must be mux or client");
        }

        if (string.IsNullOrEmpty(nonce.Value))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "join nonce is required");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var device = loaded.Devices.FirstOrDefault(d => d.Id.Value == deviceId.Value);
            if (device is null || device.Status != DeviceTrustStatus.Trusted)
            {
                return ConnectivityOutcome<JoinCapability>.Failure(
                    ConnectivityReasons.JoinDenied,
                    device?.Status == DeviceTrustStatus.Revoked
                        ? "device is revoked"
                        : "device is not paired");
            }

            if (expiresAt <= now)
            {
                return ConnectivityOutcome<JoinCapability>.Failure(
                    ConnectivityReasons.JoinExpired,
                    "join capability expired");
            }

            if (expiresAt - now > JoinCapabilityLifetime.Max)
            {
                return ConnectivityOutcome<JoinCapability>.Failure(
                    ConnectivityReasons.BootstrapInvalid,
                    "capability lifetime exceeds maximum");
            }

            var issued = new IssuedJoinCapability
            {
                OperatorId = OperatorIdentity.LocalSelfHosted,
                PlacementId = placementId,
                DeviceId = deviceId,
                Role = role,
                ExpiresAt = expiresAt,
                Nonce = nonce,
                Spent = false,
            };

            var capabilities = loaded.JoinCapabilities.ToList();
            capabilities.Add(issued);
            await _store.SaveAsync(
                    loaded with { JoinCapabilities = capabilities },
                    cancellationToken)
                .ConfigureAwait(false);

            return ConnectivityOutcome<JoinCapability>.Success(new JoinCapability
            {
                OperatorId = OperatorIdentity.LocalSelfHosted,
                PlacementId = placementId,
                DeviceId = deviceId,
                Role = role,
                ExpiresAt = expiresAt,
                Nonce = nonce,
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Mint a client join capability from a local pairing key.
    /// Host admit checks trust. Pending local devices may mint.
    /// </summary>
    public async ValueTask<ConnectivityOutcome<JoinCapability>> MintClientJoinCapabilityAsync(
        PlacementId placementId,
        JoinRole role,
        DateTimeOffset expiresAt,
        DeviceId? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(placementId.Value))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "placement id must start with plc_");
        }

        if (!JoinRoleRules.IsDefined(role))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "role must be mux or client");
        }

        var now = _clock.GetUtcNow();
        if (expiresAt <= now)
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.JoinExpired,
                "join capability expired");
        }

        if (expiresAt - now > JoinCapabilityLifetime.Max)
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability lifetime exceeds maximum");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            DeviceRecord? local = null;
            foreach (var device in loaded.Devices)
            {
                if (device.Status == DeviceTrustStatus.Revoked)
                    continue;
                if (deviceId is { } required
                    && !string.Equals(device.Id.Value, required.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                var key = await _keys.LoadPrivateKeyAsync(device.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (key is { Length: > 0 })
                {
                    local = device;
                    break;
                }
            }

            if (local is null)
            {
                return ConnectivityOutcome<JoinCapability>.Failure(
                    ConnectivityReasons.JoinDenied,
                    "device is not paired");
            }

            var nonceValue = "jn" + Guid.NewGuid().ToString("N");
            if (!JoinNonce.TryParse(nonceValue, out var nonce))
            {
                return ConnectivityOutcome<JoinCapability>.Failure(
                    ConnectivityReasons.BootstrapInvalid,
                    "join nonce is required");
            }

            return ConnectivityOutcome<JoinCapability>.Success(new JoinCapability
            {
                OperatorId = OperatorIdentity.LocalSelfHosted,
                PlacementId = placementId,
                DeviceId = local.Id,
                Role = role,
                ExpiresAt = expiresAt,
                Nonce = nonce,
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ConnectivityOutcome> AdmitAsync(
        JoinBootstrap bootstrap,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var deviceId = bootstrap.Capability.DeviceId;
            var device = loaded.Devices.FirstOrDefault(d => d.Id.Value == deviceId.Value);
            if (device is null || device.Status == DeviceTrustStatus.Pending)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.JoinDenied,
                    "device is not paired");
            }

            if (device.Status == DeviceTrustStatus.Revoked)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.JoinDenied,
                    "device is revoked");
            }

            var capabilities = loaded.JoinCapabilities.ToList();
            var sameNonce = capabilities.FindIndex(c =>
                c.PlacementId == bootstrap.Capability.PlacementId
                && c.DeviceId.Value == deviceId.Value
                && c.Role == bootstrap.Capability.Role
                && string.Equals(c.Nonce.Value, bootstrap.Capability.Nonce.Value, StringComparison.Ordinal));
            if (sameNonce < 0)
                return await AdmitClientMintedAsync(
                        loaded,
                        capabilities,
                        bootstrap,
                        device,
                        utcNow,
                        cancellationToken)
                    .ConfigureAwait(false);

            var issued = capabilities[sameNonce];
            if (!issued.OperatorId.IsLocalSelfHosted
                || issued.OperatorId != bootstrap.Capability.OperatorId)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.JoinDenied,
                    "join capability operator does not match");
            }

            if (!ConnectivityTimestamp.Equal(issued.ExpiresAt, bootstrap.Capability.ExpiresAt))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.JoinDenied,
                    "join capability expiry does not match");
            }

            if (issued.Spent)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.JoinDenied,
                    "join capability already used");
            }

            if (issued.ExpiresAt - utcNow > JoinCapabilityLifetime.Max)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.BootstrapInvalid,
                    "capability lifetime exceeds maximum");
            }

            if (issued.ExpiresAt <= utcNow)
            {
                capabilities[sameNonce] = issued with { Spent = true };
                await _store.SaveAsync(
                        loaded with { JoinCapabilities = capabilities },
                        cancellationToken)
                    .ConfigureAwait(false);
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.JoinExpired,
                    "join capability expired");
            }

            var proof = JoinDeviceAuthenticator.Verify(bootstrap, device.PublicKeySpkiBase64);
            if (!proof.Ok)
            {
                return ConnectivityOutcome.Failure(
                    proof.Reason ?? ConnectivityReasons.Unauthorized,
                    proof.Detail ?? "device signature is required");
            }

            capabilities[sameNonce] = issued with { Spent = true };
            await _store.SaveAsync(
                    loaded with { JoinCapabilities = capabilities },
                    cancellationToken)
                .ConfigureAwait(false);
            return ConnectivityOutcome.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<bool> IsJoinStillValidAsync(
        JoinBootstrap bootstrap,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        EvaluateStoredJoinTrustAsync(
            bootstrap,
            utcNow,
            requireUnexpired: true,
            requireSpent: false,
            cancellationToken);

    public ValueTask<bool> IsEstablishedJoinStillValidAsync(
        JoinBootstrap bootstrap,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        EvaluateStoredJoinTrustAsync(
            bootstrap,
            utcNow,
            requireUnexpired: false,
            requireSpent: true,
            cancellationToken);

    private async ValueTask<bool> EvaluateStoredJoinTrustAsync(
        JoinBootstrap bootstrap,
        DateTimeOffset utcNow,
        bool requireUnexpired,
        bool requireSpent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var device = loaded.Devices.FirstOrDefault(d =>
                d.Id.Value == bootstrap.Capability.DeviceId.Value);
            if (device is null || device.Status != DeviceTrustStatus.Trusted)
                return false;

            var issued = loaded.JoinCapabilities.FirstOrDefault(c =>
                c.PlacementId == bootstrap.Capability.PlacementId
                && c.DeviceId.Value == bootstrap.Capability.DeviceId.Value
                && c.Role == bootstrap.Capability.Role
                && string.Equals(c.Nonce.Value, bootstrap.Capability.Nonce.Value, StringComparison.Ordinal));
            if (issued is null)
                return false;

            if (!issued.OperatorId.IsLocalSelfHosted
                || issued.OperatorId != bootstrap.Capability.OperatorId)
                return false;

            if (!ConnectivityTimestamp.Equal(issued.ExpiresAt, bootstrap.Capability.ExpiresAt))
                return false;

            if (requireSpent && !issued.Spent)
                return false;

            if (requireUnexpired && issued.ExpiresAt <= utcNow)
                return false;

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ConnectivityOutcome> AdmitClientMintedAsync(
        DevicePairingSnapshot loaded,
        List<IssuedJoinCapability> capabilities,
        JoinBootstrap bootstrap,
        DeviceRecord device,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        if (!bootstrap.Capability.OperatorId.IsLocalSelfHosted)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "join capability operator does not match");
        }

        if (bootstrap.Capability.ExpiresAt - utcNow > JoinCapabilityLifetime.Max)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability lifetime exceeds maximum");
        }

        if (bootstrap.Capability.ExpiresAt <= utcNow)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinExpired,
                "join capability expired");
        }

        var proof = JoinDeviceAuthenticator.Verify(bootstrap, device.PublicKeySpkiBase64);
        if (!proof.Ok)
        {
            return ConnectivityOutcome.Failure(
                proof.Reason ?? ConnectivityReasons.Unauthorized,
                proof.Detail ?? "device signature is required");
        }

        capabilities.Add(new IssuedJoinCapability
        {
            OperatorId = OperatorIdentity.LocalSelfHosted,
            PlacementId = bootstrap.Capability.PlacementId,
            DeviceId = bootstrap.Capability.DeviceId,
            Role = bootstrap.Capability.Role,
            ExpiresAt = bootstrap.Capability.ExpiresAt,
            Nonce = bootstrap.Capability.Nonce,
            Spent = true,
        });
        await _store.SaveAsync(
                loaded with { JoinCapabilities = capabilities },
                cancellationToken)
            .ConfigureAwait(false);
        return ConnectivityOutcome.Success();
    }

    private static ConnectivityOutcome RequireLocalSelfHosted(OperatorIdentity operatorId)
    {
        if (!operatorId.IsLocalSelfHosted)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "self-hosted pairing uses local-operator identity");
        }

        return ConnectivityOutcome.Success();
    }

    private static ValueTask<ConnectivityOutcome> AuthorizeApproverAsync(
        DevicePairingSnapshot snapshot,
        PairingApprover approver,
        DeviceId pendingDeviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (approver.Kind == PairingApproverKind.SelfHostedConsole)
            return ValueTask.FromResult(ConnectivityOutcome.Success());

        if (approver.Kind != PairingApproverKind.TrustedDevice
            || approver.TrustedDeviceId is not { } trustedId
            || string.IsNullOrEmpty(trustedId.Value))
        {
            return ValueTask.FromResult(ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "trusted device approver is required"));
        }

        if (trustedId.Value == pendingDeviceId.Value)
        {
            return ValueTask.FromResult(ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "a device cannot approve itself"));
        }

        var trusted = snapshot.Devices.FirstOrDefault(d => d.Id.Value == trustedId.Value);
        if (trusted is null || trusted.Status != DeviceTrustStatus.Trusted)
        {
            return ValueTask.FromResult(ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "approver device is not trusted"));
        }

        return ValueTask.FromResult(ConnectivityOutcome.Success());
    }

    internal static DeviceId DeviceIdFromPublicKey(ReadOnlySpan<byte> spki)
    {
        var hash = SHA256.HashData(spki);
        var id = "dev_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        if (!DeviceId.TryParse(id, out var deviceId))
            throw new InvalidOperationException("generated device id is invalid");
        return deviceId;
    }

    internal static string Fingerprint(ReadOnlySpan<byte> spki) =>
        Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();

    internal static string HashPairingCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    internal static bool TryNormalizePairingCode(string? codeOrQr, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(codeOrQr))
            return false;

        var trimmed = codeOrQr.Trim();
        if (trimmed.StartsWith(DevicePairingPaths.QrPrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[DevicePairingPaths.QrPrefix.Length..];

        if (trimmed.Length != 8)
            return false;

        var normalized = trimmed.ToUpperInvariant();
        foreach (var c in normalized)
        {
            if (!PairingAlphabet.Contains(c))
                return false;
        }

        code = normalized;
        return true;
    }

    private static bool TryResolvePairingInput(
        string? codeOrQr,
        out string code,
        out TransferablePairingOffer? transferable,
        out string? error)
    {
        code = "";
        transferable = null;
        error = "pairing code is invalid";
        if (string.IsNullOrWhiteSpace(codeOrQr))
            return false;

        var trimmed = codeOrQr.Trim();
        if (trimmed.StartsWith('{'))
        {
            DevicePairingOfferDocument? document;
            try
            {
                document = JsonSerializer.Deserialize(
                    trimmed,
                    ConnectivityJsonContext.Default.DevicePairingOfferDocument);
            }
            catch (JsonException)
            {
                return false;
            }

            if (document is null)
                return false;
            if (!string.IsNullOrWhiteSpace(document.OperatorId)
                && (!OperatorIdentity.TryParse(document.OperatorId, out var operatorId)
                    || !operatorId.IsLocalSelfHosted))
            {
                error = "self-hosted pairing uses local-operator identity";
                return false;
            }

            if (TryReadTransferable(
                    document.QrValue,
                    document.PairingCode,
                    document.DeviceId,
                    document.PublicKeyFingerprint,
                    document.PublicKeySpkiBase64,
                    out var jsonOffer,
                    out error))
            {
                transferable = jsonOffer;
                code = jsonOffer.Code;
                return true;
            }

            return TryNormalizePairingCode(document.PairingCode, out code);
        }

        if (TryReadTransferable(trimmed, null, null, null, null, out var qrOffer, out error))
        {
            transferable = qrOffer;
            code = qrOffer.Code;
            return true;
        }

        return TryNormalizePairingCode(trimmed, out code);
    }

    private static bool TryReadTransferable(
        string? qrValue,
        string? pairingCode,
        string? deviceIdValue,
        string? fingerprint,
        string? spkiBase64,
        out TransferablePairingOffer transferable,
        out string? error)
    {
        transferable = null!;
        error = "pairing code is invalid";
        if (!PairingOfferFormat.TryDecodeQr(qrValue, out var code, out var unix, out var spki, out var signature))
            return false;
        if (!TryNormalizePairingCode(code, out code))
            return false;
        if (!string.IsNullOrWhiteSpace(pairingCode)
            && (!TryNormalizePairingCode(pairingCode, out var printedCode)
                || !string.Equals(printedCode, code, StringComparison.Ordinal)))
        {
            return false;
        }

        DateTimeOffset expiresAt;
        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (!TryVerifyTransferableKey(spki, signature, code, unix, out var deviceId, out error))
            return false;

        if (!string.IsNullOrWhiteSpace(deviceIdValue)
            && (!DeviceId.TryParse(deviceIdValue, out var printedDevice)
                || printedDevice.Value != deviceId.Value))
        {
            error = "pairing device id does not match";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(fingerprint)
            && !string.Equals(fingerprint, Fingerprint(spki), StringComparison.OrdinalIgnoreCase))
        {
            error = "pairing public key fingerprint does not match";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(spkiBase64))
        {
            try
            {
                var printedSpki = Convert.FromBase64String(spkiBase64);
                if (!printedSpki.AsSpan().SequenceEqual(spki))
                {
                    error = "device public key does not match";
                    return false;
                }
            }
            catch (FormatException)
            {
                error = "device public key is invalid";
                return false;
            }
        }

        transferable = new TransferablePairingOffer(code, expiresAt, spki, deviceId);
        error = null;
        return true;
    }

    private static bool TryVerifyTransferableKey(
        byte[] spki,
        byte[] signature,
        string code,
        long expiresUnix,
        out DeviceId deviceId,
        out string? error)
    {
        deviceId = default;
        error = "pairing code is invalid";
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            var body = PairingOfferFormat.SignedBody(
                code,
                expiresUnix,
                PairingOfferFormat.ToBase64Url(spki));
            if (!ecdsa.VerifyData(
                    Encoding.UTF8.GetBytes(body),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                error = "pairing offer signature is invalid";
                return false;
            }
        }
        catch (CryptographicException)
        {
            error = "device public key is invalid";
            return false;
        }

        deviceId = DeviceIdFromPublicKey(spki);
        error = null;
        return true;
    }

    private static ConnectivityOutcome<DevicePairingSnapshot> ImportTransferable(
        DevicePairingSnapshot loaded,
        TransferablePairingOffer offer,
        DateTimeOffset now)
    {
        var spkiB64 = Convert.ToBase64String(offer.Spki);
        var devices = loaded.Devices.ToList();
        var existing = devices.FindIndex(d => d.Id.Value == offer.DeviceId.Value);
        if (existing >= 0)
        {
            var device = devices[existing];
            if (!string.Equals(device.PublicKeySpkiBase64, spkiB64, StringComparison.Ordinal))
            {
                return ConnectivityOutcome<DevicePairingSnapshot>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "device public key does not match");
            }

            if (device.Status == DeviceTrustStatus.Revoked)
            {
                return ConnectivityOutcome<DevicePairingSnapshot>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "device is revoked");
            }
        }
        else
        {
            devices.Add(new DeviceRecord
            {
                Id = offer.DeviceId,
                PublicKeySpkiBase64 = spkiB64,
                Status = DeviceTrustStatus.Pending,
                CreatedAt = now,
            });
        }

        var hash = HashPairingCode(offer.Code);
        var challenges = loaded.PairingChallenges.ToList();
        var challengeIndex = challenges.FindIndex(c =>
            string.Equals(c.CodeSha256, hash, StringComparison.Ordinal));
        if (challengeIndex >= 0)
        {
            if (challenges[challengeIndex].DeviceId.Value != offer.DeviceId.Value)
            {
                return ConnectivityOutcome<DevicePairingSnapshot>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "pairing code is invalid");
            }
        }
        else
        {
            challenges.Add(new PairingChallenge
            {
                CodeSha256 = hash,
                DeviceId = offer.DeviceId,
                ExpiresAt = offer.ExpiresAt,
                Consumed = false,
            });
        }

        return ConnectivityOutcome<DevicePairingSnapshot>.Success(
            loaded with
            {
                OperatorId = OperatorIdentity.LocalSelfHosted,
                Devices = devices,
                PairingChallenges = challenges,
            });
    }

    private sealed record TransferablePairingOffer(
        string Code,
        DateTimeOffset ExpiresAt,
        byte[] Spki,
        DeviceId DeviceId);

    private static string NewPairingCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < 8; i++)
            chars[i] = PairingAlphabet[bytes[i] % PairingAlphabet.Length];
        return new string(chars);
    }
}
