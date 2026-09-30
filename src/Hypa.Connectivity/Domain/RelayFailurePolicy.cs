namespace Hypa.Connectivity.Domain;

/// <summary>
/// Relay-failure matrix for join, attach, and handoff.
/// Unknown dest confirmation is never <c>ok: true</c>.
/// </summary>
public static class RelayFailurePolicy
{
    /// <summary>
    /// Split-brain window. Dest is already active. Source is still active.
    /// Source fences when it next sees dest authority. This window is not success.
    /// </summary>
    public const string SplitBrainWindowDetail =
        "Dest is active and source is still active. This is the split-brain window. " +
        "Source fences when it next sees dest authority. This result is not success.";

    public static RelayFailure ForJoinLoss(string attemptId, string? reason = null)
    {
        var resolved = string.IsNullOrWhiteSpace(reason)
            ? ConnectivityReasons.PeerUnavailable
            : reason;
        return Fail(
            resolved,
            "relay closed before join confirmation",
            RelayFailureStageWire.Join,
            attemptId,
            retryable: IsRetryable(resolved, destConfirmation: DestConfirmationWire.Unconfirmed),
            sourceStatus: null,
            destConfirmation: DestConfirmationWire.Unconfirmed);
    }

    public static RelayFailure ForHandoffLoss(string stage, string attemptId, string? reason = null)
    {
        var dest = DestConfirmationFor(stage);
        var resolved = ClassifyHandoffReason(stage, reason);
        var source = SourceStatusWire.Active;
        return Fail(
            resolved,
            dest == DestConfirmationWire.Unknown
                ? "relay closed before dest confirmation"
                : "relay closed before dest received the transfer",
            stage,
            attemptId,
            retryable: IsRetryable(resolved, dest),
            sourceStatus: source,
            destConfirmation: dest);
    }

    public static RelayFailure ForSplitBrain(string attemptId, string stage)
    {
        return Fail(
            ConnectivityReasons.UnknownCommit,
            SplitBrainWindowDetail,
            stage,
            attemptId,
            retryable: false,
            sourceStatus: SourceStatusWire.Active,
            destConfirmation: DestConfirmationWire.SplitBrain);
    }

    public static RelayFailure AfterSourceSeesDestAuthority(string attemptId, string stage)
    {
        return Fail(
            ConnectivityReasons.UnknownCommit,
            "Source fenced after it saw dest authority. The failed attempt is not success.",
            stage,
            attemptId,
            retryable: false,
            sourceStatus: SourceStatusWire.Dead,
            destConfirmation: DestConfirmationWire.Confirmed);
    }

    /// <summary>
    /// C-53 reconnect sequences are not dest confirmation and not handoff success.
    /// </summary>
    public static RelayFailure ReconnectSequenceIsNotCommit(string attemptId)
    {
        return Fail(
            ConnectivityReasons.UnknownCommit,
            "A reconnect sequence is not dest confirmation.",
            RelayFailureStageWire.Attach,
            attemptId,
            retryable: false,
            sourceStatus: SourceStatusWire.Active,
            destConfirmation: DestConfirmationWire.Unknown);
    }

    public static string ClassifyHandoffReason(string stage, string? transportReason)
    {
        if (string.Equals(transportReason, ConnectivityReasons.UnknownCommit, StringComparison.Ordinal)
            || string.Equals(transportReason, ConnectivityReasons.TransferIncomplete, StringComparison.Ordinal))
        {
            return transportReason!;
        }

        var destUnknown = stage is RelayFailureStageWire.Probing or RelayFailureStageWire.Fencing;
        if (!destUnknown
            && (string.Equals(transportReason, ConnectivityReasons.JoinDenied, StringComparison.Ordinal)
                || string.Equals(transportReason, ConnectivityReasons.JoinExpired, StringComparison.Ordinal)))
        {
            return transportReason!;
        }

        // Unknown dest confirmation is not retryable peer loss.
        return destUnknown
            ? ConnectivityReasons.UnknownCommit
            : ConnectivityReasons.TransferIncomplete;
    }

    public static string DestConfirmationFor(string stage) =>
        stage is RelayFailureStageWire.Probing or RelayFailureStageWire.Fencing
            ? DestConfirmationWire.Unknown
            : DestConfirmationWire.Unconfirmed;

    public static bool IsRetryable(string reason, string destConfirmation)
    {
        if (string.Equals(reason, ConnectivityReasons.UnknownCommit, StringComparison.Ordinal))
            return false;
        if (string.Equals(reason, ConnectivityReasons.JoinDenied, StringComparison.Ordinal))
            return false;
        if (string.Equals(destConfirmation, DestConfirmationWire.SplitBrain, StringComparison.Ordinal)
            || string.Equals(destConfirmation, DestConfirmationWire.Unknown, StringComparison.Ordinal))
        {
            return string.Equals(reason, ConnectivityReasons.PeerUnavailable, StringComparison.Ordinal)
                || string.Equals(reason, ConnectivityReasons.StreamReset, StringComparison.Ordinal)
                || string.Equals(reason, ConnectivityReasons.JoinExpired, StringComparison.Ordinal);
        }

        return string.Equals(reason, ConnectivityReasons.TransferIncomplete, StringComparison.Ordinal)
            || string.Equals(reason, ConnectivityReasons.PeerUnavailable, StringComparison.Ordinal)
            || string.Equals(reason, ConnectivityReasons.StreamReset, StringComparison.Ordinal)
            || string.Equals(reason, ConnectivityReasons.JoinExpired, StringComparison.Ordinal);
    }

    public static bool IsStableReason(string? reason) =>
        reason is ConnectivityReasons.JoinDenied
            or ConnectivityReasons.JoinExpired
            or ConnectivityReasons.PeerUnavailable
            or ConnectivityReasons.StreamReset
            or ConnectivityReasons.TransferIncomplete
            or ConnectivityReasons.UnknownCommit;

    private static RelayFailure Fail(
        string reason,
        string detail,
        string stage,
        string attemptId,
        bool retryable,
        string? sourceStatus,
        string? destConfirmation) =>
        new()
        {
            Ok = false,
            Reason = reason,
            Detail = detail,
            Stage = stage,
            AttemptId = attemptId,
            Retryable = retryable,
            SourceStatus = sourceStatus,
            DestConfirmation = destConfirmation,
        };
}

/// <summary>
/// Attach drop after a relay loss. Keep the destination mux alive.
/// Do not advise <c>hypa mux stop</c>. Allow a fresh authorized attach
/// after the relay returns. Do not treat a reconnect sequence as success.
/// </summary>
public static class AttachRelayDrop
{
    public const string OperatorCopy =
        "The destination mux is still running. The client is disconnected. " +
        "Attach again after the relay returns.";

    public static AttachDropResult ForRelayLoss(string attemptId, string? reason = null)
    {
        var resolved = string.Equals(reason, ConnectivityReasons.StreamReset, StringComparison.Ordinal)
            ? ConnectivityReasons.StreamReset
            : ConnectivityReasons.PeerUnavailable;
        return new AttachDropResult
        {
            Ok = false,
            Reason = resolved,
            Detail = "relay closed during attach",
            Stage = RelayFailureStageWire.Attach,
            AttemptId = attemptId,
            Retryable = true,
            MuxRemainsUp = true,
            ClientDisconnected = true,
            FreshAuthorizedAttachAllowed = true,
            OperatorCopy = OperatorCopy,
        };
    }

    public static bool AdvisesMuxStop(string copy) =>
        copy.Contains("hypa mux stop", StringComparison.Ordinal);
}
