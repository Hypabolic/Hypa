namespace Hypa.AgentRuntime.Application;

/// <summary>
/// The running server's own install. A package upgrade can remove the
/// directory a live mux started from; new panes then cannot start.
/// </summary>
public sealed record ServerInstallStatus(string? Version, bool InstallPresent);

/// <summary>
/// Port so <c>ping</c> and pane start failures can report a removed install.
/// </summary>
public interface IServerInstallProbe
{
    ServerInstallStatus Probe();
}
