using Hypa.AgentRuntime.Domain.ProgramStatus;

namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Pure rules for <see cref="PaneState.Seen"/>. A frontend marks a pane seen when the person can see it.
/// These rules decide when a pane becomes unseen again.
/// </summary>
public static class PaneSeenPolicy
{
    /// <summary>
    /// Skill model: idle is seen done. A transition into Done is unseen unless
    /// the occupant was already reviewed (Done or Idle). A transition into Error is unseen
    /// unless the pane already was in Error and seen.
    /// </summary>
    public static bool AfterStatus(PaneState pane, AgentStatus nextStatus)
    {
        ArgumentNullException.ThrowIfNull(pane);
        if (nextStatus == AgentStatus.Idle)
            return true;
        if (nextStatus == AgentStatus.Error)
            return pane.AgentStatus == AgentStatus.Error && pane.Seen;
        if (nextStatus != AgentStatus.Done)
            return pane.Seen;
        if (pane.AgentStatus is AgentStatus.Done or AgentStatus.Idle)
            return pane.Seen;
        return false;
    }

    /// <summary>
    /// True when <paramref name="next"/> holds a new done or error result of the program status:
    /// program status owns the status, the top record is done or error, and it differs from the
    /// top record of <paramref name="previous"/> in identity or content. A report that sends the
    /// same record again is not a new result. A new result is unseen, also when the pane was in
    /// done or error before.
    /// </summary>
    public static bool IsNewProgramResult(PaneState previous, PaneState next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (next.AgentAuthority is not { IsProgramStatus: true })
            return false;
        if (ProgramStatusRollup.Top(next.ProgramStatusStore) is not
            { State: ProgramStatusState.Done or ProgramStatusState.Error } top)
        {
            return false;
        }

        var before = ProgramStatusRollup.Top(previous.ProgramStatusStore);
        return before is null || !before.SameContent(top);
    }
}
