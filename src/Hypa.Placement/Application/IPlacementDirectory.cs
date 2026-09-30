using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

/// <summary>
/// Named Placement directory. Connectivity consumes this port. It does not own it.
/// </summary>
public interface IPlacementDirectory
{
    ValueTask<PlacementOutcome<PlacementRecord>> RegisterAsync(
        PlacementRegistration request,
        CancellationToken cancellationToken = default);

    ValueTask<PlacementOutcome<PlacementRecord>> SetReachabilityAsync(
        DirectoryIdentity actor,
        PlacementId placementId,
        PlacementReachability reachability,
        CancellationToken cancellationToken = default);

    ValueTask<PlacementOutcome<PlacementRecord>> SetActiveWorkAsync(
        DirectoryIdentity actor,
        PlacementId placementId,
        string? workId,
        CancellationToken cancellationToken = default);

    ValueTask<PlacementOutcome> GrantListAccessAsync(
        DirectoryIdentity actor,
        PlacementId placementId,
        DirectoryIdentity grantee,
        CancellationToken cancellationToken = default);

    ValueTask<PlacementOutcome> GrantWorkAccessAsync(
        DirectoryIdentity actor,
        DirectoryIdentity identity,
        string workId,
        CancellationToken cancellationToken = default);

    ValueTask<PlacementOutcome<IReadOnlyList<PlacementRow>>> ListAsync(
        DirectoryIdentity requester,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read one stored row by placement id. This is a directory lookup.
    /// It is not list authorization and it is not Connectivity proof.
    /// </summary>
    ValueTask<PlacementOutcome<PlacementRecord>> GetAsync(
        PlacementId placementId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read one row for Cubes Connect after directory authorization.
    /// SSH profiles require owner access. Other rows allow owner or list grant.
    /// </summary>
    ValueTask<PlacementOutcome<PlacementRecord>> GetForConnectAsync(
        DirectoryIdentity requester,
        PlacementId placementId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove one owned Placement and its grants. Host revoke is not this call.
    /// </summary>
    ValueTask<PlacementOutcome<PlacementRecord>> RemoveOwnedAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        CancellationToken cancellationToken = default);
}

public interface IPlacementDirectoryStore
{
    ValueTask<PlacementDirectorySnapshot> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(PlacementDirectorySnapshot snapshot, CancellationToken cancellationToken = default);

    ValueTask<T> MutateAsync<T>(
        Func<PlacementDirectorySnapshot, (PlacementDirectorySnapshot Next, T Result)> mutator,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hashes the bytes of the directory file. No lock and no JSON parse.
    /// A save replaces the file in one rename, so a read sees one whole
    /// version. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the file cannot be read.
    /// </summary>
    PlacementDirectoryChangeStamp ReadChangeStamp();
}

/// <summary>
/// Content identity of directory.json. Two stamps are equal only when the
/// file bytes are equal. A missing file is the default value.
/// </summary>
public readonly record struct PlacementDirectoryChangeStamp
{
    public bool Exists { get; init; }

    public long Length { get; init; }

    public string? ContentSha256 { get; init; }
}

/// <summary>Forwards <see cref="IPlacementDirectoryStore.ReadChangeStamp"/>.</summary>
public interface IPlacementDirectoryChangeStamp
{
    PlacementDirectoryChangeStamp ReadChangeStamp();
}

public sealed record PlacementDirectorySnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public int Revision { get; init; }
    public IReadOnlyList<PlacementRecord> Placements { get; init; } = [];
    public IReadOnlyList<PlacementAccessGrant> PlacementGrants { get; init; } = [];
    public IReadOnlyList<WorkAccessGrant> WorkGrants { get; init; } = [];
}
