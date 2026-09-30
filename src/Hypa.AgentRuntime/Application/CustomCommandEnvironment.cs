namespace Hypa.AgentRuntime.Application;

/// <summary>Inputs for Hypa custom-command child environment keys.</summary>
public sealed record CustomCommandEnvironmentRequest
{
    public string? SocketPath { get; init; }

    public string? BinPath { get; init; }

    public string? WorkspaceId { get; init; }

    public string? TabId { get; init; }

    public string? PaneId { get; init; }

    public string? PaneCwd { get; init; }
}

/// <summary>
/// Builds Hypa env for shell / pane / popup custom-command children.
// Socket path is <c>HYPA_RUNTIME_SOCKET</c> only.
/// Do not invent <c>HYPA_SOCKET_PATH</c>.
/// </summary>
public static class CustomCommandEnvironment
{
    public const string RuntimeSocket = "HYPA_RUNTIME_SOCKET";
    public const string BinPath = "HYPA_BIN_PATH";
    public const string ActiveWorkspaceId = "HYPA_ACTIVE_WORKSPACE_ID";
    public const string ActiveTabId = "HYPA_ACTIVE_TAB_ID";
    public const string ActivePaneId = "HYPA_ACTIVE_PANE_ID";
    public const string ActivePaneCwd = "HYPA_ACTIVE_PANE_CWD";

    public static IReadOnlyDictionary<string, string> Build(CustomCommandEnvironmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(request.SocketPath))
            env[RuntimeSocket] = request.SocketPath;

        if (!string.IsNullOrWhiteSpace(request.BinPath))
            env[BinPath] = request.BinPath;
        if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
            env[ActiveWorkspaceId] = request.WorkspaceId;
        if (!string.IsNullOrWhiteSpace(request.TabId))
            env[ActiveTabId] = request.TabId;
        if (!string.IsNullOrWhiteSpace(request.PaneId))
            env[ActivePaneId] = request.PaneId;
        if (!string.IsNullOrWhiteSpace(request.PaneCwd))
            env[ActivePaneCwd] = request.PaneCwd;
        return env;
    }
}
