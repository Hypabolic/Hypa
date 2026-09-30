using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>runtime.checkpoint.prepare</c> params.</summary>
public sealed record CheckpointPrepareParams
{
    /// <summary>Opaque operator reason. Reject CR/LF.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>When true, export may include bounded scrollback text files.</summary>
    [JsonPropertyName("include_scrollback")]
    public bool? IncludeScrollback { get; init; }

    /// <summary>
    /// When true (default when project_root present), export may walk workspace files.
    /// </summary>
    [JsonPropertyName("include_workspace_files")]
    public bool? IncludeWorkspaceFiles { get; init; }
}

/// <summary><c>runtime.checkpoint.prepare</c> result.</summary>
public sealed record CheckpointPrepareResult
{
    [JsonPropertyName("checkpoint_id")]
    public required string CheckpointId { get; init; }

    /// <summary>Wire state token; prepare always returns <c>prepared</c>.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>Last durable journal seq at the quiescent barrier (0 when journal empty).</summary>
    [JsonPropertyName("barrier_seq")]
    public long BarrierSeq { get; init; }

    [JsonPropertyName("runtime_session_id")]
    public required string RuntimeSessionId { get; init; }

    /// <summary>Session lifecycle after prepare — always <c>frozen_read_only</c>.</summary>
    [JsonPropertyName("session_state")]
    public required string SessionState { get; init; }

    [JsonPropertyName("placement_generation")]
    public int PlacementGeneration { get; init; }
}

/// <summary><c>runtime.checkpoint.export</c> params.</summary>
public sealed record CheckpointExportParams
{
    [JsonPropertyName("checkpoint_id")]
    public required string CheckpointId { get; init; }
}

/// <summary><c>runtime.checkpoint.abort</c> params.</summary>
public sealed record CheckpointAbortParams
{
    /// <summary>
    /// Optional prepared checkpoint to mark aborted. When omitted, unfreeze the
    /// session and demote every non-exported row for the session.
    /// </summary>
    [JsonPropertyName("checkpoint_id")]
    public string? CheckpointId { get; init; }

    /// <summary>Opaque operator reason. Reject CR/LF.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary><c>runtime.checkpoint.abort</c> result.</summary>
public sealed record CheckpointAbortResult
{
    [JsonPropertyName("checkpoint_id")]
    public string? CheckpointId { get; init; }

    /// <summary>
    /// Durable checkpoint state after the call. Returns <c>aborted</c> only when the
    /// row was demoted (prepared/conflict/failed → aborted). Already-<c>exported</c>
    /// rows stay <c>exported</c> (session unfreeze only; Atomic pull handle preserved).
    /// Session-only unfreeze (no checkpoint_id) demotes non-exported rows and
    /// returns <c>aborted</c> when any row demoted.
    /// </summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>Session lifecycle after abort — always <c>ready</c>.</summary>
    [JsonPropertyName("session_state")]
    public required string SessionState { get; init; }

    [JsonPropertyName("runtime_session_id")]
    public required string RuntimeSessionId { get; init; }
}

/// <summary><c>runtime.checkpoint.export</c> result.</summary>
public sealed record CheckpointExportResult
{
    [JsonPropertyName("checkpoint_id")]
    public required string CheckpointId { get; init; }

    /// <summary>Wire state token; export success returns <c>exported</c>.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("barrier_seq")]
    public long BarrierSeq { get; init; }

    /// <summary>Manifest path relative to the runtime state root.</summary>
    [JsonPropertyName("manifest_path")]
    public required string ManifestPath { get; init; }

    /// <summary>Lowercase hex SHA-256 of the manifest bytes.</summary>
    [JsonPropertyName("manifest_sha256")]
    public required string ManifestSha256 { get; init; }

    [JsonPropertyName("byte_count")]
    public long ByteCount { get; init; }

    /// <summary>
    /// True when workspace walk or optional probes could not fully complete.
    /// Prefer this flag over hard-fail for partial workspace transfer.
    /// </summary>
    [JsonPropertyName("transfer_incomplete")]
    public bool TransferIncomplete { get; init; }
}

/// <summary>
/// Normative transfer manifest under <c>checkpoints/{id}/manifest.json</c>.
/// Atomic fakes verify each artifact digests against this document.
/// </summary>
public sealed record CheckpointManifestDto
{
    [JsonPropertyName("checkpoint_id")]
    public required string CheckpointId { get; init; }

    [JsonPropertyName("runtime_session_id")]
    public required string RuntimeSessionId { get; init; }

    [JsonPropertyName("barrier_seq")]
    public long BarrierSeq { get; init; }

    [JsonPropertyName("created_at")]
    public required string CreatedAt { get; init; }

    [JsonPropertyName("protocol_major")]
    public int ProtocolMajor { get; init; }

    [JsonPropertyName("protocol_minor")]
    public int ProtocolMinor { get; init; }

    [JsonPropertyName("persistence_schema")]
    public int PersistenceSchema { get; init; }

    [JsonPropertyName("placement")]
    public string? Placement { get; init; }

    [JsonPropertyName("placement_generation")]
    public int PlacementGeneration { get; init; }

    [JsonPropertyName("binding")]
    public BindingDto? Binding { get; init; }

    [JsonPropertyName("limits")]
    public CheckpointLimitsDto? Limits { get; init; }

    [JsonPropertyName("excludes")]
    public IReadOnlyList<string>? Excludes { get; init; }

    [JsonPropertyName("journal")]
    public CheckpointJournalDto? Journal { get; init; }

    [JsonPropertyName("session_fingerprint")]
    public required string SessionFingerprint { get; init; }

    [JsonPropertyName("artifacts")]
    public required IReadOnlyList<CheckpointArtifactEntryDto> Artifacts { get; init; }

    [JsonPropertyName("transfer_incomplete")]
    public bool TransferIncomplete { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string>? Warnings { get; init; }
}

public sealed record CheckpointLimitsDto
{
    [JsonPropertyName("max_workspace_bytes")]
    public long MaxWorkspaceBytes { get; init; }

    [JsonPropertyName("max_file_bytes")]
    public long MaxFileBytes { get; init; }
}

public sealed record CheckpointJournalDto
{
    [JsonPropertyName("barrier_seq")]
    public long BarrierSeq { get; init; }

    [JsonPropertyName("next_seq_at_prepare")]
    public long NextSeqAtPrepare { get; init; }

    [JsonPropertyName("replay_complete")]
    public bool ReplayComplete { get; init; }
}

public sealed record CheckpointArtifactEntryDto
{
    /// <summary>Path relative to the checkpoint directory (posix separators).</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("byte_count")]
    public long ByteCount { get; init; }

    /// <summary>One of: metadata | scrollback | workspace_file | git_meta.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
}
