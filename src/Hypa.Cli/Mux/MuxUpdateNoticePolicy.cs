using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Infrastructure;
using Hypa.ControlPlane;

namespace Hypa.Cli.Mux;

/// <summary>
/// Decides the sidebar update row from the mux <c>ping</c> and this client's
/// own install. Restart is the only notice today. An auto-updater adds its
/// states here and reuses the restart that follows an install.
/// </summary>
internal static class MuxUpdateNoticePolicy
{
    internal const string RestartPrompt =
        "RESTART mux to update? Every pane closes. [y/n]";

    /// <param name="pingJson">Latest <c>ping</c> result from the attached mux.</param>
    /// <param name="clientVersion">This attach client's version.</param>
    /// <param name="clientInstallPresent">
    /// False when an upgrade replaced this client's executable. Restarting
    /// then brings both the client and the mux up on the installed version.
    /// </param>
    public static SidebarUpdateNotice? Evaluate(
        string? pingJson,
        string? clientVersion,
        bool clientInstallPresent)
    {
        var check = MuxServerVersionCheck.FromPing(pingJson, clientVersion);
        if (check is { IsStale: true } || !clientInstallPresent)
        {
            return new SidebarUpdateNotice(
                SidebarUpdateNoticeKind.RestartRequired,
                MuxRestartCommands.SidebarAction,
                RestartPrompt);
        }

        return null;
    }

    /// <summary>Whether this process's executable is still on disk.</summary>
    public static bool ClientInstallPresent() =>
        new ProcessServerInstallProbe(version: null, Environment.ProcessPath).Probe().InstallPresent;
}

/// <summary>
/// Port for the sidebar update row. Attach asks it on every heartbeat
/// <c>ping</c>. An auto-updater would extend the process implementation
/// with "update available" and "installing" states.
/// </summary>
public interface IMuxUpdateNoticeSource
{
    /// <summary>The row to show for the attached mux, or null to hide it.</summary>
    SidebarUpdateNotice? Evaluate(string? pingJson);
}

/// <summary>Compares the mux with this process's version and install.</summary>
public sealed class ProcessMuxUpdateNoticeSource : IMuxUpdateNoticeSource
{
    private readonly string? _clientVersion = MuxServerVersionCheck.CurrentClientVersion();

    public SidebarUpdateNotice? Evaluate(string? pingJson) =>
        MuxUpdateNoticePolicy.Evaluate(
            pingJson,
            _clientVersion,
            MuxUpdateNoticePolicy.ClientInstallPresent());
}
