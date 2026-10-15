namespace Hypa.AgentRuntime.Domain.ProgramStatus;

/// <summary>How much a frontend should press for an attention item.</summary>
public enum AttentionPriority
{
    /// <summary>The pane finished. The person can look later.</summary>
    Normal,

    /// <summary>The pane is blocked or failed. It waits for the person.</summary>
    High,
}

/// <summary>
/// The part of a pane program status that decides if a change is a new request.
/// A rule compares two of these values.
/// </summary>
public sealed record ProgramStatusAttentionSignature(ProgramStatusState State, ProgramStatusKind? Kind, string? Msg);

/// <summary>
/// Pure rule: when does a program status change need the person?
/// A pane needs the person when its state is blocked, done, or error.
/// A change is new when the state differs, or when a blocked pane asks something else
/// (a different kind or message).
/// Memory per pane and rate limits are not part of this rule.
/// They belong to the policy that calls it.
/// </summary>
public static class ProgramStatusAttentionRule
{
    /// <summary>True for blocked, done, and error.</summary>
    public static bool NeedsPerson(ProgramStatusState state) =>
        state is ProgramStatusState.Blocked or ProgramStatusState.Done or ProgramStatusState.Error;

    /// <summary>
    /// True when <paramref name="next"/> is a new request after <paramref name="previous"/>.
    /// A repeat of the same state is new only for a blocked pane that asks something else.
    /// </summary>
    public static bool IsNewRequest(ProgramStatusAttentionSignature previous, ProgramStatusAttentionSignature next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        return previous.State != next.State
            || (next.State is ProgramStatusState.Blocked
                && (previous.Kind != next.Kind || !string.Equals(previous.Msg, next.Msg, StringComparison.Ordinal)));
    }

    /// <summary>Done is normal. Blocked and error are high.</summary>
    public static AttentionPriority PriorityOf(ProgramStatusState state) =>
        state is ProgramStatusState.Done ? AttentionPriority.Normal : AttentionPriority.High;
}
