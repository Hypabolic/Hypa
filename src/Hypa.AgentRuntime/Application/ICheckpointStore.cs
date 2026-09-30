using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Persist checkpoint prepare/export records and materialize on-disk checkpoint trees.
/// </summary>
public interface ICheckpointStore
{
    /// <summary>Insert or replace a prepared/exported checkpoint row and ensure directory.</summary>
    Task<RuntimeResult<CheckpointRecord>> SaveAsync(CheckpointRecord record, CancellationToken ct = default);

    /// <summary>Lookup by checkpoint_id, or null when absent.</summary>
    Task<RuntimeResult<CheckpointRecord?>> GetAsync(string checkpointId, CancellationToken ct = default);

    /// <summary>All durable rows for a runtime session (sidecar preferred, SQLite fallback).</summary>
    Task<RuntimeResult<IReadOnlyList<CheckpointRecord>>> ListBySessionAsync(
        string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Write <c>manifest.json</c> under the checkpoint directory and return relative path + digest.
    /// </summary>
    Task<RuntimeResult<CheckpointManifestWriteResult>> WriteManifestAsync(
        string checkpointId,
        string manifestJson,
        CancellationToken ct = default);

    /// <summary>Absolute directory for <c>checkpoints/{id}/</c> under the state root.</summary>
    string GetCheckpointDirectory(string checkpointId);

    /// <summary>Relative path of the checkpoint directory from the state root (posix).</summary>
    string GetCheckpointRelativeDirectory(string checkpointId);
}

/// <summary>Result of writing <c>manifest.json</c>.</summary>
public sealed record CheckpointManifestWriteResult
{
    /// <summary>Path relative to the runtime state root (posix separators).</summary>
    public required string ManifestRelativePath { get; init; }

    public required string ManifestSha256 { get; init; }

    public required long ByteCount { get; init; }
}
