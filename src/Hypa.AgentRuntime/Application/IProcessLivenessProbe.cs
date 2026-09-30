namespace Hypa.AgentRuntime.Application;

/// <summary>
/// OS process liveness check used during restart PID reconciliation.
/// Test doubles return fixed answers; production uses kill(pid, 0) / OpenProcess.
/// </summary>
public interface IProcessLivenessProbe
{
    /// <summary>True if a process with <paramref name="pid"/> appears alive.</summary>
    bool IsAlive(int pid);
}
