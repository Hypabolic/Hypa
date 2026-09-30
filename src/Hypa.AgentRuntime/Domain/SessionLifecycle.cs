namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Session lifecycle states for persistence and wire <c>session_state</c>.
/// </summary>
public static class SessionLifecycle
{
    public const string Starting = "starting";
    public const string Ready = "ready";
    public const string Stopping = "stopping";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    public const string HandoffPending = "handoff_pending";
    public const string HandoffInProgress = "handoff_in_progress";

    /// <summary>Design §10.3: prepare complete, awaiting export or transfer.</summary>
    public const string HandoffPrepared = "handoff_prepared";

    /// <summary>
    /// session is frozen for checkpoint transfer. Mutations denied with invalid_state.
    /// Local P0 reattach never requires this state.
    /// </summary>
    public const string FrozenReadOnly = "frozen_read_only";

    /// <summary>Design §10.3: remote/local placement reconciliation in progress.</summary>
    public const string Reconciling = "reconciling";

    /// <summary>True when graph mutations (create/close/bind/send) must be denied.</summary>
    public static bool IsFrozen(string? lifecycleState) =>
        string.Equals(lifecycleState, FrozenReadOnly, StringComparison.Ordinal);
}
