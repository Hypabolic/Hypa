using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>Default Work / Run / generation fence service (C-01).</summary>
public sealed class WorkService : IWorkService
{
    private readonly IContinuityStore _store;

    public WorkService(IContinuityStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ValueTask<WorkRecord> CreateWorkAsync(
        string harnessAdapterId,
        string placementId,
        string muxEndpoint,
        CancellationToken cancellationToken = default) =>
        CreateWorkAsync(
            harnessAdapterId,
            placementId,
            muxEndpoint,
            ResumeEvidence.None,
            cancellationToken);

    public ValueTask<WorkRecord> CreateWorkAsync(
        string harnessAdapterId,
        string placementId,
        string muxEndpoint,
        ResumeEvidence resumeEvidence,
        CancellationToken cancellationToken = default) =>
        CreateWorkAsync(
            harnessAdapterId,
            placementId,
            muxEndpoint,
            resumeEvidence,
            home: null,
            cwd: null,
            cancellationToken);

    public async ValueTask<WorkRecord> CreateWorkAsync(
        string harnessAdapterId,
        string placementId,
        string muxEndpoint,
        ResumeEvidence resumeEvidence,
        string? home,
        string? cwd,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessAdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(placementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(muxEndpoint);

        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        var work = new WorkRecord
        {
            Id = WorkId.New(),
            HarnessAdapterId = harnessAdapterId.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var run = new RunRecord
        {
            WorkId = work.Id,
            Generation = 1,
            PlacementId = placementId.Trim(),
            MuxEndpoint = muxEndpoint.Trim(),
            Status = RunStatus.Active,
            ResumeEvidence = resumeEvidence,
            Home = NormalizePath(home),
            Cwd = NormalizePath(cwd),
        };

        await _store.SaveNewWorkAsync(work, run, cancellationToken).ConfigureAwait(false);
        return work;
    }

    public async ValueTask<WorkRecord?> GetWorkAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetWorkAsync(workId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ActiveWorkRun>> ListActiveWorksAsync(
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ListActiveWorksAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FenceResult> ImportWorkFromPackAsync(
        WorkRecord work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(work.HarnessAdapterId);

        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var existing = await _store.GetWorkAsync(work.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(
                    existing.HarnessAdapterId,
                    work.HarnessAdapterId,
                    StringComparison.Ordinal))
            {
                return FenceResult.Fail(
                    ContinuityReasons.PackInvalid,
                    "harness.adapter_id does not match Work");
            }

            return FenceResult.Pass(existing);
        }

        await _store.SaveImportedWorkAsync(work, cancellationToken).ConfigureAwait(false);
        var loaded = await _store.GetWorkAsync(work.Id, cancellationToken).ConfigureAwait(false);
        return loaded is null
            ? FenceResult.Fail(ContinuityReasons.Internal, "imported Work is missing")
            : FenceResult.Pass(loaded);
    }

    public async ValueTask<FenceResult> CheckDestPackGenerationAsync(
        WorkId workId,
        long packGeneration,
        string harnessAdapterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessAdapterId);
        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _store.RecoverActiveInvariantAsync(workId, cancellationToken).ConfigureAwait(false);

        var work = await _store.GetWorkAsync(workId, cancellationToken).ConfigureAwait(false);
        if (work is null)
            return FenceResult.Pass(new WorkRecord
            {
                Id = workId,
                HarnessAdapterId = harnessAdapterId.Trim(),
                CreatedAt = DateTimeOffset.UtcNow,
            });

        if (!string.Equals(work.HarnessAdapterId, harnessAdapterId.Trim(), StringComparison.Ordinal))
        {
            return FenceResult.Fail(
                ContinuityReasons.PackInvalid,
                "harness.adapter_id does not match Work");
        }

        var runs = await _store.ListRunsAsync(workId, cancellationToken).ConfigureAwait(false);
        var destGen = packGeneration + 1;
        foreach (var run in runs)
        {
            if (run.Generation >= destGen && run.Status == RunStatus.Active)
            {
                return FenceResult.Fail(
                    ContinuityReasons.StalePack,
                    $"pack generation {packGeneration} is stale vs dest {run.Generation}");
            }
        }

        var active = await _store.GetActiveRunAsync(workId, cancellationToken).ConfigureAwait(false);
        if (active is not null && active.Generation != packGeneration && active.Generation != destGen)
        {
            return FenceResult.Fail(
                ContinuityReasons.StalePack,
                $"pack generation {packGeneration} is stale vs active {active.Generation}");
        }

        return FenceResult.Pass(work, active);
    }

    public async ValueTask<FenceResult> CommitDestApplyAsync(
        WorkId workId,
        long packGeneration,
        string destPlacementId,
        string destMuxEndpoint,
        ResumeEvidence destResumeEvidence,
        string? attemptId,
        string? workPackSha256,
        string? home,
        string? cwd,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPlacementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destMuxEndpoint);
        if (destResumeEvidence < ResumeEvidence.HarnessReported)
        {
            return FenceResult.Fail(
                ContinuityReasons.ResumeUnproven,
                $"dest evidence {destResumeEvidence} is below {ResumeEvidence.HarnessReported}");
        }

        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _store.RecoverActiveInvariantAsync(workId, cancellationToken).ConfigureAwait(false);

        var work = await _store.GetWorkAsync(workId, cancellationToken).ConfigureAwait(false);
        if (work is null)
            return FenceResult.Fail(ContinuityReasons.Internal, "work not found");

        var dest = new RunRecord
        {
            WorkId = workId,
            Generation = packGeneration + 1,
            PlacementId = destPlacementId.Trim(),
            MuxEndpoint = destMuxEndpoint.Trim(),
            Status = RunStatus.Active,
            ResumeEvidence = destResumeEvidence,
            Home = NormalizePath(home),
            Cwd = NormalizePath(cwd),
            AttemptId = string.IsNullOrWhiteSpace(attemptId) ? null : attemptId.Trim(),
            WorkPackSha256 = string.IsNullOrWhiteSpace(workPackSha256) ? null : workPackSha256.Trim(),
        };

        var active = await _store.GetActiveRunAsync(workId, cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            if (active.Generation == dest.Generation)
                return FenceResult.Pass(work, active);
            if (active.Generation != packGeneration)
            {
                return FenceResult.Fail(
                    ContinuityReasons.StalePack,
                    $"expected source generation {packGeneration}, active is {active.Generation}");
            }

            return await CommitHandoffAsync(
                    workId,
                    packGeneration,
                    destPlacementId,
                    destMuxEndpoint,
                    destResumeEvidence,
                    attemptId,
                    workPackSha256,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await _store.InsertActiveRunAsync(dest, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return FenceResult.Fail(ContinuityReasons.SourceStillActive, ex.Message);
        }

        var destLoaded = await _store.GetRunAsync(workId, dest.Generation, cancellationToken)
            .ConfigureAwait(false);
        return destLoaded is null
            ? FenceResult.Fail(ContinuityReasons.Internal, "dest Run is missing")
            : FenceResult.Pass(work, destLoaded);
    }

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());

    /// <summary>
    /// Compatibility commit without handoff identity. Dest confirmation cannot match.
    /// </summary>
    public ValueTask<FenceResult> CommitHandoffAsync(
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

    public async ValueTask<FenceResult> CommitHandoffAsync(
        WorkId workId,
        long expectedSourceGeneration,
        string destPlacementId,
        string destMuxEndpoint,
        ResumeEvidence destResumeEvidence,
        string? attemptId,
        string? workPackSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPlacementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destMuxEndpoint);

        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _store.RecoverActiveInvariantAsync(workId, cancellationToken).ConfigureAwait(false);

        var work = await _store.GetWorkAsync(workId, cancellationToken).ConfigureAwait(false);
        if (work is null)
            return FenceResult.Fail(ContinuityReasons.Internal, "work not found");

        var active = await _store.GetActiveRunAsync(workId, cancellationToken).ConfigureAwait(false);
        if (active is null)
            return FenceResult.Fail(ContinuityReasons.Internal, "work has no active Run");

        if (active.Generation != expectedSourceGeneration)
        {
            return FenceResult.Fail(
                ContinuityReasons.StalePack,
                $"expected source generation {expectedSourceGeneration}, active is {active.Generation}");
        }

        if (active.Status != RunStatus.Active)
            return FenceResult.Fail(ContinuityReasons.SourceStillActive, "source Run is not active");

        var dest = new RunRecord
        {
            WorkId = workId,
            Generation = expectedSourceGeneration + 1,
            PlacementId = destPlacementId.Trim(),
            MuxEndpoint = destMuxEndpoint.Trim(),
            Status = RunStatus.Active,
            ResumeEvidence = destResumeEvidence,
            AttemptId = string.IsNullOrWhiteSpace(attemptId) ? null : attemptId.Trim(),
            WorkPackSha256 = string.IsNullOrWhiteSpace(workPackSha256) ? null : workPackSha256.Trim(),
        };

        try
        {
            await _store.FlipGenerationAsync(
                    workId,
                    expectedSourceGeneration,
                    dest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return FenceResult.Fail(ContinuityReasons.SourceStillActive, ex.Message);
        }

        var source = await _store.GetRunAsync(workId, expectedSourceGeneration, cancellationToken)
            .ConfigureAwait(false);
        var destLoaded = await _store.GetRunAsync(workId, dest.Generation, cancellationToken)
            .ConfigureAwait(false);
        return FenceResult.Pass(work, destLoaded, source);
    }

    public async ValueTask<FenceResult> AuthorizeMutationAsync(
        WorkId workId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _store.RecoverActiveInvariantAsync(workId, cancellationToken).ConfigureAwait(false);

        var work = await _store.GetWorkAsync(workId, cancellationToken).ConfigureAwait(false);
        if (work is null)
            return FenceResult.Fail(ContinuityReasons.Internal, "work not found");

        var active = await _store.GetActiveRunAsync(workId, cancellationToken).ConfigureAwait(false);
        if (active is null)
            return FenceResult.Fail(ContinuityReasons.Fenced, "no active Run");

        if (active.Generation != generation || active.Status != RunStatus.Active)
        {
            return FenceResult.Fail(
                ContinuityReasons.Fenced,
                $"generation {generation} is not the active owner");
        }

        return FenceResult.Pass(work, active);
    }

    public async ValueTask<FenceResult> CheckPackGenerationAsync(
        WorkId workId,
        long packGeneration,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _store.RecoverActiveInvariantAsync(workId, cancellationToken).ConfigureAwait(false);

        var work = await _store.GetWorkAsync(workId, cancellationToken).ConfigureAwait(false);
        if (work is null)
        {
            // Source path still fails closed. Dest apply uses ImportWorkFromPackAsync.
            return FenceResult.Fail(ContinuityReasons.Internal, "work not found");
        }

        var active = await _store.GetActiveRunAsync(workId, cancellationToken).ConfigureAwait(false);
        if (active is null)
            return FenceResult.Fail(ContinuityReasons.Internal, "no active Run");

        // Spec §1.4: after dest is active, refuse pack generation <= stored generation.
        // Pre-flip exception: dest has not committed n+1. The active Run is still
        // source generation n. pack.generation == n is required, not stale.
        // A forged high generation (pack > active) is also refused.
        if (packGeneration != active.Generation)
        {
            return FenceResult.Fail(
                ContinuityReasons.StalePack,
                $"pack generation {packGeneration} is stale vs active {active.Generation}");
        }

        return FenceResult.Pass(work, active);
    }

    public async ValueTask RecoverAsync(WorkId workId, CancellationToken cancellationToken = default)
    {
        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _store.RecoverActiveInvariantAsync(workId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RunRecord>> ListRunsAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ListRunsAsync(workId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RunRecord?> GetActiveRunAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _store.RecoverActiveInvariantAsync(workId, cancellationToken).ConfigureAwait(false);
        return await _store.GetActiveRunAsync(workId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AttachRunResult> ResolveAttachRunAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        // Attach is a read. Recovery can promote a fencing source or hide two active Runs.

        var work = await _store.GetWorkAsync(workId, cancellationToken).ConfigureAwait(false);
        if (work is null)
            return AttachRunResult.Fail(ContinuityReasons.Internal, "work not found");

        var runs = await _store.ListRunsAsync(workId, cancellationToken).ConfigureAwait(false);
        var active = runs.Where(r => r.Status == RunStatus.Active).ToList();
        if (active.Count > 1)
            return AttachRunResult.Fail(ContinuityReasons.Internal, "multiple active runs");
        if (active.Count == 0)
            return AttachRunResult.Fail(ContinuityReasons.Fenced, "no active Run");

        var run = active[0];
        if (run.Status != RunStatus.Active)
            return AttachRunResult.Fail(ContinuityReasons.Fenced, "source Run is not an attach target");

        return AttachRunResult.Pass(work, run);
    }
}
