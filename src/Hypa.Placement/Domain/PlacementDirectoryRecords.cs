using Hypa.Continuity.Domain;

namespace Hypa.Placement.Domain;

/// <summary>
/// Directory kind. Values are local, peer, and cube. Transport is not a kind.
/// </summary>
public enum PlacementDirectoryKind
{
    Local = 0,
    Peer = 1,
    Cube = 2,
}

/// <summary>Reachability status. Not the identity of the row.</summary>
public enum PlacementReachability
{
    Local = 0,
    Reachable = 1,
    Unreachable = 2,
    Asleep = 3,
}

/// <summary>
/// Reachability is a stored status. Local status is only valid on kind local.
/// </summary>
public static class PlacementReachabilityRules
{
    public static bool IsDefined(PlacementReachability value) =>
        value is PlacementReachability.Local
            or PlacementReachability.Reachable
            or PlacementReachability.Unreachable
            or PlacementReachability.Asleep;

    public static bool IsAllowedForKind(
        PlacementDirectoryKind kind,
        PlacementReachability reachability)
    {
        if (!IsDefined(reachability))
            return false;
        if (reachability == PlacementReachability.Local)
            return kind == PlacementDirectoryKind.Local;
        return true;
    }
}

public static class PlacementDirectoryNames
{
    public const string OnScreenHeading = "Cubes";
}

/// <summary>Stored Placement row. Active Work id is stored; list may hide it.</summary>
public sealed record PlacementRecord
{
    public required PlacementId Id { get; init; }
    public required DirectoryIdentity Owner { get; init; }
    public required string DisplayName { get; init; }
    public required PlacementDirectoryKind Kind { get; init; }
    public required MuxIdentity MuxIdentity { get; init; }
    public required PlacementReachability Reachability { get; init; }
    public string? ActiveWorkId { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
    public SshPlacementProfile? Ssh { get; init; }
    public QuicPlacementProfile? Quic { get; init; }
}

/// <summary>Authorized list row. Work id is absent without Work access.</summary>
public sealed record PlacementRow
{
    public required PlacementId Id { get; init; }
    public required string DisplayName { get; init; }
    public required PlacementDirectoryKind Kind { get; init; }
    public required MuxIdentity MuxIdentity { get; init; }
    public required PlacementReachability Reachability { get; init; }
    public string? ActiveWorkId { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
    public SshPlacementProfile? Ssh { get; init; }
    public QuicPlacementProfile? Quic { get; init; }
}

public sealed record PlacementAccessGrant
{
    public required PlacementId PlacementId { get; init; }
    public required DirectoryIdentity Grantee { get; init; }
}

public sealed record WorkAccessGrant
{
    public required DirectoryIdentity Identity { get; init; }
    public required string WorkId { get; init; }
}

public sealed record PlacementRegistration
{
    public required DirectoryIdentity Owner { get; init; }
    public required string DisplayName { get; init; }
    public required PlacementDirectoryKind Kind { get; init; }
    public required MuxIdentity MuxIdentity { get; init; }
    public PlacementId? Id { get; init; }
}

/// <summary>
/// Attach target for one Work. Mux identity is the C-49 opaque value.
/// </summary>
public sealed record WorkAttachTarget
{
    public required WorkId WorkId { get; init; }
    public required PlacementId PlacementId { get; init; }
    public required MuxIdentity MuxIdentity { get; init; }
    public required long Generation { get; init; }
    public required string MuxEndpoint { get; init; }
}
