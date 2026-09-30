using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;
using Hypa.ControlPlane;

namespace Hypa.Placement;

/// <summary>
/// Best-effort source occupant stop via <c>pane.close</c> after the fence (spec §4.2).
/// </summary>
public sealed class ProtocolSourceOccupantStopper : ISourceOccupantStopper
{
    public async ValueTask<ContinuityOutcome> StopAsync(
        SourceOccupantStopRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SocketPath))
            return ContinuityOutcome.Failure(ContinuityReasons.Internal, "source socket empty");
        if (string.IsNullOrWhiteSpace(request.PaneId))
            return ContinuityOutcome.Failure(ContinuityReasons.Internal, "source pane_id empty");

        try
        {
            await using var client = new ControlPlaneClient(request.SocketPath);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var close = new JsonObject { ["pane_id"] = request.PaneId };
            if (!string.IsNullOrWhiteSpace(request.WorkId) && request.Generation is > 0)
            {
                close["work_id"] = request.WorkId;
                close["generation"] = request.Generation.Value;
            }

            _ = await client.CallAsync(
                    ProtocolMethods.PaneClose,
                    close,
                    cancellationToken)
                .ConfigureAwait(false);
            return ContinuityOutcome.Success();
        }
        catch (OperationCanceledException)
        {
            // Best-effort after fence: never throw into HandoffService success path.
            return ContinuityOutcome.Failure(
                ContinuityReasons.SourceStillActive,
                "source stop cancelled");
        }
        catch (Exception ex)
        {
            // Best-effort: fence already committed; report but do not undo.
            return ContinuityOutcome.Failure(ContinuityReasons.SourceStillActive, ex.Message);
        }
    }
}
