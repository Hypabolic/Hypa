using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Reads a live mux pane without Continuity speaking NDJSON.
/// Placement implements the protocol client.
/// </summary>
public interface ILivePaneOccupantProbe
{
    ValueTask<LivePaneProbeResult> ProbeAsync(
        string socketPath,
        string paneId,
        CancellationToken cancellationToken = default);
}

/// <summary>Live pane occupant snapshot used by adopt.</summary>
public sealed record LivePaneOccupant
{
    public required string PaneId { get; init; }
    public required string OccupantId { get; init; }
    public string Home { get; init; } = "";
    public string Cwd { get; init; } = "";
    public required bool Alive { get; init; }
    public IDestOccupantLiveness? Liveness { get; init; }
    public HarnessStartArgs? ResumeStartArgs { get; init; }
}

/// <summary>Result of a live pane probe. Missing panes fail closed.</summary>
public sealed record LivePaneProbeResult
{
    public required bool Ok { get; init; }
    public LivePaneOccupant? Occupant { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }

    public static LivePaneProbeResult Found(LivePaneOccupant occupant) =>
        new() { Ok = true, Occupant = occupant };

    public static LivePaneProbeResult Fail(string reason, string detail) =>
        new() { Ok = false, Reason = reason, Detail = detail };

    public ContinuityOutcome ToOutcome() =>
        Ok
            ? ContinuityOutcome.Success()
            : ContinuityOutcome.Failure(
                Reason ?? ContinuityReasons.Internal,
                Detail ?? "");
}
