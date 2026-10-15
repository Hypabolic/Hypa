namespace Hypa.AgentRuntime.Domain.ProgramStatus;

/// <summary>
/// Pure policy that turns the records of a pane into one pane status.
/// Urgency order, most urgent first: blocked, error, working, done, idle.
/// Records of the same state: the most recently updated record wins.
/// Panes of the same state: the most recently changed pane wins (<see cref="Outranks"/>).
/// </summary>
public static class ProgramStatusRollup
{
    /// <summary>The most urgent record with its <c>app</c> resolved. Null when the store is empty.</summary>
    public static ProgramStatusRecord? Top(ProgramStatusStore store) => Top(store.Resolved());

    /// <summary>The most urgent record of <paramref name="records"/>, which already have <c>app</c> resolved.</summary>
    public static ProgramStatusRecord? Top(IReadOnlyList<ProgramStatusRecord> records)
    {
        ProgramStatusRecord? top = null;
        foreach (var record in records)
        {
            if (top is null
                || Urgency(record.State) > Urgency(top.State)
                || (Urgency(record.State) == Urgency(top.State) && record.UpdatedSeq > top.UpdatedSeq))
            {
                top = record;
            }
        }

        return top;
    }

    /// <summary>
    /// True when the first pane outranks the second one: more urgent state, or the same
    /// urgency and a more recent change. A pane without a change time (a restored pane) is
    /// older than any pane with one. Equal times do not outrank, so the caller's order breaks that tie.
    /// This is the one tie rule for the tab view and the session view.
    /// </summary>
    public static bool Outranks(
        ProgramStatusState state, DateTimeOffset? changedAt,
        ProgramStatusState otherState, DateTimeOffset? otherChangedAt)
    {
        var urgency = Urgency(state);
        var otherUrgency = Urgency(otherState);
        if (urgency != otherUrgency)
            return urgency > otherUrgency;
        return (changedAt ?? DateTimeOffset.MinValue) > (otherChangedAt ?? DateTimeOffset.MinValue);
    }

    public static AgentStatus ToAgentStatus(ProgramStatusState state) => state switch
    {
        ProgramStatusState.Blocked => AgentStatus.Blocked,
        ProgramStatusState.Error => AgentStatus.Error,
        ProgramStatusState.Working => AgentStatus.Working,
        ProgramStatusState.Done => AgentStatus.Done,
        ProgramStatusState.Idle => AgentStatus.Idle,
        _ => AgentStatus.Unknown,
    };

    /// <summary>Higher is more urgent.</summary>
    public static int Urgency(ProgramStatusState state) => state switch
    {
        ProgramStatusState.Blocked => 5,
        ProgramStatusState.Error => 4,
        ProgramStatusState.Working => 3,
        ProgramStatusState.Done => 2,
        ProgramStatusState.Idle => 1,
        _ => 0,
    };
}
