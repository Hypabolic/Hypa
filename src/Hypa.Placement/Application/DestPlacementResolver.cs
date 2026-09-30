using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

/// <summary>
/// Resolve a dest Placement id. This lookup is not a dest filesystem path
/// and it is not an SSH URL.
/// </summary>
public sealed class DestPlacementResolver
{
    private readonly IPlacementDirectory _directory;

    public DestPlacementResolver(IPlacementDirectory directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public async ValueTask<PlacementOutcome<DestPlacementTarget>> ResolveAsync(
        PlacementId placementId,
        CancellationToken cancellationToken = default)
    {
        var found = await _directory.GetAsync(placementId, cancellationToken).ConfigureAwait(false);
        if (!found.Ok || found.Value is null)
        {
            return PlacementOutcome<DestPlacementTarget>.Failure(
                found.Reason ?? PlacementReasons.PlacementNotFound,
                found.Detail ?? "placement is not in the directory");
        }

        var row = found.Value;
        if (row.Reachability is PlacementReachability.Unreachable or PlacementReachability.Asleep)
        {
            return PlacementOutcome<DestPlacementTarget>.Failure(
                PlacementReasons.ReachabilityInvalid,
                "destination placement is not reachable");
        }

        return PlacementOutcome<DestPlacementTarget>.Success(
            new DestPlacementTarget
            {
                PlacementId = row.Id,
                MuxIdentity = row.MuxIdentity,
                Kind = row.Kind,
                Reachability = row.Reachability,
            });
    }
}

/// <summary>
/// Dest Placement for handoff. Mux identity is opaque. Dest HOME is absent.
/// </summary>
public sealed record DestPlacementTarget
{
    public required PlacementId PlacementId { get; init; }
    public required MuxIdentity MuxIdentity { get; init; }
    public required PlacementDirectoryKind Kind { get; init; }
    public required PlacementReachability Reachability { get; init; }

    public string OpaqueMuxEndpoint => "mux:" + MuxIdentity.Value;
}
