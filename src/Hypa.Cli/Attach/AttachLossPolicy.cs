using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Connectivity.Domain;

namespace Hypa.Cli.Attach;

/// <summary>
/// Stall versus detach for attach. Remote drop names the Placement.
/// Remote drop does not print mux stop.
/// </summary>
public static class AttachLossPolicy
{
    public const string LocalDetachBanner =
        "Detached. Server is still running. Stop with: hypa mux stop";

    public const string UnnamedRemoteName = "destination";

    public const string RemoteDetachBanner =
        "disconnected from " + UnnamedRemoteName + ". Destination mux is still running.";

    public static ClientLossKind Classify(bool userDetach, string? reason) =>
        AttachReconnectRules.Classify(userDetach, reason);

    public static bool ShouldReconnect(ClientLossKind kind) =>
        AttachReconnectRules.ShouldReconnect(kind);

    public static bool IsRemoteDestination(bool remoteDestination, SidebarCubeKind? kind)
    {
        if (kind == SidebarCubeKind.Local)
            return false;
        if (kind is SidebarCubeKind.Peer or SidebarCubeKind.Cube)
            return true;
        return remoteDestination;
    }

    public static string ResolveDisplayName(string? displayName, SidebarCubeKind? kind)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName.Trim();
        return kind == SidebarCubeKind.Cube ? "Cube" : UnnamedRemoteName;
    }

    public static bool AdvisesMuxStop(string copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        return copy.Contains("hypa mux stop", StringComparison.Ordinal);
    }

    public static string FormatStall(
        bool remoteDestination,
        string? displayName = null,
        SidebarCubeKind? kind = null)
    {
        if (!IsRemoteDestination(remoteDestination, kind))
            return "reconnecting";
        return "reconnecting to " + ResolveDisplayName(displayName, kind);
    }

    public static string FormatBanner(
        string? reason,
        bool remoteDestination,
        string? displayName = null,
        SidebarCubeKind? kind = null)
    {
        if (IsRemoteDestination(remoteDestination, kind))
        {
            return "disconnected from "
                + ResolveDisplayName(displayName, kind)
                + ". Destination mux is still running.";
        }

        return string.IsNullOrWhiteSpace(reason)
            ? LocalDetachBanner
            : "Detached (" + reason + "). Server is still running. Stop with: hypa mux stop";
    }
}
