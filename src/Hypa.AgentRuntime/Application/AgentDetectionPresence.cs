namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Foreground-process identity with miss confirmation.
/// Transient misses keep the current agent. Confirmed misses clear it.
/// A pane-shell return reports process-exit, then clears on the next probe.
/// </summary>
public sealed class AgentDetectionPresence
{
    public const int MissConfirmationAttempts = 6;

    public static readonly TimeSpan ProcessRecheckIdentified = TimeSpan.FromSeconds(5);

    public string? CurrentAgent { get; private set; }

    public int OccupantGeneration { get; private set; } = -1;

    public int ConsecutiveMisses { get; private set; }

    public bool PendingForegroundShellClear { get; private set; }

    public bool ForegroundShellExitReported { get; private set; }

    /// <summary>
    /// A live foreground group was observed for this occupant.
    /// After that, a null probe is a miss, not spawn <c>pane.Command</c>.
    /// </summary>
    public bool SawForegroundGroup { get; private set; }

    public int? LastForegroundGroupId { get; private set; }

    public DateTimeOffset LastProcessCheck { get; private set; }

    public void Reset(int occupantGeneration)
    {
        CurrentAgent = null;
        ConsecutiveMisses = 0;
        OccupantGeneration = occupantGeneration;
        PendingForegroundShellClear = false;
        ForegroundShellExitReported = false;
        SawForegroundGroup = false;
        LastForegroundGroupId = null;
        LastProcessCheck = default;
    }

    public void MarkForegroundGroupObserved() => SawForegroundGroup = true;

    public void NoteProcessCheck(DateTimeOffset at, int? groupId)
    {
        LastProcessCheck = at;
        LastForegroundGroupId = groupId;
    }

    /// <summary>
    /// <c>foreground_shell_exit_reported</c> when process-exit is published.
    /// </summary>
    public void MarkForegroundShellExitReported()
    {
        if (PendingForegroundShellClear)
            ForegroundShellExitReported = true;
    }

    /// <summary>
    // Returns true when published
    /// identity changed. <c>ReportReplacementProcess</c> is always a
    /// change (<c>src/pane.rs:402-408</c>, <c>:845-847</c>).
    /// </summary>
    public bool ApplyForegroundShellAction(
        ForegroundShellAgentAction action,
        string? previousAgent,
        string? newAgent)
    {
        switch (action)
        {
            case ForegroundShellAgentAction.ReportReplacementProcess:
                PendingForegroundShellClear = false;
                ForegroundShellExitReported = false;
                ObserveProcessProbe(previousAgent);
                return true;
            case ForegroundShellAgentAction.ReportProcessExit:
                PendingForegroundShellClear = true;
                return false;
            case ForegroundShellAgentAction.ClearAgent:
                PendingForegroundShellClear = false;
                ForegroundShellExitReported = false;
                return ClearCurrentAgent();
            default:
                PendingForegroundShellClear = false;
                ForegroundShellExitReported = false;
                return ObserveProcessProbe(newAgent);
        }
    }

    /// <summary>
    /// </summary>
    public bool ClearCurrentAgent()
    {
        ConsecutiveMisses = 0;
        if (CurrentAgent is null)
            return false;
        CurrentAgent = null;
        return true;
    }

    /// <summary>
    /// Returns true when the published agent identity changed.
    /// </summary>
    public bool ObserveProcessProbe(string? identifiedAgent)
    {
        if (!string.IsNullOrWhiteSpace(identifiedAgent))
        {
            ConsecutiveMisses = 0;
            if (string.Equals(identifiedAgent, CurrentAgent, StringComparison.Ordinal))
                return false;
            CurrentAgent = identifiedAgent;
            return true;
        }

        if (CurrentAgent is null)
        {
            ConsecutiveMisses = 0;
            return false;
        }

        ConsecutiveMisses = ConsecutiveMisses < int.MaxValue
            ? ConsecutiveMisses + 1
            : ConsecutiveMisses;
        if (ConsecutiveMisses < MissConfirmationAttempts)
            return false;

        CurrentAgent = null;
        ConsecutiveMisses = 0;
        return true;
    }
}
