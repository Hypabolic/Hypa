using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>Source-generated checkpoint workspace metadata artifact (artifacts/metadata.json).</summary>
public sealed record CheckpointMetadataArtifactDto
{
    [JsonPropertyName("checkpoint_id")]
    public required string CheckpointId { get; init; }

    [JsonPropertyName("runtime_session_id")]
    public required string RuntimeSessionId { get; init; }

    [JsonPropertyName("session_name")]
    public string? SessionName { get; init; }

    [JsonPropertyName("placement")]
    public string? Placement { get; init; }

    [JsonPropertyName("placement_generation")]
    public int PlacementGeneration { get; init; }

    [JsonPropertyName("session_fingerprint")]
    public required string SessionFingerprint { get; init; }

    [JsonPropertyName("barrier_seq")]
    public long BarrierSeq { get; init; }

    [JsonPropertyName("binding")]
    public AtomicBindingStorageDto? Binding { get; init; }

    [JsonPropertyName("governed")]
    public bool Governed { get; init; }

    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("replay_complete")]
    public bool ReplayComplete { get; init; }

    [JsonPropertyName("workspaces")]
    public required IReadOnlyList<CheckpointWorkspaceArtifactDto> Workspaces { get; init; }

    [JsonPropertyName("panes")]
    public required IReadOnlyList<CheckpointPaneArtifactDto> Panes { get; init; }
}

/// <summary>Workspace row in the checkpoint metadata artifact.</summary>
public sealed record CheckpointWorkspaceArtifactDto
{
    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("binding")]
    public AtomicBindingStorageDto? Binding { get; init; }
}

/// <summary>Pane row in the checkpoint metadata artifact. Never includes env or credentials.</summary>
public sealed record CheckpointPaneArtifactDto
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("tab_id")]
    public required string TabId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("lifecycle_state")]
    public string? LifecycleState { get; init; }

    [JsonPropertyName("occupant_generation")]
    public int OccupantGeneration { get; init; }

    [JsonPropertyName("binding")]
    public AtomicBindingStorageDto? Binding { get; init; }
}

/// <summary>Source-generated git metadata artifact (artifacts/git_meta.json).</summary>
public sealed record CheckpointGitMetaArtifactDto
{
    [JsonPropertyName("head")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Head { get; init; }

    [JsonPropertyName("dirty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? Dirty { get; init; }

    [JsonPropertyName("incomplete")]
    public bool Incomplete { get; init; }
}
