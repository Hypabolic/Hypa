using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;
using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

/// <summary>
/// Join Continuity active Run with the Placement directory mux identity.
/// This is a read. It is not an attach protocol.
/// </summary>
public interface IWorkAttachTargetResolver
{
    ValueTask<PlacementOutcome<WorkAttachTarget>> ResolveAsync(
        WorkId workId,
        CancellationToken cancellationToken = default);
}

public sealed class WorkAttachTargetResolver : IWorkAttachTargetResolver
{
    private readonly IWorkService _works;
    private readonly IPlacementDirectory _directory;

    public WorkAttachTargetResolver(IWorkService works, IPlacementDirectory directory)
    {
        _works = works ?? throw new ArgumentNullException(nameof(works));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public async ValueTask<PlacementOutcome<WorkAttachTarget>> ResolveAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        var attachRun = await _works.ResolveAttachRunAsync(workId, cancellationToken)
            .ConfigureAwait(false);
        if (!attachRun.Ok || attachRun.Run is null)
        {
            return PlacementOutcome<WorkAttachTarget>.Failure(
                attachRun.Reason ?? ContinuityReasons.Internal,
                attachRun.Detail ?? "attach target is missing");
        }

        if (!PlacementId.TryParse(attachRun.Run.PlacementId, out var placementId))
        {
            return PlacementOutcome<WorkAttachTarget>.Failure(
                PlacementReasons.PlacementNotFound,
                "active Run placement is not in the directory");
        }

        var placement = await _directory.GetAsync(placementId, cancellationToken)
            .ConfigureAwait(false);
        if (!placement.Ok || placement.Value is null)
        {
            return PlacementOutcome<WorkAttachTarget>.Failure(
                placement.Reason ?? PlacementReasons.PlacementNotFound,
                placement.Detail ?? "placement is not in the directory");
        }

        if (!MuxIdentity.TryParse(placement.Value.MuxIdentity.Value, out var muxIdentity))
        {
            return PlacementOutcome<WorkAttachTarget>.Failure(
                PlacementReasons.MuxIdentityInvalid,
                "placement mux identity is not an opaque mux_ value");
        }

        return PlacementOutcome<WorkAttachTarget>.Success(new WorkAttachTarget
        {
            WorkId = workId,
            PlacementId = placementId,
            MuxIdentity = muxIdentity,
            Generation = attachRun.Run.Generation,
            MuxEndpoint = attachRun.Run.MuxEndpoint,
        });
    }
}
