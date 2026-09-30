using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Official agent resume owns conversation history.
/// </summary>
public sealed record PaneRestoreStartup(
    OfficialAgentResumePlan? RestorePlan,
    string? InitialHistoryAnsi);

/// <summary>
/// Native agent resume suppresses history replay, including duplicates.
/// </summary>
public static class PaneRestoreStartupPlanner
{
    public static PaneRestoreStartup Plan(
        NativeAgentSessionRef? session,
        PaneHistorySnapshot? history,
        bool resumeAgentsOnRestore)
    {
        var restorePlan = OfficialAgentResumePlanner.TryPlan(session, resumeAgentsOnRestore);
        var hasNativeAgentRestore = restorePlan is not null;
        return new PaneRestoreStartup(
            restorePlan,
            hasNativeAgentRestore ? null : history?.Ansi);
    }
}
