using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Optional transport checkpoint. A failed checkpoint is a relay loss.
/// Unknown dest confirmation is not success.
/// </summary>
public interface IHandoffTransport
{
    ValueTask<ContinuityOutcome> NotifyStageAsync(
        HandoffState stage,
        string attemptId,
        CancellationToken cancellationToken = default);
}

/// <summary>Dest authority seen by source after a relay loss.</summary>
public sealed record HandoffDestAuthority
{
    public required WorkId WorkId { get; init; }
    public required long SourceGeneration { get; init; }
    public required string DestPlacementId { get; init; }
    public required string DestMuxEndpoint { get; init; }
    public required string AttemptId { get; init; }
    public string? WorkPackSha256 { get; init; }
    public ResumeEvidence? CallerResumeEvidence { get; init; }
}

/// <summary>
/// Handoff relay-failure matrix. Mirror of Connectivity reasons.
/// Spec §4.2 and §5.1: probe before flip. Unknown dest state is not ok:true.
/// </summary>
public static class HandoffRelayFailure
{
    public const string SplitBrainWindowDetail =
        "Dest is active and source is still active. This is the split-brain window. " +
        "Source fences when it next sees dest authority. This result is not success.";

    public static string NewAttemptId() => "att_" + Guid.NewGuid().ToString("N");

    public static bool IsRelayReason(string? reason) =>
        reason is ContinuityReasons.JoinDenied
            or ContinuityReasons.JoinExpired
            or ContinuityReasons.PeerUnavailable
            or ContinuityReasons.StreamReset
            or ContinuityReasons.TransferIncomplete
            or ContinuityReasons.UnknownCommit;

    public static string ClassifyReason(HandoffState stage, string? transportReason)
    {
        if (transportReason is ContinuityReasons.UnknownCommit or ContinuityReasons.TransferIncomplete)
            return transportReason;
        if (transportReason is ContinuityReasons.JoinDenied or ContinuityReasons.JoinExpired
            && stage is not HandoffState.Probing and not HandoffState.Fencing)
        {
            return transportReason;
        }

        return stage is HandoffState.Probing or HandoffState.Fencing
            ? ContinuityReasons.UnknownCommit
            : ContinuityReasons.TransferIncomplete;
    }

    public static string StageWire(HandoffState stage) =>
        stage switch
        {
            HandoffState.Quiescing => "quiescing",
            HandoffState.Packing => "packing",
            HandoffState.Pulling => "pulling",
            HandoffState.Applying => "applying",
            HandoffState.Starting => "starting",
            HandoffState.Probing => "probing",
            HandoffState.Fencing => "fencing",
            HandoffState.Done => "done",
            HandoffState.Failed => "failed",
            _ => "failed",
        };

    public static string DestConfirmation(HandoffState stage) =>
        stage is HandoffState.Probing or HandoffState.Fencing
            ? "unknown"
            : "unconfirmed";

    public static bool IsRetryable(string reason, HandoffState stage)
    {
        if (reason is ContinuityReasons.UnknownCommit or ContinuityReasons.JoinDenied)
            return false;
        if (stage is HandoffState.Probing or HandoffState.Fencing or HandoffState.Done)
            return reason is ContinuityReasons.PeerUnavailable
                or ContinuityReasons.StreamReset
                or ContinuityReasons.JoinExpired;
        return reason is ContinuityReasons.TransferIncomplete
            or ContinuityReasons.PeerUnavailable
            or ContinuityReasons.StreamReset
            or ContinuityReasons.JoinExpired;
    }
}
