using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

public sealed record SshPlacementAddRequest
{
    public required DirectoryIdentity Owner { get; init; }
    public required string Label { get; init; }
    public required string Target { get; init; }
    public required string Session { get; init; }
}

public interface ISshPlacementCatalog
{
    ValueTask<PlacementOutcome<PlacementRecord>> AddAsync(
        SshPlacementAddRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PlacementOutcome<PlacementRecord>> RenameAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        string label,
        CancellationToken cancellationToken = default);

    ValueTask<PlacementOutcome<PlacementRecord>> RemoveAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        CancellationToken cancellationToken = default);

    ValueTask<PlacementOutcome<PlacementRecord>> EnableAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        CancellationToken cancellationToken = default);

    ValueTask<PlacementOutcome<PlacementRecord>> DisableAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        CancellationToken cancellationToken = default);
}

public interface ISshPlacementPreparation
{
    ValueTask<PlacementOutcome<MuxIdentity>> PrepareAsync(
        PeerProfile profile,
        CancellationToken cancellationToken = default);
}
