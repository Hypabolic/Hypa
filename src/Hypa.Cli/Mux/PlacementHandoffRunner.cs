using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;

namespace Hypa.Cli.Mux;

/// <summary>
/// Shared <c>hypa work handoff --to &lt;placement-id&gt;</c> path. Menu Move Work
/// calls this runner.
/// </summary>
public interface IPlacementHandoffRunner
{
    ValueTask<HandoffResult> RunAsync(
        PlacementHandoffRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PlacementHandoffRequest
{
    public required WorkId WorkId { get; init; }
    public required IHarnessAdapter Harness { get; init; }
    public required string SourceHome { get; init; }
    public required string SourceWorkspace { get; init; }
    public required string SourceMuxEndpoint { get; init; }
    public required PlacementId DestPlacementId { get; init; }
    public required IDestWorkExecutor DestExecutor { get; init; }
    public required IHandoffService Handoff { get; init; }
    public required IPlacementDirectory Directory { get; init; }
    public string SourcePlacementId { get; init; } = "src";
    public string? SourcePaneId { get; init; }
    public ISourceOccupantStopper? SourceStopper { get; init; }
    public bool OccupantStreaming { get; init; }
    public bool RequireDestStart { get; init; }
    public string? AttemptId { get; init; }
}

public sealed class PlacementHandoffRunner : IPlacementHandoffRunner
{
    public async ValueTask<HandoffResult> RunAsync(
        PlacementHandoffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Harness);
        ArgumentNullException.ThrowIfNull(request.DestExecutor);
        ArgumentNullException.ThrowIfNull(request.Handoff);
        ArgumentNullException.ThrowIfNull(request.Directory);

        var resolved = await new DestPlacementResolver(request.Directory)
            .ResolveAsync(request.DestPlacementId, cancellationToken)
            .ConfigureAwait(false);
        if (!resolved.Ok || resolved.Value is null)
        {
            return new HandoffResult
            {
                Ok = false,
                State = HandoffState.Failed,
                FailedAt = HandoffState.Quiescing,
                Reason = resolved.Reason ?? ContinuityReasons.PeerUnavailable,
                Detail = resolved.Detail ?? "placement is not in the directory",
            };
        }

        return await request.Handoff.RunAsync(
                new HandoffRequest
                {
                    WorkId = request.WorkId,
                    Harness = request.Harness,
                    SourceHome = Path.GetFullPath(request.SourceHome),
                    SourceWorkspace = Path.GetFullPath(request.SourceWorkspace),
                    SourceMuxEndpoint = request.SourceMuxEndpoint,
                    SourcePlacementId = request.SourcePlacementId,
                    DestHome = "",
                    DestWorkspace = "",
                    DestMuxEndpoint = resolved.Value.OpaqueMuxEndpoint,
                    DestPlacementId = resolved.Value.PlacementId.Value,
                    DestExecutor = request.DestExecutor,
                    SourcePaneId = request.SourcePaneId,
                    SourceStopper = request.SourceStopper,
                    OccupantStreaming = request.OccupantStreaming,
                    RequireDestStart = request.RequireDestStart,
                    AttemptId = request.AttemptId,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
