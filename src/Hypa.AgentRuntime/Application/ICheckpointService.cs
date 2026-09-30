using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Application orchestration for G1 checkpoint prepare bookkeeping and export materialization.
/// Control plane owns freeze gates and journal emit; this service owns store + artifact strategy.
/// </summary>
public interface ICheckpointService
{
    /// <summary>Persist a prepared checkpoint record and ensure on-disk directory.</summary>
    Task<RuntimeResult<CheckpointRecord>> SavePreparedAsync(
        CheckpointRecord record,
        CancellationToken ct = default);

    Task<RuntimeResult<CheckpointRecord?>> GetAsync(string checkpointId, CancellationToken ct = default);

    /// <summary>All durable rows for a runtime session.</summary>
    Task<RuntimeResult<IReadOnlyList<CheckpointRecord>>> ListBySessionAsync(
        string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Pin project_root device/inode at prepare. Operator symlink roots pin the target.
    /// </summary>
    bool TryPinProjectRoot(
        string? projectRoot,
        out ulong device,
        out ulong inode,
        out bool wasSymlink);

    /// <summary>
    /// Build artifacts and write manifest files. Does not change durable checkpoint state.
    /// </summary>
    Task<RuntimeResult<CheckpointExportMaterial>> BuildExportAsync(
        CheckpointRecord prepared,
        SessionState session,
        CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap prepared → exported. Idempotent when the row is already exported.
    /// Fails when the row is aborted, conflict, or otherwise not prepared.
    /// </summary>
    Task<RuntimeResult<CheckpointRecord>> CommitExportedAsync(
        CheckpointExportMaterial material,
        CancellationToken ct = default);

    /// <summary>
    /// Build artifacts, write manifest, then CAS-mark the record exported.
    /// Caller must have already verified session fingerprint (or accept conflict handling).
    /// </summary>
    Task<RuntimeResult<CheckpointExportMaterial>> ExportAsync(
        CheckpointRecord prepared,
        SessionState session,
        CancellationToken ct = default);
}

/// <summary>Export materialization outcome for the control-plane wire result.</summary>
public sealed record CheckpointExportMaterial
{
    public required CheckpointRecord Record { get; init; }
    public required string ManifestRelativePath { get; init; }
    public required string ManifestSha256 { get; init; }
    public required long ByteCount { get; init; }
    public bool TransferIncomplete { get; init; }
}
