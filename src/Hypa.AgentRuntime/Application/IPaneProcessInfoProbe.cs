namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Foreground process-group snapshot. Command is the agent kind when argv
/// names an agent. Otherwise it is the basename that owns the group.
/// </summary>
public sealed record PaneForegroundInfo
{
    public required int GroupId { get; init; }
    public int? Pid { get; init; }
    public string Command { get; init; } = "";

    /// <summary>
    /// the pane shell pid is in the foreground job. Unix approximates
    /// that with <c>tpgid == shellPid</c>.
    /// </summary>
    public bool ForegroundIsPaneShell { get; init; }
}

/// <summary>
/// Best-effort foreground process-group lookup for <c>pane.process_info</c>.
/// Returns null when the group cannot be observed (process-io, dead pane, missing host).
/// </summary>
public interface IPaneProcessInfoProbe
{
    int? TryGetForegroundGroup(int shellPid);
    PaneForegroundInfo? TryGetForegroundInfo(int shellPid);
}
