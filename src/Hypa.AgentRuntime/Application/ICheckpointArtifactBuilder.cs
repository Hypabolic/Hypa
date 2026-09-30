using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Strategy that materializes hashed checkpoint artifacts under a checkpoint directory.
/// Always writes metadata; optional scrollback / workspace / git probes.
/// </summary>
public interface ICheckpointArtifactBuilder
{
    Task<RuntimeResult<CheckpointArtifactBuildResult>> BuildAsync(
        CheckpointArtifactBuildRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Pin project_root device/inode without following a real directory.
    /// Operator symlink roots pin the resolved target directory.
    /// </summary>
    bool TryPinProjectRoot(
        string? projectRoot,
        out ulong device,
        out ulong inode,
        out bool wasSymlink);
}

/// <summary>Inputs for artifact materialization.</summary>
public sealed record CheckpointArtifactBuildRequest
{
    public required CheckpointRecord Record { get; init; }
    public required SessionState Session { get; init; }

    /// <summary>Absolute path of <c>checkpoints/{id}/</c>.</summary>
    public required string CheckpointDirectory { get; init; }

    public long MaxWorkspaceBytes { get; init; } = CheckpointBudgets.MaxWorkspaceBytes;
    public long MaxFileBytes { get; init; } = CheckpointBudgets.MaxFileBytes;
}

/// <summary>Design §12 budgets for checkpoint workspace transfer.</summary>
public static class CheckpointBudgets
{
    public const long MaxWorkspaceBytes = 2L * 1024 * 1024 * 1024; // 2 GiB
    public const long MaxFileBytes = 100L * 1024 * 1024; // 100 MiB
}

/// <summary>Built artifact entries + transfer flags (paths relative to checkpoint dir).</summary>
public sealed record CheckpointArtifactBuildResult
{
    public required IReadOnlyList<CheckpointArtifactEntry> Artifacts { get; init; }
    public bool TransferIncomplete { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public long TotalArtifactBytes { get; init; }
}

/// <summary>One hashed file under the checkpoint tree.</summary>
public sealed record CheckpointArtifactEntry
{
    /// <summary>Path relative to the checkpoint directory (posix separators).</summary>
    public required string RelativePath { get; init; }

    public required string Sha256 { get; init; }
    public required long ByteCount { get; init; }
    public required string Kind { get; init; }
}
