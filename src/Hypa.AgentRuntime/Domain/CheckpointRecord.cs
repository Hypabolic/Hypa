namespace Hypa.AgentRuntime.Domain;

/// <summary>Wire / store state tokens for a checkpoint row.</summary>
public static class CheckpointStates
{
    public const string Prepared = "prepared";
    public const string Exported = "exported";
    public const string Failed = "failed";
    public const string Conflict = "conflict";

    /// <summary>Prepare cancelled / session unfrozen without successful export.</summary>
    public const string Aborted = "aborted";

    /// <summary>
    /// Legal durable transitions. Terminal rows stay put except an idempotent same-state write.
    /// Aborted or conflict never become exported.
    /// </summary>
    public static bool CanTransition(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return false;
        if (string.Equals(from, to, StringComparison.Ordinal))
            return true;
        if (string.Equals(from, Prepared, StringComparison.Ordinal))
        {
            return to is Exported or Aborted or Conflict or Failed;
        }

        if (string.Equals(from, Failed, StringComparison.Ordinal))
        {
            return to is Exported or Aborted or Conflict;
        }

        return false;
    }
}

/// <summary>Artifact kind tokens written into the transfer manifest.</summary>
public static class CheckpointArtifactKinds
{
    public const string Metadata = "metadata";
    public const string Scrollback = "scrollback";
    public const string WorkspaceFile = "workspace_file";
    public const string GitMeta = "git_meta";
}

/// <summary>
/// Durable checkpoint prepare/export record. Local P0 reattach does not require rows.
/// </summary>
public sealed record CheckpointRecord
{
    public required string CheckpointId { get; init; }
    public required string SessionId { get; init; }
    public required string State { get; init; }
    public required long BarrierSeq { get; init; }
    public required long NextSeqAtPrepare { get; init; }
    public required string SessionFingerprint { get; init; }
    public string? Reason { get; init; }
    public bool IncludeScrollback { get; init; }
    public bool IncludeWorkspaceFiles { get; init; } = true;
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExportedAt { get; init; }
    public string? ManifestRelativePath { get; init; }
    public string? ManifestSha256 { get; init; }
    public long? ByteCount { get; init; }
    public bool TransferIncomplete { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int PlacementGeneration { get; init; }
    public string Placement { get; init; } = "local";
    public bool ReplayComplete { get; init; } = true;

    /// <summary>Device id of project_root at prepare (nofollow pin).</summary>
    public ulong? ProjectRootDevice { get; init; }

    /// <summary>Inode of project_root at prepare (target inode when the root was a symlink).</summary>
    public ulong? ProjectRootInode { get; init; }

    /// <summary>True when project_root was an operator symlink at prepare.</summary>
    public bool? ProjectRootWasSymlink { get; init; }

    /// <summary>
    /// True when prepare recorded a complete project_root pin. Walk and git require this.
    /// </summary>
    public bool HasProjectRootPin =>
        ProjectRootDevice is not null
        && ProjectRootInode is not null
        && ProjectRootWasSymlink is not null;
}
