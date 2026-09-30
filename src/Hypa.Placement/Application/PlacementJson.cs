using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

public sealed class PlacementDirectoryKindConverter : JsonStringEnumConverter<PlacementDirectoryKind>
{
    public PlacementDirectoryKindConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}

public sealed class PlacementReachabilityConverter : JsonStringEnumConverter<PlacementReachability>
{
    public PlacementReachabilityConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}

public sealed record SshPlacementProfileDto
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Target { get; init; } = "";
    public string Session { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string? Attention { get; init; }
}

public sealed record QuicPlacementProfileDto
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Target { get; init; } = "";
    public string Session { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string? CertificateSha256 { get; init; }
    public string? EnrolledDeviceId { get; init; }
}

public sealed record PlacementRecordDto
{
    public string PlacementId { get; init; } = "";
    public string OwnerId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public PlacementDirectoryKind Kind { get; init; }
    public string MuxIdentity { get; init; } = "";
    public PlacementReachability Reachability { get; init; }
    public string? ActiveWorkId { get; init; }
    public string LastSeen { get; init; } = "";
    public SshPlacementProfileDto? Ssh { get; init; }
    public QuicPlacementProfileDto? Quic { get; init; }
}

public sealed record PlacementGrantDto
{
    public string PlacementId { get; init; } = "";
    public string GranteeId { get; init; } = "";
}

public sealed record WorkAccessGrantDto
{
    public string Identity { get; init; } = "";
    public string WorkId { get; init; } = "";
}

public sealed record PlacementDirectoryDocument
{
    public int Schema { get; init; } = 1;
    public int Revision { get; init; }
    public List<PlacementRecordDto>? Placements { get; init; }
    public List<PlacementGrantDto>? PlacementGrants { get; init; }
    public List<WorkAccessGrantDto>? WorkGrants { get; init; }
}

public sealed record PlacementRowDto
{
    public string PlacementId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public PlacementDirectoryKind Kind { get; init; }
    public string MuxIdentity { get; init; } = "";
    public PlacementReachability Reachability { get; init; }
    public string? ActiveWorkId { get; init; }
    public string LastSeen { get; init; } = "";
    public SshPlacementProfileDto? Ssh { get; init; }
    public QuicPlacementProfileDto? Quic { get; init; }
}

public sealed record PlacementListDocument
{
    public bool Ok { get; init; } = true;
    public string Heading { get; init; } = PlacementDirectoryNames.OnScreenHeading;
    public List<PlacementRowDto> Placements { get; init; } = [];
}

public sealed record PlacementRegisterDocument
{
    public bool Ok { get; init; } = true;
    public string PlacementId { get; init; } = "";
    public PlacementDirectoryKind Kind { get; init; }
    public string MuxIdentity { get; init; } = "";
    public PlacementReachability Reachability { get; init; }
}

public sealed record PlacementMutationDocument
{
    public bool Ok { get; init; } = true;
}

public sealed record PlacementFailureDocument
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
}

public sealed record PlacementSshMutationDocument
{
    public bool Ok { get; init; } = true;
    public string? PlacementId { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Shared peer profile contract. Provider is a discriminator.
/// Kind stays peer. A later catalog persists this on a Placement row.
/// </summary>
public sealed record PeerProfileDto
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Target { get; init; } = "";
    public string Session { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Provider { get; init; } = PeerProviders.Ssh;
}

[JsonSerializable(typeof(SshPlacementProfileDto))]
[JsonSerializable(typeof(QuicPlacementProfileDto))]
[JsonSerializable(typeof(PlacementRecordDto))]
[JsonSerializable(typeof(PlacementGrantDto))]
[JsonSerializable(typeof(WorkAccessGrantDto))]
[JsonSerializable(typeof(PlacementDirectoryDocument))]
[JsonSerializable(typeof(PlacementRowDto))]
[JsonSerializable(typeof(PlacementListDocument))]
[JsonSerializable(typeof(PlacementRegisterDocument))]
[JsonSerializable(typeof(PlacementMutationDocument))]
[JsonSerializable(typeof(PlacementFailureDocument))]
[JsonSerializable(typeof(PlacementSshMutationDocument))]
[JsonSerializable(typeof(PeerProfileDto))]
[JsonSerializable(typeof(List<PlacementRecordDto>))]
[JsonSerializable(typeof(List<PlacementGrantDto>))]
[JsonSerializable(typeof(List<WorkAccessGrantDto>))]
[JsonSerializable(typeof(List<PlacementRowDto>))]
[JsonSerializable(typeof(PlacementDirectoryKind))]
[JsonSerializable(typeof(PlacementReachability))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    Converters = [typeof(PlacementDirectoryKindConverter), typeof(PlacementReachabilityConverter)])]
public partial class PlacementJsonContext : JsonSerializerContext;
