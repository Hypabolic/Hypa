namespace Hypa.Placement.Domain;

/// <summary>Stable directory failure reasons. Wire as lowercase snake strings.</summary>
public static class PlacementReasons
{
    public const string MuxIdentityInvalid = "mux_identity_invalid";
    public const string PlacementNotFound = "placement_not_found";
    public const string KindInvalid = "kind_invalid";
    public const string Unauthorized = "unauthorized";
    public const string DisplayNameInvalid = "display_name_invalid";
    public const string DirectoryInvalid = "directory_invalid";
    public const string IdentityInvalid = "identity_invalid";
    public const string WorkIdInvalid = "work_id_invalid";
    public const string ReachabilityInvalid = "reachability_invalid";
    public const string Internal = "internal";
    public const string ProviderMismatch = "placement_provider_mismatch";
    public const string ProfileInvalid = "placement_profile_invalid";
    public const string PreparationFailed = "ssh_preparation_failed";
    public const string StoreFailed = "placement_store_failed";
}

/// <summary>Expected directory result. Exceptions stay for programmer errors.</summary>
public sealed record PlacementOutcome
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }

    public static PlacementOutcome Success() => new() { Ok = true };

    public static PlacementOutcome Failure(string reason, string detail) =>
        new() { Ok = false, Reason = reason, Detail = detail };
}

public sealed record PlacementOutcome<T>
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public T? Value { get; init; }

    public static PlacementOutcome<T> Success(T value) => new() { Ok = true, Value = value };

    public static PlacementOutcome<T> Failure(string reason, string detail) =>
        new() { Ok = false, Reason = reason, Detail = detail };

    public PlacementOutcome WithoutValue() =>
        Ok ? PlacementOutcome.Success() : PlacementOutcome.Failure(Reason ?? PlacementReasons.Internal, Detail ?? "");
}
