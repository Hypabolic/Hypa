using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Mux;
using Hypa.Placement.Application;

namespace Hypa.Cli.Attach;

public sealed record MuxAttachRequest(
    bool Once,
    bool InputRedirected,
    bool OutputRedirected,
    AttachClientConfig? Config = null,
    IAttachConfigLoader? ConfigLoader = null,
    bool RemoteDestination = false,
    string? PlacementDisplayName = null,
    SidebarCubeKind? PlacementKind = null,
    string? PlacementId = null,
    string? FocusPaneId = null,
    string? ConnectPlacementId = null,
    bool RemoteAttach = false,
    RemoteKeybindingsMode RemoteKeybindings = RemoteKeybindingsMode.Local,
    bool LiveHandoff = false)
{
    public bool PrintSnapshotAndExit => Once || InputRedirected || OutputRedirected;

    public AttachClientConfig AttachConfig => Config ?? AttachClientConfig.Default;
}

public interface IMuxAttachDriver
{
    Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct);
}

/// <summary>The mux the user confirmed a restart for before attach returned.</summary>
public sealed record MuxRestartRequest(string Session, string SocketPath);

/// <summary>
/// A driver that can end attach to restart the mux. The caller stops the mux
/// and starts the installed Hypa, since attach must leave the TTY first.
/// </summary>
public interface IMuxRestartSource
{
    /// <summary>Returns and clears the pending restart, if any.</summary>
    MuxRestartRequest? TakeRestartRequest();
}
