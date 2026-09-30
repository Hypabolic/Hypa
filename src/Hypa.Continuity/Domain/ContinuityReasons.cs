namespace Hypa.Continuity.Domain;

/// <summary>Stable fail-closed reasons (C-03). Wire as lowercase snake strings.</summary>
public static class ContinuityReasons
{
    public const string NotQuiescent = "not_quiescent";
    public const string WorkspaceUnsupported = "workspace_unsupported";
    public const string WorkspaceApplyFailed = "workspace_apply_failed";
    public const string WorkspaceMismatch = "workspace_mismatch";
    public const string PackInvalid = "pack_invalid";
    public const string PackTooLarge = "pack_too_large";
    public const string StalePack = "stale_pack";
    public const string HarnessVersionUnreadable = "harness_version_unreadable";
    public const string HarnessVersionIncompatible = "harness_version_incompatible";
    public const string StoreRestoreFailed = "store_restore_failed";
    public const string StartFailed = "start_failed";
    public const string ConversationMismatch = "conversation_mismatch";
    public const string SourcePathRequired = "source_path_required";
    public const string ResumeUnproven = "resume_unproven";
    public const string HarnessUncertified = "harness_uncertified";
    public const string SourceStillActive = "source_still_active";
    public const string Fenced = "fenced";
    public const string Timeout = "timeout";
    public const string Internal = "internal";
    public const string JoinDenied = "join_denied";
    public const string JoinExpired = "join_expired";
    public const string PeerUnavailable = "peer_unavailable";
    public const string StreamReset = "stream_reset";
    public const string TransferIncomplete = "transfer_incomplete";
    public const string UnknownCommit = "unknown_commit";
    public const string WorkNotAdopted = "work_not_adopted";
}

/// <summary>RPC/CLI fail-closed payload. Never include relayed:true.</summary>
public sealed record ContinuityFail
{
    public required string Reason { get; init; }
    public required string Detail { get; init; }

    public static ContinuityFail Of(string reason, string detail) =>
        new() { Reason = reason, Detail = detail };
}

/// <summary>Ok:true only when section 0 success holds.</summary>
public sealed record ContinuityOutcome
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }

    public static ContinuityOutcome Success() => new() { Ok = true };

    public static ContinuityOutcome Failure(string reason, string detail) =>
        new() { Ok = false, Reason = reason, Detail = detail };

    public static ContinuityOutcome Failure(ContinuityFail fail) =>
        Failure(fail.Reason, fail.Detail);
}
