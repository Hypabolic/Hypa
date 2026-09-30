namespace Hypa.Connectivity.Domain;

/// <summary>Device trust status. Sign-in is not this value.</summary>
public enum DeviceTrustStatus
{
    Pending = 0,
    Trusted = 1,
    Revoked = 2,
}

/// <summary>
/// Who may approve a pairing request. Team RBAC is out of scope.
/// </summary>
public enum PairingApproverKind
{
    SelfHostedConsole = 0,
    TrustedDevice = 1,
    InviteRedeem = 2,
}

/// <summary>Out-of-band pairing code lifetime.</summary>
public static class PairingCodeLifetime
{
    public const int MaxSeconds = 600;

    public static TimeSpan Max { get; } = TimeSpan.FromSeconds(MaxSeconds);
}

/// <summary>
/// Named pairing and key store paths. These are not Placement directory paths.
/// </summary>
public static class DevicePairingPaths
{
    public const string StateSegment = "connectivity";
    public const string StoreFileName = "pairing.json";
    public const string KeyDirectoryName = "device-keys";
    public const string OperatorHomeVariable = "HYPA_OPERATOR_HOME";
    public const string PairingStoreVariable = "HYPA_PAIRING_STORE";
    public const string PlatformApplicationName = "Hypa";
    public const string QrPrefix = "hypa-pair:";
    public const string InvitePrefix = "hypa-invite:";
}

/// <summary>One-time host invite. The store keeps the secret hash only.</summary>
public sealed record HostInviteRecord
{
    public required InviteId Id { get; init; }
    public required string SecretSha256 { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required bool Consumed { get; init; }
    public DeviceId? RedeemedBy { get; init; }
    public int FailedAttempts { get; init; }
}

/// <summary>Host-issued invite shown once. Do not persist the secret.</summary>
public sealed record HostInvite
{
    public required InviteId InviteId { get; init; }
    public required string Secret { get; init; }

    /// <summary>First advertised address. Old invites have only this field.</summary>
    public required string Host { get; init; }

    /// <summary>Every address the client dials, in order. <see cref="Host"/> is the first.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    public required int Port { get; init; }
    public required string CertificateSha256 { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public string? Session { get; init; }
    public string? Label { get; init; }
    public required string TransferValue { get; init; }
}

/// <summary>Fields the host needs to print a share invite.</summary>
public sealed record HostInviteIssueRequest
{
    public required string Host { get; init; }

    /// <summary>When set, these addresses are the invite reach. <see cref="Host"/> is not added.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    public required int Port { get; init; }
    public required string CertificateSha256 { get; init; }
    public string? Session { get; init; }
    public string? Label { get; init; }
}

/// <summary>Redeem result. Address is the advertised host that answered.</summary>
public sealed record HostInviteRedeemResult
{
    public required DeviceRecord Device { get; init; }
    public required string Address { get; init; }
}

/// <summary>First-line redeem document. This is not a join bootstrap.</summary>
public sealed record HostInviteRedeemRequest
{
    public required InviteId InviteId { get; init; }
    public required string Secret { get; init; }
    public required DeviceId DeviceId { get; init; }
    public required string DevicePublicKeySpkiBase64 { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required string Signature { get; init; }
}

/// <summary>First-line revoke document. This is not a join bootstrap.</summary>
public sealed record HostInviteRevokeRequest
{
    public required DeviceId DeviceId { get; init; }
    public required string DevicePublicKeySpkiBase64 { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required string Signature { get; init; }
}

/// <summary>Reach fields the enrolled cube uses to revoke on the host.</summary>
public sealed record HostInviteRevokeReach
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string CertificateSha256 { get; init; }
}

/// <summary>Host invite attempt limits.</summary>
public static class HostInviteLimits
{
    public const int SecretBytes = 16;
    public const int MaxFailedAttempts = 8;
}

/// <summary>Paired or pending device. The private key is not this record.</summary>
public sealed record DeviceRecord
{
    public required DeviceId Id { get; init; }
    public required string PublicKeySpkiBase64 { get; init; }
    public required DeviceTrustStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public PairingApproverKind? ApprovedBy { get; init; }
    public DeviceId? ApproverDeviceId { get; init; }
}

/// <summary>One-time pairing challenge. The store keeps the code hash only.</summary>
public sealed record PairingChallenge
{
    public required string CodeSha256 { get; init; }
    public required DeviceId DeviceId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required bool Consumed { get; init; }
}

/// <summary>
/// Issued join capability. Single-use. Bound to operator, placement, device, role, expiry, and nonce.
/// </summary>
public sealed record IssuedJoinCapability
{
    public required OperatorIdentity OperatorId { get; init; }
    public required PlacementId PlacementId { get; init; }
    public required DeviceId DeviceId { get; init; }
    public required JoinRole Role { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required JoinNonce Nonce { get; init; }
    public required bool Spent { get; init; }
}

/// <summary>Local operator pairing snapshot. One tenant. No team policy.</summary>
public sealed record DevicePairingSnapshot
{
    public OperatorIdentity OperatorId { get; init; } = OperatorIdentity.LocalSelfHosted;
    public IReadOnlyList<DeviceRecord> Devices { get; init; } = [];
    public IReadOnlyList<PairingChallenge> PairingChallenges { get; init; } = [];
    public IReadOnlyList<IssuedJoinCapability> JoinCapabilities { get; init; } = [];
    public IReadOnlyList<HostInviteRecord> HostInvites { get; init; } = [];
}

/// <summary>Approver of a pending device. Display name is not this value.</summary>
public sealed record PairingApprover
{
    public required PairingApproverKind Kind { get; init; }
    public DeviceId? TrustedDeviceId { get; init; }

    public static PairingApprover SelfHostedConsole { get; } = new()
    {
        Kind = PairingApproverKind.SelfHostedConsole,
    };

    public static PairingApprover TrustedDevice(DeviceId deviceId) =>
        new()
        {
            Kind = PairingApproverKind.TrustedDevice,
            TrustedDeviceId = deviceId,
        };

    public static PairingApprover InviteRedeem { get; } = new()
    {
        Kind = PairingApproverKind.InviteRedeem,
    };
}

/// <summary>Out-of-band pairing offer. Print once. Do not persist the code.</summary>
public sealed record PairingOffer
{
    public required DeviceId DeviceId { get; init; }
    public required OperatorIdentity OperatorId { get; init; }
    public required string PairingCode { get; init; }
    public required string QrValue { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string PublicKeyFingerprint { get; init; }
    public required string PublicKeySpkiBase64 { get; init; }
}

public static class DeviceTrustStatusRules
{
    public static bool IsDefined(DeviceTrustStatus value) =>
        value is DeviceTrustStatus.Pending or DeviceTrustStatus.Trusted or DeviceTrustStatus.Revoked;

    public static bool TryParse(string? value, out DeviceTrustStatus status)
    {
        status = default;
        if (string.Equals(value, "pending", StringComparison.OrdinalIgnoreCase))
        {
            status = DeviceTrustStatus.Pending;
            return true;
        }

        if (string.Equals(value, "trusted", StringComparison.OrdinalIgnoreCase))
        {
            status = DeviceTrustStatus.Trusted;
            return true;
        }

        if (string.Equals(value, "revoked", StringComparison.OrdinalIgnoreCase))
        {
            status = DeviceTrustStatus.Revoked;
            return true;
        }

        return false;
    }
}

public static class PairingApproverKindRules
{
    public static bool IsDefined(PairingApproverKind value) =>
        value is PairingApproverKind.SelfHostedConsole
            or PairingApproverKind.TrustedDevice
            or PairingApproverKind.InviteRedeem;

    public static bool TryParse(string? value, out PairingApproverKind kind)
    {
        kind = default;
        if (string.Equals(value, "self_hosted_console", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "self-hosted-console", StringComparison.OrdinalIgnoreCase))
        {
            kind = PairingApproverKind.SelfHostedConsole;
            return true;
        }

        if (string.Equals(value, "trusted_device", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "trusted-device", StringComparison.OrdinalIgnoreCase))
        {
            kind = PairingApproverKind.TrustedDevice;
            return true;
        }

        if (string.Equals(value, "invite_redeem", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "invite-redeem", StringComparison.OrdinalIgnoreCase))
        {
            kind = PairingApproverKind.InviteRedeem;
            return true;
        }

        return false;
    }
}
