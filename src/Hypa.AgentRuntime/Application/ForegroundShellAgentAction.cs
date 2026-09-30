namespace Hypa.AgentRuntime.Application;

/// <summary>
/// </summary>
public enum ForegroundShellAgentAction
{
    ObserveProbe,
    ReportProcessExit,
    ReportReplacementProcess,
    ClearAgent,
}

/// <summary>
/// </summary>
public static class ForegroundShellAgentActions
{
    public static ForegroundShellAgentAction Resolve(
        string? previousAgent,
        string? newAgent,
        bool foregroundIsPaneShell,
        bool processExitReported)
    {
        if (string.IsNullOrWhiteSpace(previousAgent))
            return ForegroundShellAgentAction.ObserveProbe;

        if (processExitReported)
        {
            if (string.Equals(newAgent, previousAgent, StringComparison.Ordinal))
                return ForegroundShellAgentAction.ReportReplacementProcess;
            if (string.IsNullOrWhiteSpace(newAgent))
                return ForegroundShellAgentAction.ClearAgent;
            return ForegroundShellAgentAction.ObserveProbe;
        }

        if (!string.IsNullOrWhiteSpace(newAgent))
            return ForegroundShellAgentAction.ObserveProbe;

        if (foregroundIsPaneShell)
            return ForegroundShellAgentAction.ReportProcessExit;

        return ForegroundShellAgentAction.ObserveProbe;
    }
}
