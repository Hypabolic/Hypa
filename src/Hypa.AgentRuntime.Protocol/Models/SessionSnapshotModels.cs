using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>
/// <c>session.snapshot</c> result. Live wire and typed DTO use JSON number major
/// (<see cref="ProtocolVersion.Current"/>). Display string <c>"1.1"</c> is
/// <see cref="ProtocolVersion.Wire"/> for human/docs only — not the wire kind.
/// </summary>
public sealed record SessionSnapshotResult
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    /// <summary>Protocol major as JSON number (same as live ControlPlane).</summary>
    [JsonPropertyName("protocol_version")]
    public int? ProtocolVersion { get; init; }

    [JsonPropertyName("focused_workspace_id")]
    public string? FocusedWorkspaceId { get; init; }

    [JsonPropertyName("workspaces")]
    public IReadOnlyList<JsonElement>? Workspaces { get; init; }

    [JsonPropertyName("panes")]
    public IReadOnlyList<JsonElement>? Panes { get; init; }

    [JsonPropertyName("tabs")]
    public IReadOnlyList<JsonElement>? Tabs { get; init; }

    [JsonPropertyName("focused_tab_id")]
    public string? FocusedTabId { get; init; }

    /// <summary>
    /// Mux-wide focus. <c>focused_*</c> can carry the calling attach client's
    /// own view; these fields always carry the mux focus.
    /// </summary>
    [JsonPropertyName("mux_focused_workspace_id")]
    public string? MuxFocusedWorkspaceId { get; init; }

    [JsonPropertyName("mux_focused_tab_id")]
    public string? MuxFocusedTabId { get; init; }

    [JsonPropertyName("mux_focused_pane_id")]
    public string? MuxFocusedPaneId { get; init; }

    [JsonPropertyName("started_at")]
    public string? StartedAt { get; init; }

    [JsonPropertyName("session_state")]
    public string? SessionState { get; init; }

    [JsonPropertyName("placement")]
    public string? Placement { get; init; }

    [JsonPropertyName("placement_generation")]
    public int? PlacementGeneration { get; init; }

    [JsonPropertyName("persistence_schema")]
    public int? PersistenceSchema { get; init; }

    [JsonPropertyName("replay_complete")]
    public bool? ReplayComplete { get; init; }

    /// <summary>Optional in-memory title override. Absent when unset. Not persisted.</summary>
    [JsonPropertyName("window_title_override")]
    public string? WindowTitleOverride { get; init; }

    /// <summary>Transient Agents-view projection. Absent when unset. Not persisted.</summary>
    [JsonPropertyName("agent_view")]
    public SessionAgentViewSnapshot? AgentView { get; init; }

    /// <summary>Pane ids after the Agents-view projection. Absent when no view is active.</summary>
    [JsonPropertyName("agent_order")]
    public IReadOnlyList<string>? AgentOrder { get; init; }

    /// <summary>
    /// Additive popup overlay when open. Omitted when closed
    /// (<see cref="JsonIgnoreCondition.WhenWritingNull"/>).
    /// </summary>
    [JsonPropertyName("popup")]
    public SessionPopupSnapshot? Popup { get; init; }

    [JsonPropertyName("worktree_directory")]
    public string? WorktreeDirectory { get; init; }

    [JsonPropertyName("attach_clients")]
    public IReadOnlyList<AttachClientSnapshot>? AttachClients { get; init; }

    [JsonPropertyName("attach_client_ids")]
    public IReadOnlyList<string>? AttachClientIds { get; init; }

    /// <summary>Live plugin resources. Absent when the store is empty. Not persisted.</summary>
    [JsonPropertyName("resources")]
    public IReadOnlyList<PluginResourceDto>? Resources { get; init; }
}

/// <summary>Live attach client listed for overlay owner selection.</summary>
public sealed record AttachClientSnapshot
{
    [JsonPropertyName("attach_client_id")]
    public string? AttachClientId { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("client_mode")]
    public string? ClientMode { get; init; }
}

/// <summary>Publish the attach client's actual UI mode.</summary>
public sealed record UiClientModeParams
{
    [JsonPropertyName("client_mode")]
    public string? ClientMode { get; init; }
}
