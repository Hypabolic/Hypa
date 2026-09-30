using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>Remote WorkPack source for C-21 peer pull.</summary>
public sealed record PeerPackSource
{
    /// <summary><c>local-path</c> (same host other spool) or <c>ssh</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>SSH target <c>user@host</c> when <see cref="Kind"/> is <c>ssh</c>.</summary>
    public string? Host { get; init; }

    /// <summary>Spool directory on the peer that already holds the workpack.</summary>
    public required string RemoteSpoolDirectory { get; init; }
}

/// <summary>
/// Pull a WorkPack from a peer into a local spool and verify the sidecar (C-21).
/// Does not use rsync as the product API — SSH file copy of the pack only.
/// </summary>
public interface IWorkPackPeerPull
{
    ContinuityOutcome Pull(
        string workId,
        PeerPackSource source,
        IWorkPackSpool destSpool);
}
