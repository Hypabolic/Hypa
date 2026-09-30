namespace Hypa.AgentRuntime.Domain;

public enum PlacementAuthorityRole
{
    Occupant,
    Parent,
    HumanLease,
}

public sealed record PlacementAuthorityError
{
    public required string Code { get; init; }
    public required string Message { get; init; }

    public static PlacementAuthorityError ForgedIdentity { get; } =
        new() { Code = "forged_identity", Message = "pane id is not a credential" };

    public static PlacementAuthorityError ForeignCredential { get; } =
        new() { Code = "foreign_credential", Message = "credential does not match the pane" };

    public static PlacementAuthorityError StatusCredential { get; } =
        new() { Code = "status_credential", Message = "status source does not grant placement" };

    public static PlacementAuthorityError MissingSequence { get; } =
        new() { Code = "missing_sequence", Message = "seq is required for this credential" };

    public static PlacementAuthorityError StaleSequence { get; } =
        new() { Code = "stale_sequence", Message = "seq is stale" };

    public static PlacementAuthorityError MissingAttachClient { get; } =
        new() { Code = "missing_attach_client", Message = "overlay show requires attach_client_id" };

    public static PlacementAuthorityError OverlayBusy { get; } =
        new() { Code = "overlay_busy", Message = "modal surface is busy" };

    public static PlacementAuthorityError UnprovenParent { get; } =
        new() { Code = "unproven_parent", Message = "parent_pane_id requires occupant or lease proof" };

    public static PlacementAuthorityError NotFound { get; } =
        new() { Code = "not_found", Message = "pane is not found" };
}

public sealed record PlacementParentProof
{
    public string? ClaimedParentPaneId { get; init; }
    public string? OccupantToken { get; init; }
    public string? LeaseId { get; init; }
    public string? HolderId { get; init; }
    public string? StatusSource { get; init; }
}

public sealed record PlacementAuthorityRequest
{
    public required PaneId PaneId { get; init; }
    public string? OccupantToken { get; init; }
    public string? ParentCapability { get; init; }
    public string? LeaseId { get; init; }
    public string? HolderId { get; init; }
    public string? StatusSource { get; init; }
    public long? Sequence { get; init; }
    public string Mode { get; init; } = "tiled";
    public string? AttachClientId { get; init; }
}

public sealed record PlacementAuthorityGrant
{
    public required PlacementAuthorityRole Role { get; init; }
    public required PaneId PaneId { get; init; }
    public required string CredentialId { get; init; }
    public required bool SequenceRequired { get; init; }
}

public sealed record PlacementApplyResult<T, E>
{
    public required bool Changed { get; init; }
    public bool Stale { get; init; }
    public T? Value { get; init; }
    public E? MutationError { get; init; }
    public PlacementAuthorityGrant? Grant { get; init; }
    public long? Sequence { get; init; }
}
