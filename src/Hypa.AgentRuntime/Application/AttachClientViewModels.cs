namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Memory-only attach view.
/// <c>src/server/clients.rs:63-67</c> stores focused workspace and active tab
/// per workspace. Hypa also stores focused pane per tab.
/// </summary>
public sealed record AttachClientView
{
    public required string ClientId { get; init; }

    public required string EndpointId { get; init; }

    public required string BootId { get; init; }

    public string? FocusedWorkspaceId { get; init; }

    public IReadOnlyDictionary<string, string> ActiveTabIds { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> FocusedPaneIds { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? FocusedTabId() =>
        FocusedWorkspaceId is { } workspace
        && ActiveTabIds.TryGetValue(workspace, out var tab)
            ? tab
            : null;

    public string? FocusedPaneId()
    {
        var tab = FocusedTabId();
        return tab is not null && FocusedPaneIds.TryGetValue(tab, out var pane)
            ? pane
            : null;
    }
}

/// <summary>
// / Topology defaults.
/// fallbacks from layout traversal order.
/// </summary>
public sealed record AttachClientTopology
{
    public string? FocusedWorkspaceId { get; init; }

    public string? FallbackWorkspaceId { get; init; }

    public IReadOnlyDictionary<string, string> ActiveTabIds { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> TabWorkspaceIds { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> FocusedPaneIds { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> FallbackPaneIds { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> PaneTabIds { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<string> WorkspaceOrder { get; init; } = [];

    public IReadOnlyDictionary<string, IReadOnlyList<string>> TabOrderByWorkspace { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> PaneOrderByTab { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public static AttachClientTopology Empty { get; } = new();
}

public sealed record AttachClientViewError(string Code, string Message)
{
    public static AttachClientViewError InvalidConnection { get; } =
        new("invalid_connection", "connection is required");

    public static AttachClientViewError ClientMismatch { get; } =
        new("client_mismatch", "client_id does not match this connection");

    public static AttachClientViewError StaleGeneration { get; } =
        new("stale_generation", "connection generation is stale");
}

public sealed record AttachClientViewBindRequest
{
    public required string ConnectionId { get; init; }

    public required string ClientId { get; init; }

    public required string EndpointId { get; init; }

    public required string BootId { get; init; }

    public required ulong ConnectionGeneration { get; init; }
}

public sealed record AttachClientViewFocusRequest
{
    public required string ConnectionId { get; init; }

    public required string ClientId { get; init; }

    public required ulong ConnectionGeneration { get; init; }

    public string? WorkspaceId { get; init; }

    public string? TabId { get; init; }

    public string? PaneId { get; init; }
}

public sealed record AttachClientViewApplyResult
{
    public required AttachClientView View { get; init; }

    public required bool Changed { get; init; }
}

public static class AttachClientViewCopy
{
    public const string EmptyTopology = "No workspaces. Create a workspace to continue.";

    public const string EmptyTab = "No panes in this tab.";

    public static string Describe(AttachClientView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (string.IsNullOrWhiteSpace(view.FocusedWorkspaceId))
            return EmptyTopology;
        if (string.IsNullOrWhiteSpace(view.FocusedPaneId()))
            return EmptyTab;
        return string.Empty;
    }
}
