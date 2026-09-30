using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Durable Continuity store (SQLite). Not mux HYJR.
/// </summary>
public interface IContinuityStore
{
    ValueTask EnsureCreatedAsync(CancellationToken cancellationToken = default);

    ValueTask<WorkRecord?> GetWorkAsync(WorkId workId, CancellationToken cancellationToken = default);

    ValueTask<RunRecord?> GetRunAsync(
        WorkId workId,
        long generation,
        CancellationToken cancellationToken = default);

    ValueTask<RunRecord?> GetActiveRunAsync(
        WorkId workId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RunRecord>> ListRunsAsync(
        WorkId workId,
        CancellationToken cancellationToken = default);

    /// <summary>List every active Run with its Work. Used to find the adopted occupant.</summary>
    ValueTask<IReadOnlyList<ActiveWorkRun>> ListActiveWorksAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert Work and its first active Run in one transaction.
    /// </summary>
    ValueTask SaveNewWorkAsync(
        WorkRecord work,
        RunRecord firstRun,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert Work from a pack when dest has no row. Dest apply does not
    /// copy the source SQLite file.
    /// </summary>
    ValueTask SaveImportedWorkAsync(
        WorkRecord work,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert dest Run generation n+1 as active when dest has no source Run.
    /// </summary>
    ValueTask InsertActiveRunAsync(
        RunRecord destRun,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomic generation flip: dest becomes active at n+1; source becomes dead.
    /// Enforces at most one active Run.
    /// </summary>
    ValueTask FlipGenerationAsync(
        WorkId workId,
        long sourceGeneration,
        RunRecord destRun,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Crash recovery: if a newer active Run exists, mark older active Runs dead.
    /// Dest row wins.
    /// </summary>
    ValueTask RecoverActiveInvariantAsync(
        WorkId workId,
        CancellationToken cancellationToken = default);

    ValueTask UpdateRunStatusAsync(
        WorkId workId,
        long generation,
        RunStatus status,
        CancellationToken cancellationToken = default);
}

/// <summary>Work + Run + generation fence service.</summary>
public interface IWorkService
{
    ValueTask<WorkRecord> CreateWorkAsync(
        string harnessAdapterId,
        string placementId,
        string muxEndpoint,
        CancellationToken cancellationToken = default);

    ValueTask<WorkRecord> CreateWorkAsync(
        string harnessAdapterId,
        string placementId,
        string muxEndpoint,
        ResumeEvidence resumeEvidence,
        CancellationToken cancellationToken = default);

    ValueTask<WorkRecord> CreateWorkAsync(
        string harnessAdapterId,
        string placementId,
        string muxEndpoint,
        ResumeEvidence resumeEvidence,
        string? home,
        string? cwd,
        CancellationToken cancellationToken = default) =>
        CreateWorkAsync(
            harnessAdapterId,
            placementId,
            muxEndpoint,
            resumeEvidence,
            cancellationToken);

    ValueTask<FenceResult> CommitHandoffAsync(
        WorkId workId,
        long expectedSourceGeneration,
        string destPlacementId,
        string destMuxEndpoint,
        ResumeEvidence destResumeEvidence,
        string? attemptId,
        string? workPackSha256,
        CancellationToken cancellationToken = default);

    ValueTask<FenceResult> CommitHandoffAsync(
        WorkId workId,
        long expectedSourceGeneration,
        string destPlacementId,
        string destMuxEndpoint,
        ResumeEvidence destResumeEvidence = ResumeEvidence.None,
        CancellationToken cancellationToken = default) =>
        CommitHandoffAsync(
            workId,
            expectedSourceGeneration,
            destPlacementId,
            destMuxEndpoint,
            destResumeEvidence,
            attemptId: null,
            workPackSha256: null,
            cancellationToken);

    /// <summary>
    /// Reject when generation is stale or Run is not the active owner (`fenced`).
    /// </summary>
    ValueTask<FenceResult> AuthorizeMutationAsync(
        WorkId workId,
        long generation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuse a pack generation that is not the active source generation (<c>stale_pack</c>).
    /// Equal generation is the pre-flip apply. Dest has not committed n+1 yet.
    /// After dest is active, a source pack below the stored generation is stale.
    /// A forged high generation is also stale.
    /// A dest store with no Work row fails closed on the source path.
    /// Dest apply bootstraps that dest with <see cref="ImportWorkFromPackAsync"/>.
    /// </summary>
    ValueTask<FenceResult> CheckPackGenerationAsync(
        WorkId workId,
        long packGeneration,
        CancellationToken cancellationToken = default);

    ValueTask<WorkRecord?> GetWorkAsync(
        WorkId workId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<WorkRecord?>(null);

    /// <summary>
    /// Insert Work from the pack when dest has no row. Existing Work with a
    /// matching adapter id is a no-op success.
    /// </summary>
    ValueTask<FenceResult> ImportWorkFromPackAsync(
        WorkRecord work,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            FenceResult.Fail(ContinuityReasons.Internal, "import is dest-only"));

    /// <summary>
    /// Dest-only generation check. Missing Work is bootstrap, not work-not-found.
    /// Dest already active at n+1 is <c>stale_pack</c>.
    /// </summary>
    ValueTask<FenceResult> CheckDestPackGenerationAsync(
        WorkId workId,
        long packGeneration,
        string harnessAdapterId,
        CancellationToken cancellationToken = default) =>
        CheckPackGenerationAsync(workId, packGeneration, cancellationToken);

    /// <summary>
    /// Commit dest Run generation n+1 after dest probes pass. Dest with no
    /// source Run inserts the dest row. Same-store source generation n flips.
    /// </summary>
    ValueTask<FenceResult> CommitDestApplyAsync(
        WorkId workId,
        long packGeneration,
        string destPlacementId,
        string destMuxEndpoint,
        ResumeEvidence destResumeEvidence,
        string? attemptId,
        string? workPackSha256,
        string? home,
        string? cwd,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            FenceResult.Fail(ContinuityReasons.Internal, "dest apply commit is dest-only"));

    ValueTask RecoverAsync(WorkId workId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read Runs without recovery. Dest observation must match identity before a fence.
    /// </summary>
    ValueTask<IReadOnlyList<RunRecord>> ListRunsAsync(
        WorkId workId,
        CancellationToken cancellationToken = default);

    ValueTask<RunRecord?> GetActiveRunAsync(
        WorkId workId,
        CancellationToken cancellationToken = default);

    /// <summary>List every active Run with its Work. Used to find the adopted occupant.</summary>
    ValueTask<IReadOnlyList<ActiveWorkRun>> ListActiveWorksAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<ActiveWorkRun>>([]);

    /// <summary>
    /// Read the active Run as an attach target. Missing Work fails closed.
    /// A fenced, fencing, failed, or stale Run is not a target.
    /// Two active Runs fail closed. Attach does not recover or promote rows.
    /// </summary>
    ValueTask<AttachRunResult> ResolveAttachRunAsync(
        WorkId workId,
        CancellationToken cancellationToken = default);
}
