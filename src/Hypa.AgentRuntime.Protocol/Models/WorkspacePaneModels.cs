using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>ping</c> result.</summary>
public sealed record PingResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("protocol")]
    public int Protocol { get; init; }

    /// <summary>Server product version. Absent on servers before 1.0.6.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>False when an upgrade removed the install the server started from.</summary>
    [JsonPropertyName("install_present")]
    public bool? InstallPresent { get; init; }
}

/// <summary><c>workspace.create</c> params. Binding may be nested or flattened.</summary>
public sealed record WorkspaceCreateParams
{
    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("args")]
    public IReadOnlyList<string>? Args { get; init; }

    [JsonPropertyName("create_pane")]
    public bool? CreatePane { get; init; }

    [JsonPropertyName("binding")]
    public BindingDto? Binding { get; init; }

    [JsonPropertyName("agent_session_id")]
    public string? AgentSessionId { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("step_id")]
    public string? StepId { get; init; }

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; init; }

    [JsonPropertyName("memory_id")]
    public string? MemoryId { get; init; }

    [JsonPropertyName("project_root")]
    public string? ProjectRoot { get; init; }
}

/// <summary><c>workspace.get</c> params.</summary>
public sealed record WorkspaceGetParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }
}

/// <summary><c>workspace.focus</c> params.</summary>
public sealed record WorkspaceFocusParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }
}

/// <summary><c>workspace.rename</c> params.</summary>
public sealed record WorkspaceRenameParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

/// <summary><c>workspace.close</c> params.</summary>
public sealed record WorkspaceCloseParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }
}

/// <summary><c>workspace.move</c> params. The index is an insert slot from 0 through count.</summary>
public sealed record WorkspaceMoveParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("insert_index")]
    public int? InsertIndex { get; init; }
}

/// <summary><c>workspace.move_block</c> params.</summary>
public sealed record WorkspaceMoveBlockParams
{
    [JsonPropertyName("workspace_ids")]
    public IReadOnlyList<string>? WorkspaceIds { get; init; }

    [JsonPropertyName("before_workspace_id")]
    public string? BeforeWorkspaceId { get; init; }
}

/// <summary><c>workspace.report_metadata</c> params.</summary>
public sealed record WorkspaceReportMetadataParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("tokens")]
    public IReadOnlyDictionary<string, string?>? Tokens { get; init; }

    [JsonPropertyName("seq")]
    public long? Sequence { get; init; }

    [JsonPropertyName("ttl_ms")]
    public int? TtlMs { get; init; }
}

/// <summary>Live workspace object (<c>workspace.get</c> / focus / rename / list item).</summary>
public sealed record WorkspaceResult
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; init; }

    [JsonPropertyName("focused_tab_id")]
    public string? FocusedTabId { get; init; }

    [JsonPropertyName("binding")]
    public BindingDto? Binding { get; init; }

    [JsonPropertyName("tokens")]
    public IReadOnlyDictionary<string, string>? Tokens { get; init; }

    /// <summary>Provenance chrome. No checkout path.</summary>
    [JsonPropertyName("worktree")]
    public WorkspaceWorktreeChrome? Worktree { get; init; }

    [JsonPropertyName("resolved_cwd")]
    public string? ResolvedCwd { get; init; }

    [JsonPropertyName("identity_pane_id")]
    public string? IdentityPaneId { get; init; }

    [JsonPropertyName("custom_label")]
    public bool CustomLabel { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("git_status")]
    public string? GitStatus { get; init; }

    [JsonPropertyName("repository_name")]
    public string? RepositoryName { get; init; }

}

/// <summary><c>workspace.close</c> result.</summary>
public sealed record WorkspaceCloseResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }
}

public sealed record MetadataReportResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

/// <summary><c>pane.create</c> params.</summary>
public sealed record PaneCreateParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("args")]
    public IReadOnlyList<string>? Args { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("binding")]
    public BindingDto? Binding { get; init; }

    [JsonPropertyName("agent_session_id")]
    public string? AgentSessionId { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("step_id")]
    public string? StepId { get; init; }

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; init; }

    [JsonPropertyName("memory_id")]
    public string? MemoryId { get; init; }

    [JsonPropertyName("project_root")]
    public string? ProjectRoot { get; init; }

    /// <summary><c>hidden</c> or <c>tiled</c>. Default tiled when omitted.</summary>
    [JsonPropertyName("placement")]
    public string? Placement { get; init; }

    [JsonPropertyName("parent_pane_id")]
    public string? ParentPaneId { get; init; }

    [JsonPropertyName("occupant_token")]
    public string? OccupantToken { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }
}

/// <summary><c>pane.get</c> / <c>agent.get</c> params.</summary>
public sealed record PaneGetParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }
}

/// <summary><c>pane.send_text</c> params.</summary>
public sealed record PaneSendTextParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("input")]
    public string? Input { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("overlay_generation")]
    public long? OverlayGeneration { get; init; }
}

/// <summary><c>pane.resize</c> params.</summary>
public sealed record PaneResizeParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("cols")]
    public int? Cols { get; init; }

    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }
}

/// <summary><c>pane.read</c> / <c>agent.read</c> params.</summary>
public sealed record PaneReadParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("lines")]
    public int? Lines { get; init; }
}

/// <summary><c>pane.close</c> params.</summary>
public sealed record PaneCloseParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    /// <summary>
    /// Optional source-fence acknowledgement. The mux records this Work
    /// generation before it closes the pane.
    /// </summary>
    [JsonPropertyName("work_id")]
    public string? WorkId { get; init; }

    [JsonPropertyName("generation")]
    public long? Generation { get; init; }
}

/// <summary><c>pane.scroll</c> params. Offset is lines above the live bottom.</summary>
public sealed record PaneScrollParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("offset")]
    public int? Offset { get; init; }
}

/// <summary><c>pane.scroll</c> result. Offset is clamped to <c>[0, max_offset]</c>.</summary>
public sealed record PaneScrollResult
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("max_offset")]
    public int MaxOffset { get; init; }
}

/// <summary>
// Hypa maps
/// <c>content_revision</c> onto <c>generation</c> (posted attach snapshot id).
/// </summary>
public sealed record PaneLinkActivateParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("viewport_row")]
    public int? ViewportRow { get; init; }

    [JsonPropertyName("col")]
    public int? Col { get; init; }

    [JsonPropertyName("generation")]
    public long? Generation { get; init; }

    [JsonPropertyName("offset_from_bottom")]
    public long? OffsetFromBottom { get; init; }
}

public sealed record PaneLinkActivateResult
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("handled")]
    public bool Handled { get; init; }
}
