using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Shared POSIX process-group kill helpers for Unix PTY disposal paths.
/// Used by the hypa-pty-host adapter fallback.
/// </summary>
internal static class UnixProcessGroupSignals
{
    /// <summary>Negative of the setsid() leader pid — target for kill(2) process-group signals.</summary>
    public static int GroupId(int leaderPid) => -leaderPid;

    public const int SIGINT = 2;
    public const int SIGKILL = 9;
    public const int SIGTERM = 15;

    /// <summary>
    /// Signal the process group of a setsid() session leader.
    /// Prefer kill(-leaderPid). Optionally fall back to the leader only when
    /// <paramref name="fallbackToLeader"/> is true (never after hard-kill:
    /// the leader PID may already be reusable).
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static void SignalGroup(int leaderPid, int signal, bool fallbackToLeader)
    {
        if (leaderPid <= 0)
            return;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var pg = GroupId(leaderPid);
        if (NativeKill(pg, signal) == 0 || !fallbackToLeader)
            return;

        NativeKill(leaderPid, signal);
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int sig);
}
