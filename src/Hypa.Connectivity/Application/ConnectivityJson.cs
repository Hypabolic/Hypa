using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

public sealed class JoinRoleConverter : JsonStringEnumConverter<JoinRole>
{
    public JoinRoleConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}

public sealed class StreamClassConverter : JsonStringEnumConverter<StreamClass>
{
    public StreamClassConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}

public sealed class RendezvousDeploymentConverter : JsonStringEnumConverter<RendezvousDeployment>
{
    public RendezvousDeploymentConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}

public sealed class DeviceTrustStatusConverter : JsonStringEnumConverter<DeviceTrustStatus>
{
    public DeviceTrustStatusConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}

public sealed class PairingApproverKindConverter : JsonStringEnumConverter<PairingApproverKind>
{
    public PairingApproverKindConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}

public sealed record JoinCapabilityDocument
{
    public string OperatorId { get; init; } = "";
    public string PlacementId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public JoinRole? Role { get; init; }
    public string ExpiresAt { get; init; } = "";
    public string Nonce { get; init; } = "";
}

public sealed record JoinBootstrapDocument
{
    public string ProtocolVersion { get; init; } = "";
    public JoinRole? Role { get; init; }
    public string PlacementId { get; init; } = "";
    public string JoinNonce { get; init; } = "";
    public StreamClass? StreamClass { get; init; }
    public JoinCapabilityDocument? Capability { get; init; }
    public string Audience { get; init; } = "";
    public string TenantScope { get; init; } = "";
    public string EphPublicKey { get; init; } = "";
    public string EphPublicMac { get; init; } = "";
    public string DeviceSignature { get; init; } = "";
}

public sealed record ConnectivityFailureDocument
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
}

public sealed record JoinResultDocument
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public string? Stage { get; init; }
    public string? AttemptId { get; init; }
    public bool? Retryable { get; init; }
    public string? PlacementId { get; init; }
    public StreamClass? StreamClass { get; init; }
    public JoinRole? Role { get; init; }
    public string? PeerEphPublicKey { get; init; }
    public string? PeerEphPublicMac { get; init; }
}

public sealed record RelayListenDocument
{
    public bool Ok { get; init; }
    public string Listen { get; init; } = "";
    public string Audience { get; init; } = "";
    public string TenantScope { get; init; } = "";
}

public sealed record DeviceRecordDocument
{
    public string DeviceId { get; init; } = "";
    public string PublicKeySpkiBase64 { get; init; } = "";
    public DeviceTrustStatus Status { get; init; }
    public string CreatedAt { get; init; } = "";
    public string? ApprovedAt { get; init; }
    public PairingApproverKind? ApprovedBy { get; init; }
    public string? ApproverDeviceId { get; init; }
}

public sealed record PairingChallengeDocument
{
    public string CodeSha256 { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string ExpiresAt { get; init; } = "";
    public bool Consumed { get; init; }
}

public sealed record IssuedJoinCapabilityDocument
{
    public string OperatorId { get; init; } = "";
    public string PlacementId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public JoinRole Role { get; init; }
    public string ExpiresAt { get; init; } = "";
    public string Nonce { get; init; } = "";
    public bool Spent { get; init; }
}

public sealed record HostInviteRecordDocument
{
    public string InviteId { get; init; } = "";
    public string SecretSha256 { get; init; } = "";
    public string ExpiresAt { get; init; } = "";
    public bool Consumed { get; init; }
    public string? RedeemedBy { get; init; }
    public int FailedAttempts { get; init; }
}

public sealed record DevicePairingDocument
{
    public int Schema { get; init; } = 1;
    public string OperatorId { get; init; } = "";
    public List<DeviceRecordDocument>? Devices { get; init; }
    public List<PairingChallengeDocument>? PairingChallenges { get; init; }
    public List<IssuedJoinCapabilityDocument>? JoinCapabilities { get; init; }
    public List<HostInviteRecordDocument>? HostInvites { get; init; }
}

public sealed record HostInviteDocument
{
    public string Type { get; init; } = "";
    public int Schema { get; init; }
    public string InviteId { get; init; } = "";
    public string Host { get; init; } = "";
    public List<string>? Hosts { get; init; }
    public int Port { get; init; }
    public string CertificateSha256 { get; init; } = "";
    public string Secret { get; init; } = "";
    public string ExpiresAt { get; init; } = "";
    public string? Session { get; init; }
    public string? Label { get; init; }
}

public sealed record HostInviteRedeemDocument
{
    public string Type { get; init; } = "";
    public int Schema { get; init; }
    public string InviteId { get; init; } = "";
    public string Secret { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string DevicePublicKey { get; init; } = "";
    public long IssuedAt { get; init; }
    public string Signature { get; init; } = "";
}

public sealed record HostInviteRevokeDocument
{
    public string Type { get; init; } = "";
    public int Schema { get; init; }
    public string DeviceId { get; init; } = "";
    public string DevicePublicKey { get; init; } = "";
    public long IssuedAt { get; init; }
    public string Signature { get; init; } = "";
}

public sealed record DevicePairingOfferDocument
{
    public bool Ok { get; init; } = true;
    public string OperatorId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string PairingCode { get; init; } = "";
    public string QrValue { get; init; } = "";
    public string ExpiresAt { get; init; } = "";
    public string PublicKeyFingerprint { get; init; } = "";
    public string PublicKeySpkiBase64 { get; init; } = "";
    public string PairingStore { get; init; } = "";
    public string KeyStore { get; init; } = "";
    public bool PlatformKeyStore { get; init; }
}

public sealed record DeviceListRowDocument
{
    public string DeviceId { get; init; } = "";
    public DeviceTrustStatus Status { get; init; }
    public string CreatedAt { get; init; } = "";
    public string? ApprovedAt { get; init; }
    public PairingApproverKind? ApprovedBy { get; init; }
}

public sealed record DeviceListDocument
{
    public bool Ok { get; init; } = true;
    public string OperatorId { get; init; } = "";
    public List<DeviceListRowDocument> Devices { get; init; } = [];
}

public sealed record DeviceMutationDocument
{
    public bool Ok { get; init; } = true;
    public string DeviceId { get; init; } = "";
    public DeviceTrustStatus Status { get; init; }
}

public sealed record ChannelCursorDocument
{
    public uint ChannelId { get; init; }
    public ulong LastReceivedSequence { get; init; }
    public bool Unavailable { get; init; }
    public string AttemptId { get; init; } = "";
    public string PlacementId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public JoinRole? Role { get; init; }
}

public sealed record AttachReconnectRequestDocument
{
    public string Type { get; init; } = "";
    public string PreviousAttemptId { get; init; } = "";
    public string AttemptId { get; init; } = "";
    public JoinCapabilityDocument? Capability { get; init; }
    public ChannelCursorDocument[] LastReceived { get; init; } = [];
}

public sealed record AttachReconnectOfferDocument
{
    public string Type { get; init; } = "";
    public string AttemptId { get; init; } = "";
    public bool SnapshotPaint { get; init; }
    public bool InputLeaseRequired { get; init; }
    public int ReplayFrameCount { get; init; }
}

public sealed record ConnectivityAcceptListenDocument
{
    public bool Ok { get; init; }

    public string Bind { get; init; } = "";

    public string? QuicBind { get; init; }

    public int Port { get; init; }

    public bool Tls { get; init; }

    public bool QuicListening { get; init; }

    public string? QuicDetail { get; init; }

    public string CertificateSha256 { get; init; } = "";

    public string? Invite { get; init; }

    /// <summary>Pairing store the listener admits joins from. Invites must be minted here.</summary>
    public string? PairingStore { get; init; }
}

public sealed record ConnectivityAcceptPairedDocument
{
    public const string EventName = "paired";

    public bool Ok { get; init; }

    public string Event { get; init; } = EventName;

    public string? DeviceId { get; init; }

    public static bool TryRead(string? line, out ConnectivityAcceptPairedDocument document)
    {
        document = new ConnectivityAcceptPairedDocument();
        if (string.IsNullOrWhiteSpace(line))
            return false;

        ConnectivityAcceptPairedDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(
                line,
                ConnectivityJsonContext.Default.ConnectivityAcceptPairedDocument);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null
            || !parsed.Ok
            || !string.Equals(parsed.Event, EventName, StringComparison.Ordinal))
        {
            return false;
        }

        document = parsed;
        return true;
    }
}

public sealed record QuicTransportCapabilityDocument
{
    public bool Ok { get; init; }

    public string RuntimeIdentifier { get; init; } = "";

    public bool IsSupported { get; init; }

    public bool NativeLibraryFound { get; init; }

    public string NativeLibraryLocation { get; init; } = MsQuicNativeLocations.Absent;

    public string QuicProvider { get; init; } = BytePathProviders.Quic;

    public string FallbackProvider { get; init; } = BytePathProviders.Tcp;

    public bool ZeroRttEnabled { get; init; }

    public string? Reason { get; init; }

    public string? Detail { get; init; }
}

[JsonSerializable(typeof(JoinCapabilityDocument))]
[JsonSerializable(typeof(JoinBootstrapDocument))]
[JsonSerializable(typeof(ConnectivityFailureDocument))]
[JsonSerializable(typeof(JoinResultDocument))]
[JsonSerializable(typeof(RelayListenDocument))]
[JsonSerializable(typeof(DeviceRecordDocument))]
[JsonSerializable(typeof(PairingChallengeDocument))]
[JsonSerializable(typeof(IssuedJoinCapabilityDocument))]
[JsonSerializable(typeof(HostInviteRecordDocument))]
[JsonSerializable(typeof(DevicePairingDocument))]
[JsonSerializable(typeof(HostInviteDocument))]
[JsonSerializable(typeof(HostInviteRedeemDocument))]
[JsonSerializable(typeof(HostInviteRevokeDocument))]
[JsonSerializable(typeof(DevicePairingOfferDocument))]
[JsonSerializable(typeof(DeviceListRowDocument))]
[JsonSerializable(typeof(DeviceListDocument))]
[JsonSerializable(typeof(DeviceMutationDocument))]
[JsonSerializable(typeof(RelayFailureDocument))]
[JsonSerializable(typeof(AttachDropDocument))]
[JsonSerializable(typeof(ChannelCursorDocument))]
[JsonSerializable(typeof(AttachReconnectRequestDocument))]
[JsonSerializable(typeof(AttachReconnectOfferDocument))]
[JsonSerializable(typeof(QuicTransportCapabilityDocument))]
[JsonSerializable(typeof(ConnectivityAcceptListenDocument))]
[JsonSerializable(typeof(ConnectivityAcceptPairedDocument))]
[JsonSerializable(typeof(JoinRole))]
[JsonSerializable(typeof(StreamClass))]
[JsonSerializable(typeof(RendezvousDeployment))]
[JsonSerializable(typeof(DeviceTrustStatus))]
[JsonSerializable(typeof(PairingApproverKind))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    Converters = [
        typeof(JoinRoleConverter),
        typeof(StreamClassConverter),
        typeof(RendezvousDeploymentConverter),
        typeof(DeviceTrustStatusConverter),
        typeof(PairingApproverKindConverter)])]
public partial class ConnectivityJsonContext : JsonSerializerContext;
