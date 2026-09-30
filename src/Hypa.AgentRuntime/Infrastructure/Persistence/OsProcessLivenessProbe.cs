using System.Diagnostics;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Production process liveness probe via <see cref="Process.GetProcessById"/>.
/// PID reuse after reboot can false-positive — documents this limitation until.
/// </summary>
public sealed class OsProcessLivenessProbe : IProcessLivenessProbe
{
    public bool IsAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
