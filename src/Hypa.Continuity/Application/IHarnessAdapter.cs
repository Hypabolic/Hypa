using System.Text.Json.Serialization;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Port for one coding-agent harness (C-00). Lives in Continuity — not in the mux.
/// </summary>
public interface IHarnessAdapter
{
    /// <summary>Stable harness id (for example <c>pi</c> or <c>fake</c>).</summary>
    string AdapterId { get; }

    /// <summary>Version string from home, or fail. Dest must match the packed envelope.</summary>
    ContinuityOutcome ReadVersion(string home, out string? version);

    /// <summary>Block until safe to pack, or fail <c>not_quiescent</c>.</summary>
    ContinuityOutcome Quiesce(HarnessRunContext run, TimeSpan timeout);

    /// <summary>
    /// Copy adapter-declared files for one conversation into <c>harness/store/</c>.
    /// A WorkPack is not safe to share. Transcript bodies may still hold secrets.
    /// </summary>
    ContinuityOutcome CaptureStore(
        string home,
        string destStoreRoot,
        string sourceCwd,
        string conversationId);

    /// <summary>
    /// Write packed conversation paths into dest home, remapped for
    /// <paramref name="destCwd"/>. Run a non-mutating preflight before any dest write.
    /// Do not recursive-delete the dest store.
    /// Strip locks from files this restore wrote.
    /// A WorkPack is not safe to share. Transcript bodies may still hold secrets.
    /// </summary>
    ContinuityOutcome RestoreStore(
        string destHome,
        string sourceStoreRoot,
        string destCwd,
        string conversationId);

    /// <summary>
    /// Argv + env for <c>agent.start</c>. Fails when the dest session file is missing
    /// (do not invent a new-session <c>--session-id</c> success path).
    /// </summary>
    ContinuityOutcome BuildStartArgs(
        string cwd,
        string home,
        string conversationId,
        out HarnessStartArgs? args);

    /// <summary>
    /// Native conversation id under the dest workspace session dir (not arbitrary HOME files).
    /// Returns the evidence rung. File copy alone is not <see cref="ResumeEvidence.HarnessReported"/>.
    /// </summary>
    ResumeProbeResult ProbeConversationId(string home, string cwd);

    /// <summary>
    /// Wait after dest <c>agent.start</c> for a running harness report.
    /// Default uses <see cref="ProbeConversationId"/>. File copy stays weak.
    /// </summary>
    ValueTask<ResumeProbeResult> WaitForHarnessReportAsync(
        string home,
        string cwd,
        HarnessStartArgs startArgs,
        TimeSpan timeout,
        IDestOccupantLiveness? liveness = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ProbeConversationId(home, cwd));

    /// <summary>
    /// Probe a live occupant. File copy stays below <see cref="ResumeEvidence.HarnessReported"/>.
    /// </summary>
    async ValueTask<ResumeProbeResult> ProbeLiveOccupantAsync(
        LivePaneOccupant occupant,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occupant);
        if (occupant.Liveness is null)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "occupant process was not probed");
        }

        var alive = await occupant.Liveness.IsAliveAsync(cancellationToken).ConfigureAwait(false);
        if (!alive)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "occupant is not alive");
        }

        if (occupant.ResumeStartArgs is not null)
        {
            return await WaitForHarnessReportAsync(
                    occupant.Home,
                    occupant.Cwd,
                    occupant.ResumeStartArgs,
                    timeout,
                    occupant.Liveness,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var probe = ProbeConversationId(occupant.Home, occupant.Cwd);
        if (!probe.Ok)
            return probe;
        if (probe.Evidence < ResumeEvidence.HarnessReported)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                $"evidence {probe.Evidence} is below {ResumeEvidence.HarnessReported}",
                probe.Evidence);
        }

        return probe;
    }
}

/// <summary>Probe result with an explicit resume evidence class (C-30).</summary>
public sealed record ResumeProbeResult
{
    public required bool Ok { get; init; }
    public string? ConversationId { get; init; }
    [JsonConverter(typeof(ResumeEvidenceJsonConverter))]
    public required ResumeEvidence Evidence { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }

    public static ResumeProbeResult Found(string conversationId, ResumeEvidence evidence, string? detail = null) =>
        new()
        {
            Ok = true,
            ConversationId = conversationId,
            Evidence = evidence,
            Detail = detail,
        };

    public static ResumeProbeResult Fail(
        string reason,
        string detail,
        ResumeEvidence evidence = ResumeEvidence.None) =>
        new()
        {
            Ok = false,
            Evidence = evidence,
            Reason = reason,
            Detail = detail,
        };

    public ContinuityOutcome ToOutcome() =>
        Ok
            ? ContinuityOutcome.Success()
            : ContinuityOutcome.Failure(
                Reason ?? ContinuityReasons.Internal,
                Detail ?? "");
}

/// <summary>Live Run context for quiesce.</summary>
public sealed record HarnessRunContext
{
    public required string Home { get; init; }
    public required string Cwd { get; init; }
    public required string ConversationId { get; init; }
    public bool OccupantStreaming { get; init; }
}

/// <summary>Dest start argv/env from the adapter.</summary>
public sealed record HarnessStartArgs
{
    public required IReadOnlyList<string> Argv { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
}
