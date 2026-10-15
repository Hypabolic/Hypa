namespace Hypa.AgentRuntime.Domain.ProgramStatus;

/// <summary>
/// Pure policy that applies a new record store to a pane.
/// It follows <see cref="AgentSourcePrecedence"/>:
/// <list type="bullet">
/// <item><description>The pane keeps the new store in every case.</description></item>
/// <item><description>An explicit report holds the status. The records do not change it.</description></item>
/// <item><description>Otherwise the top record (<see cref="ProgramStatusRollup"/>) sets the status, kind (resolved app), and message,
/// and the pane holds a program status authority.</description></item>
/// <item><description>With no record left, the program status authority ends. The pane gets back the kind it had
/// before the records and loses the message of the records. The detector applies again.</description></item>
/// </list>
/// </summary>
public static class ProgramStatusAuthorityPolicy
{
    public static PaneState Apply(PaneState pane, ProgramStatusStore store, DateTimeOffset now)
    {
        var next = pane with { ProgramStatusStore = store };
        if (pane.HoldsReportedAuthority)
            return next;

        var top = ProgramStatusRollup.Top(store);
        if (top is null)
        {
            return pane.AgentAuthority is { IsProgramStatus: true } held
                ? next with
                {
                    AgentAuthority = null,
                    AgentStatus = AgentStatus.Unknown,
                    AgentKind = held.PriorAgentKind,
                    AgentMessage = null,
                }
                : next;
        }

        var status = ProgramStatusRollup.ToAgentStatus(top.State);
        // The first record keeps the kind that the pane has now (for example from the detector).
        var priorKind = pane.AgentAuthority is { IsProgramStatus: true } own ? own.PriorAgentKind : pane.AgentKind;
        return next with
        {
            AgentStatus = status,
            // A top record with no app shows the kind from before the records, not the app
            // of an earlier record.
            AgentKind = top.App ?? priorKind,
            AgentMessage = top.Msg,
            AgentAuthority = new PaneAgentAuthority
            {
                Source = AgentSourcePrecedence.ProgramStatusSource,
                // The same kind as AgentKind, so a copy of the authority keeps it.
                Agent = top.App ?? priorKind ?? string.Empty,
                State = status,
                Message = top.Msg,
                ReportedAt = now,
                PriorAgentKind = priorKind,
            },
        };
    }
}
