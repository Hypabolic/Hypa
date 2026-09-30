using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Names an existing live pane as Work generation 1. Adopt does not move Work.
/// </summary>
public sealed class WorkAdoptService
{
    public const string LocalPlacementId = "local";

    private readonly IWorkService _works;
    private readonly ILivePaneOccupantProbe _panes;

    public WorkAdoptService(IWorkService works, ILivePaneOccupantProbe panes)
    {
        _works = works ?? throw new ArgumentNullException(nameof(works));
        _panes = panes ?? throw new ArgumentNullException(nameof(panes));
    }

    public async ValueTask<WorkAdoptResult> AdoptAsync(
        WorkAdoptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Harness);
        if (string.IsNullOrWhiteSpace(request.PaneId))
            return Fail(ContinuityReasons.Internal, "pane id is required");
        if (string.IsNullOrWhiteSpace(request.SocketPath))
            return Fail(ContinuityReasons.Internal, "mux socket is required");
        if (string.IsNullOrWhiteSpace(request.HarnessId))
            return Fail(ContinuityReasons.HarnessUncertified, "harness id is required");

        var harnessId = request.HarnessId.Trim();
        if (!CertifiedHarnessIds.Contains(harnessId)
            || !string.Equals(request.Harness.AdapterId, harnessId, StringComparison.Ordinal))
        {
            return Fail(
                ContinuityReasons.HarnessUncertified,
                "pane is not a certified harness");
        }

        var probed = await _panes.ProbeAsync(
                request.SocketPath,
                request.PaneId.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        if (!probed.Ok || probed.Occupant is null)
        {
            return Fail(
                probed.Reason ?? ContinuityReasons.Internal,
                probed.Detail ?? "live pane probe failed");
        }

        var occupant = probed.Occupant;
        if (!string.IsNullOrWhiteSpace(occupant.OccupantId)
            && (!CertifiedHarnessIds.Contains(occupant.OccupantId)
                || !string.Equals(occupant.OccupantId, harnessId, StringComparison.Ordinal)))
        {
            return Fail(
                ContinuityReasons.HarnessUncertified,
                "pane is not a certified harness");
        }

        if (!occupant.Alive)
        {
            return Fail(
                ContinuityReasons.ResumeUnproven,
                "occupant is not alive");
        }

        if (!CertifiedHarnessIds.Contains(occupant.OccupantId)
            || !string.Equals(occupant.OccupantId, harnessId, StringComparison.Ordinal))
        {
            return Fail(
                ContinuityReasons.HarnessUncertified,
                "pane is not a certified harness");
        }

        if (string.IsNullOrWhiteSpace(occupant.Home) || string.IsNullOrWhiteSpace(occupant.Cwd))
        {
            return Fail(
                ContinuityReasons.Internal,
                "live pane home or cwd is unreadable");
        }

        var report = await request.Harness.ProbeLiveOccupantAsync(
                occupant,
                request.ReportTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!report.Ok || string.IsNullOrWhiteSpace(report.ConversationId))
        {
            var reason = report.Reason ?? ContinuityReasons.ResumeUnproven;
            if (string.Equals(reason, ContinuityReasons.ConversationMismatch, StringComparison.Ordinal))
                reason = ContinuityReasons.ResumeUnproven;
            return Fail(
                reason,
                report.Detail ?? "live occupant did not report a conversation id",
                report.Evidence);
        }

        if (report.Evidence < ResumeEvidence.HarnessReported)
        {
            return Fail(
                ContinuityReasons.ResumeUnproven,
                $"evidence {report.Evidence} is below {ResumeEvidence.HarnessReported}",
                report.Evidence);
        }

        var work = await _works.CreateWorkAsync(
                harnessId,
                LocalPlacementId,
                NormalizeMuxEndpoint(request.SocketPath),
                report.Evidence,
                occupant.Home,
                occupant.Cwd,
                cancellationToken)
            .ConfigureAwait(false);
        var run = await _works.GetActiveRunAsync(work.Id, cancellationToken).ConfigureAwait(false);
        if (run is null)
            return Fail(ContinuityReasons.Internal, "adopt stored Work without an active Run");

        return new WorkAdoptResult
        {
            Ok = true,
            Work = work,
            Run = run,
            ConversationId = report.ConversationId,
            ResumeEvidence = report.Evidence,
            Home = occupant.Home,
            Cwd = occupant.Cwd,
        };
    }

    private static string NormalizeMuxEndpoint(string socketPath)
    {
        var trimmed = socketPath.Trim();
        if (trimmed.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return "unix:" + Path.GetFullPath(trimmed);
    }

    private static WorkAdoptResult Fail(
        string reason,
        string detail,
        ResumeEvidence evidence = ResumeEvidence.None) =>
        new()
        {
            Ok = false,
            Reason = reason,
            Detail = detail,
            ResumeEvidence = evidence,
        };
}

public sealed record WorkAdoptRequest
{
    public required string PaneId { get; init; }
    public required string SocketPath { get; init; }
    public required string HarnessId { get; init; }
    public required IHarnessAdapter Harness { get; init; }
    public TimeSpan ReportTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record WorkAdoptResult
{
    public required bool Ok { get; init; }
    public WorkRecord? Work { get; init; }
    public RunRecord? Run { get; init; }
    public string? ConversationId { get; init; }
    public ResumeEvidence ResumeEvidence { get; init; }
    public string? Home { get; init; }
    public string? Cwd { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }

    /// <summary>
    /// Convert a failed adopt. Adopt <c>ok: true</c> is not spec section 0
    /// handoff success.
    /// </summary>
    public ContinuityOutcome ToOutcome()
    {
        if (Ok)
        {
            throw new InvalidOperationException(
                "adopt ok is not spec section 0 handoff success");
        }

        return ContinuityOutcome.Failure(
            Reason ?? ContinuityReasons.Internal,
            Detail ?? "");
    }
}
