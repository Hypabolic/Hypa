using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>Portable BSP node used by <c>layout.export</c> / <c>layout.apply</c>.</summary>
public sealed record LayoutNodeDto
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("command")]
    public IReadOnlyList<string>? Command { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("ratio")]
    public double? Ratio { get; init; }

    [JsonPropertyName("first")]
    public LayoutNodeDto? First { get; init; }

    [JsonPropertyName("second")]
    public LayoutNodeDto? Second { get; init; }
}

public sealed record TabCreateParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("create_pane")]
    public bool? CreatePane { get; init; }

    [JsonPropertyName("focus")]
    public bool? Focus { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("args")]
    public IReadOnlyList<string>? Args { get; init; }
}

public sealed record TabListParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }
}

public sealed record TabGetParams
{
    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }
}

public sealed record TabFocusParams
{
    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }
}

public sealed record TabRenameParams
{
    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

public sealed record TabMoveParams
{
    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("index")]
    public int? Index { get; init; }
}

public sealed record TabCloseParams
{
    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    /// <summary>
    /// When true, close only if the tab has no occupants. The server
    /// checks tiled layout ids and <c>HiddenPaneIds</c> under the same
    /// graph mutation lock as the close. Ordinary close omits this flag.
    /// </summary>
    [JsonPropertyName("if_empty")]
    public bool? IfEmpty { get; init; }
}

public sealed record PaneSplitParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("ratio")]
    public double? Ratio { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("args")]
    public IReadOnlyList<string>? Args { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }

    [JsonPropertyName("close_on_exit")]
    public bool? CloseOnExit { get; init; }

    [JsonPropertyName("focus")]
    public bool? Focus { get; init; }
}

public sealed record PaneMoveParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("destination")]
    public string? Destination { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("split")]
    public string? Split { get; init; }

    [JsonPropertyName("target_pane_id")]
    public string? TargetPaneId { get; init; }

    [JsonPropertyName("ratio")]
    public double? Ratio { get; init; }
}

public sealed record PaneZoomParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }
}

public sealed record PaneFocusDirectionParams
{
    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

public sealed record PaneLayoutParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }
}

public sealed record LayoutExportParams
{
    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

public sealed record LayoutApplyParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("root")]
    public LayoutNodeDto? Root { get; init; }

    [JsonPropertyName("tab_label")]
    public string? TabLabel { get; init; }

    [JsonPropertyName("focus")]
    public bool? Focus { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }
}

public sealed record LayoutSetSplitRatioParams
{
    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("path")]
    public IReadOnlyList<int>? Path { get; init; }

    [JsonPropertyName("ratio")]
    public double? Ratio { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }
}

public sealed record AgentStartParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    /// <summary>First-party or registered occupant id (for example <c>pi</c>). Mutually exclusive with <see cref="Command"/> and <see cref="Kind"/>.</summary>
    [JsonPropertyName("occupant")]
    public string? Occupant { get; init; }

    /// <summary>
    // Resolves aliases to a canonical occupant.
    /// Mutually exclusive with <see cref="Occupant"/> and <see cref="Command"/>.
    /// A present value (including empty or whitespace) must resolve. Omitted or JSON null
    /// keeps occupant, command, or restart-existing.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>
    /// Extra argv. With <see cref="Occupant"/>, appended after the manifest command
    /// (for example <c>--session</c> / path on Work handoff resume).
    /// </summary>
    [JsonPropertyName("args")]
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>
    /// Optional cube HOME override for this start (Continuity dest placement).
    /// When set, expands occupant templates with this HOME instead of the mux default.
    /// </summary>
    [JsonPropertyName("cube_home")]
    public string? CubeHome { get; init; }

    /// <summary>
    /// Optional spawn cwd override. When set, used as workspace for occupant expansion
    /// and as the process cwd.
    /// </summary>
    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    /// <summary>Optional env overlay merged onto the occupant env (dest wins on key clash).</summary>
    [JsonPropertyName("env")]
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Continuity Work id. Required when <see cref="Generation"/> is set.</summary>
    [JsonPropertyName("work_id")]
    public string? WorkId { get; init; }

    /// <summary>Continuity Work generation. Required when <see cref="WorkId"/> is set.</summary>
    [JsonPropertyName("generation")]
    public long? Generation { get; init; }
}

public sealed record TabResult
{
    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("ordinal")]
    public int? Ordinal { get; init; }

    [JsonPropertyName("focused_pane_id")]
    public string? FocusedPaneId { get; init; }

    [JsonPropertyName("zoomed")]
    public bool? Zoomed { get; init; }

    [JsonPropertyName("zoomed_pane_id")]
    public string? ZoomedPaneId { get; init; }

    [JsonPropertyName("custom_label")]
    public bool? CustomLabel { get; init; }

    [JsonPropertyName("layout")]
    public LayoutNodeDto? Layout { get; init; }

    [JsonPropertyName("pane")]
    public PaneCreateResult? Pane { get; init; }
}

public sealed record PaneCreateResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("cols")]
    public int? Cols { get; init; }

    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    [JsonPropertyName("alive")]
    public bool? Alive { get; init; }

    [JsonPropertyName("exit_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? ExitCode { get; init; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Agent { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Message { get; init; }

    [JsonPropertyName("binding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public BindingDto? Binding { get; init; }

    [JsonPropertyName("seen")]
    public bool Seen { get; init; } = true;

    [JsonPropertyName("right_click")]
    public string? RightClick { get; init; }

    [JsonPropertyName("tokens")]
    public IReadOnlyDictionary<string, string>? Tokens { get; init; }

    [JsonPropertyName("agent_session")]
    public NativeAgentSessionDto? AgentSession { get; init; }

    /// <summary>Wire <c>hidden</c> or <c>tiled</c>. Always emitted.</summary>
    [JsonPropertyName("placement")]
    public string? Placement { get; init; }

    /// <summary>True when the pane has no layout leaf. Always emitted.</summary>
    [JsonPropertyName("hidden")]
    public bool Hidden { get; init; }

    [JsonPropertyName("parent_pane_id")]
    public string? ParentPaneId { get; init; }

    [JsonPropertyName("parent_capability")]
    public string? ParentCapability { get; init; }
}

public sealed record LayoutExportResult
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("zoomed")]
    public bool Zoomed { get; init; }

    [JsonPropertyName("focused_pane_id")]
    public string? FocusedPaneId { get; init; }

    [JsonPropertyName("zoomed_pane_id")]
    public string? ZoomedPaneId { get; init; }

    [JsonPropertyName("root")]
    public LayoutNodeDto? Root { get; init; }
}

public sealed record PaneZoomResult
{
    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("zoomed")]
    public bool Zoomed { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

public sealed record PaneMoveResult
{
    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }
}

public sealed record PaneFocusDirectionResult
{
    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }
}

public sealed record PaneSwapParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("target_pane_id")]
    public string? TargetPaneId { get; init; }
}

public sealed record PaneSwapResult
{
    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("target_pane_id")]
    public string? TargetPaneId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }
}

public sealed record PaneShowParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("occupant_token")]
    public string? OccupantToken { get; init; }

    [JsonPropertyName("parent_capability")]
    public string? ParentCapability { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("target_pane_id")]
    public string? TargetPaneId { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("ratio")]
    public double? Ratio { get; init; }

    [JsonPropertyName("attach_client_id")]
    public string? AttachClientId { get; init; }

    [JsonPropertyName("seq")]
    public long? Seq { get; init; }

    [JsonPropertyName("focus")]
    public bool? Focus { get; init; }

    [JsonPropertyName("area_cols")]
    public int? AreaCols { get; init; }

    [JsonPropertyName("area_rows")]
    public int? AreaRows { get; init; }

    [JsonPropertyName("overlay_generation")]
    public long? OverlayGeneration { get; init; }
}

public sealed record PaneHideParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("occupant_token")]
    public string? OccupantToken { get; init; }

    [JsonPropertyName("parent_capability")]
    public string? ParentCapability { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("attach_client_id")]
    public string? AttachClientId { get; init; }

    [JsonPropertyName("seq")]
    public long? Seq { get; init; }

    [JsonPropertyName("overlay_generation")]
    public long? OverlayGeneration { get; init; }
}

public sealed record AgentStartResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("occupant_generation")]
    public int OccupantGeneration { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("alive")]
    public bool Alive { get; init; }

    /// <summary>Resolved occupant id when start used a manifest.</summary>
    [JsonPropertyName("occupant")]
    public string? Occupant { get; init; }

    /// <summary>Resolved native transcript root when start used a manifest.</summary>
    [JsonPropertyName("transcript_root")]
    public string? TranscriptRoot { get; init; }

    /// <summary>Isolated cube HOME used for the occupant env.</summary>
    [JsonPropertyName("cube_home")]
    public string? CubeHome { get; init; }

    /// <summary>Continuity Work id stored on the occupant record.</summary>
    [JsonPropertyName("work_id")]
    public string? WorkId { get; init; }

    /// <summary>Continuity Work generation stored on the occupant record.</summary>
    [JsonPropertyName("generation")]
    public long? Generation { get; init; }
}
