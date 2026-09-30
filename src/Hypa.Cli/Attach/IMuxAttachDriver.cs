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
