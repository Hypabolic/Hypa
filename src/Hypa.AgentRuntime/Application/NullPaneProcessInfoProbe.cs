namespace Hypa.AgentRuntime.Application;

/// <summary>Always-null probe for tests and hosts that cannot observe a PTY child.</summary>
public sealed class NullPaneProcessInfoProbe : IPaneProcessInfoProbe
{
    public static NullPaneProcessInfoProbe Instance { get; } = new();

    public int? TryGetForegroundGroup(int shellPid) => null;

    public PaneForegroundInfo? TryGetForegroundInfo(int shellPid) => null;
}
