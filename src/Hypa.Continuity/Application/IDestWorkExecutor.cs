using System.Text.Json.Serialization;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Destination worker. Owns pack verify, workspace apply, harness start, probe,
/// and dest Run commit. Source does not resolve dest HOME or dest cwd.
/// </summary>
public interface IDestWorkExecutor
{
    ValueTask<DestApplyResult> ApplyAsync(
        DestApplyInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>Source-side invocation. Pack path is the source spool file.</summary>
public sealed record DestApplyInvocation
{
    public required string PackPath { get; init; }
    public required DestApplyRequest Request { get; init; }
}

/// <summary>
/// Apply request bound to one attempt id. Dest HOME and dest cwd are absent.
/// Dest worker supplies those paths on the destination host.
/// </summary>
public sealed record DestApplyRequest
{
    public required string AttemptId { get; init; }
    public required string WorkId { get; init; }
    public required string DestPlacementId { get; init; }
    public string? DestPaneId { get; init; }
    public string? PackSha256 { get; init; }
    public string? HarnessAdapterId { get; init; }
    public long? PackBytes { get; init; }
}

/// <summary>
/// Apply result bound to the same attempt id. <c>ok: true</c> only after dest
/// probes pass and dest Run generation n+1 is active.
/// </summary>
public sealed record DestApplyResult
{
    public required bool Ok { get; init; }
    public required string AttemptId { get; init; }
    public long? DestGeneration { get; init; }
    [JsonConverter(typeof(ResumeEvidenceJsonConverter))]
    public ResumeEvidence DestResumeEvidence { get; init; } = ResumeEvidence.None;
    public string? ConversationId { get; init; }
    public string? DestMuxEndpoint { get; init; }
    public string? DestPlacementId { get; init; }
    public string? WorkId { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }

    public static DestApplyResult Fail(string attemptId, string reason, string detail) =>
        new()
        {
            Ok = false,
            AttemptId = attemptId,
            Reason = reason,
            Detail = detail,
            DestResumeEvidence = ResumeEvidence.None,
        };
}

/// <summary>Destination-local apply host. Source never constructs these paths.</summary>
public sealed record DestApplyHostContext
{
    public required string DestHome { get; init; }
    public required string DestWorkspace { get; init; }
    public required string DestMuxEndpoint { get; init; }
    public required IHarnessAdapter Harness { get; init; }
    public IDestOccupantStarter? DestStarter { get; init; }
    public string? DestPaneId { get; init; }
    public bool RequireDestStart { get; init; } = true;
    public TimeSpan ResumeReportTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
