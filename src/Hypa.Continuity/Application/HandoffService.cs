using System.Text;
using System.Text.Json;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>Handoff state machine states (C-02).</summary>
public enum HandoffState
{
    Quiescing = 0,
    Packing = 1,
    Pulling = 2,
    Applying = 3,
    Starting = 4,
    Probing = 5,
    Fencing = 6,
    Done = 7,
    Failed = 8,
}

public sealed record HandoffRequest
{
    public required WorkId WorkId { get; init; }
    public required IHarnessAdapter Harness { get; init; }
    public required string SourceHome { get; init; }
    public required string SourceWorkspace { get; init; }
    public required string SourceMuxEndpoint { get; init; }
    public required string SourcePlacementId { get; init; }
    public required string DestHome { get; init; }
    public required string DestWorkspace { get; init; }
    public required string DestMuxEndpoint { get; init; }
    public required string DestPlacementId { get; init; }
    public string? DestPaneId { get; init; }
    public IDestOccupantStarter? DestStarter { get; init; }
    /// <summary>Optional source pane for best-effort stop after fence (spec §4.2).</summary>
    public string? SourcePaneId { get; init; }
    public ISourceOccupantStopper? SourceStopper { get; init; }
    public TimeSpan QuiesceTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool OccupantStreaming { get; init; }
    /// <summary>When false, Starting is a no-op success (fake mux tests).</summary>
    public bool RequireDestStart { get; init; }
    /// <summary>Wait bound after dest start for a harness resume report.</summary>
    public TimeSpan ResumeReportTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Optional C-21 peer pull into <see cref="DestSpool"/> before local verify.</summary>
    public IWorkPackPeerPull? PeerPull { get; init; }
    public PeerPackSource? PeerSource { get; init; }
    /// <summary>Dest-side spool. Defaults to the service source spool when null.</summary>
    public IWorkPackSpool? DestSpool { get; init; }
    /// <summary>Optional relay/transport checkpoint. Unknown dest confirmation is not success.</summary>
    public IHandoffTransport? Transport { get; init; }
    public string? AttemptId { get; init; }
    /// <summary>
    /// Destination worker. When set, source does not resolve dest HOME or dest cwd.
    /// </summary>
    public IDestWorkExecutor? DestExecutor { get; init; }
}

public sealed record HandoffResult
{
    public required bool Ok { get; init; }
    public required HandoffState State { get; init; }
    public HandoffState? FailedAt { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public string? PackPath { get; init; }
    public long? DestGeneration { get; init; }
    public string? ConversationId { get; init; }
    public ResumeEvidence DestResumeEvidence { get; init; } = ResumeEvidence.None;
    /// <summary>True when a source stopper ran and reported success.</summary>
    public bool? SourceStopped { get; init; }
    public string? SourceStopDetail { get; init; }
    public string? AttemptId { get; init; }
    public bool? Retryable { get; init; }
    public string? Stage { get; init; }
    public string? SourceStatus { get; init; }
    public string? DestConfirmation { get; init; }

    public ContinuityOutcome ToOutcome() =>
        Ok
            ? ContinuityOutcome.Success()
            : ContinuityOutcome.Failure(Reason ?? ContinuityReasons.Internal, Detail ?? "");
}

public interface IHandoffService
{
    ValueTask<HandoffResult> RunAsync(
        HandoffRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Source saw dest authority after a relay loss. Fence source.
    /// The failed attempt stays <c>ok: false</c>.
    /// </summary>
    ValueTask<HandoffResult> ObserveDestAuthorityAsync(
        HandoffDestAuthority observation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// C-02 handoff SM. Probe + workspace equivalence before generation flip.
/// Dest version must be readable before any dest write. Empty dest HOME fails.
/// </summary>
public sealed class HandoffService : IHandoffService
{
    private readonly IWorkService _works;
    private readonly IWorkspacePacker _workspace;
    private readonly IWorkPackCodec _codec;
    private readonly IWorkPackSpool _spool;

    public HandoffService(
        IWorkService works,
        IWorkspacePacker workspace,
        IWorkPackCodec codec,
        IWorkPackSpool spool)
    {
        _works = works ?? throw new ArgumentNullException(nameof(works));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
    }

    public async ValueTask<HandoffResult> RunAsync(
        HandoffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Harness);
        if (string.IsNullOrWhiteSpace(request.SourceHome)
            || string.IsNullOrWhiteSpace(request.SourceWorkspace))
        {
            return new HandoffResult
            {
                Ok = false,
                State = HandoffState.Failed,
                FailedAt = HandoffState.Quiescing,
                Reason = ContinuityReasons.Internal,
                Detail = "source home and workspace paths are required",
            };
        }

        var destExecutor = request.DestExecutor;
        if (destExecutor is null)
        {
            if (string.IsNullOrWhiteSpace(request.DestHome)
                || string.IsNullOrWhiteSpace(request.DestWorkspace))
            {
                return new HandoffResult
                {
                    Ok = false,
                    State = HandoffState.Failed,
                    FailedAt = HandoffState.Quiescing,
                    Reason = ContinuityReasons.Internal,
                    Detail = "source/dest home and workspace paths are required",
                };
            }

            if (string.Equals(
                    Path.GetFullPath(request.SourceWorkspace),
                    Path.GetFullPath(request.DestWorkspace),
                    StringComparison.Ordinal))
            {
                return new HandoffResult
                {
                    Ok = false,
                    State = HandoffState.Failed,
                    FailedAt = HandoffState.Quiescing,
                    Reason = ContinuityReasons.WorkspaceUnsupported,
                    Detail = "dest workspace must differ from source",
                };
            }

            if (string.Equals(
                    Path.GetFullPath(request.SourceHome),
                    Path.GetFullPath(request.DestHome),
                    StringComparison.Ordinal))
            {
                return new HandoffResult
                {
                    Ok = false,
                    State = HandoffState.Failed,
                    FailedAt = HandoffState.Quiescing,
                    Reason = ContinuityReasons.Internal,
                    Detail = "dest HOME must differ from source HOME",
                };
            }
        }

        HandoffState state = HandoffState.Quiescing;
        string? packPath = null;
        string? workPackSha256 = null;
        string? conversationId = null;
        long sourceGeneration = 0;
        var destResumeEvidence = ResumeEvidence.None;
        string? destHomeFull = null;
        string? destWs = null;
        var destWasEmpty = false;
        Dictionary<string, byte[]>? destHomeSnapshot = null;
        var destTouched = false;
        var attemptId = string.IsNullOrWhiteSpace(request.AttemptId)
            ? HandoffRelayFailure.NewAttemptId()
            : request.AttemptId.Trim();

        ContinuityOutcome Fail(string reason, string detail) =>
            ContinuityOutcome.Failure(reason, detail);

        HandoffResult TerminalFail(ContinuityOutcome o, bool relayLoss = false, bool classifyRelay = true)
        {
            var reason = o.Reason ?? ContinuityReasons.Internal;
            var isRelay = relayLoss || (classifyRelay && HandoffRelayFailure.IsRelayReason(reason));
            return new HandoffResult
            {
                Ok = false,
                State = HandoffState.Failed,
                FailedAt = state,
                Reason = isRelay ? HandoffRelayFailure.ClassifyReason(state, reason) : reason,
                Detail = o.Detail,
                PackPath = packPath,
                ConversationId = conversationId,
                DestResumeEvidence = destResumeEvidence,
                AttemptId = attemptId,
                Retryable = isRelay ? HandoffRelayFailure.IsRetryable(
                    HandoffRelayFailure.ClassifyReason(state, reason),
                    state) : null,
                Stage = HandoffRelayFailure.StageWire(state),
                SourceStatus = isRelay ? "active" : null,
                DestConfirmation = isRelay ? HandoffRelayFailure.DestConfirmation(state) : null,
            };
        }

        async ValueTask<HandoffResult?> AbortIfRelayFailedAsync()
        {
            if (request.Transport is null)
                return null;
            var notified = await request.Transport.NotifyStageAsync(state, attemptId, cancellationToken)
                .ConfigureAwait(false);
            if (notified.Ok)
                return null;
            CleanupDest();
            return TerminalFail(notified, relayLoss: true);
        }

        void CleanupDest()
        {
            if (!destTouched)
                return;
            if (destWs is not null)
                TryRemoveEmptyDestThisRun(destWs, destWasEmpty);
            if (destHomeFull is not null && destHomeSnapshot is not null)
                TryRevertDestHome(destHomeFull, destHomeSnapshot);
        }

        try
        {
            var active = await _works.GetActiveRunAsync(request.WorkId, cancellationToken)
                .ConfigureAwait(false);
            if (active is null)
                return TerminalFail(Fail(ContinuityReasons.Internal, "no active Run"));
            sourceGeneration = active.Generation;

            var version = request.Harness.ReadVersion(request.SourceHome, out var sourceVersion);
            if (!version.Ok)
                return TerminalFail(version);
            if (string.IsNullOrWhiteSpace(sourceVersion))
            {
                return TerminalFail(Fail(
                    ContinuityReasons.HarnessVersionUnreadable,
                    "source harness version empty"));
            }

            var probeSource = request.Harness.ProbeConversationId(
                request.SourceHome,
                request.SourceWorkspace);
            conversationId = probeSource.ConversationId;
            if (!probeSource.Ok || string.IsNullOrWhiteSpace(conversationId))
            {
                return TerminalFail(probeSource.Ok
                    ? Fail(ContinuityReasons.ConversationMismatch, "source conversation empty")
                    : probeSource.ToOutcome());
            }

            var quiesce = request.Harness.Quiesce(
                new HarnessRunContext
                {
                    Home = request.SourceHome,
                    Cwd = request.SourceWorkspace,
                    ConversationId = conversationId!,
                    OccupantStreaming = request.OccupantStreaming,
                },
                request.QuiesceTimeout);
            if (!quiesce.Ok)
                return TerminalFail(quiesce);

            state = HandoffState.Packing;
            using var staging = new TempDir("hypa-pack-");
            var harnessStore = Path.Combine(staging.Path, "harness", "store");
            Directory.CreateDirectory(Path.Combine(staging.Path, "harness"));
            Directory.CreateDirectory(harnessStore);

            var capture = request.Harness.CaptureStore(
                request.SourceHome,
                harnessStore,
                request.SourceWorkspace,
                conversationId!);
            if (!capture.Ok)
                return TerminalFail(capture);

            var harnessVersion = sourceVersion!;
            foreach (var verFile in new[]
                     {
                         Path.Combine(harnessStore, ".fake", "agent", "version.txt"),
                         Path.Combine(harnessStore, ".pi", "agent", "version.txt"),
                     })
            {
                if (!File.Exists(verFile))
                    continue;
                var text = File.ReadAllText(verFile).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    harnessVersion = text;
                    break;
                }
            }

            if (!string.Equals(harnessVersion, sourceVersion, StringComparison.Ordinal))
            {
                return TerminalFail(Fail(
                    ContinuityReasons.HarnessVersionIncompatible,
                    "packed harness version does not match source envelope"));
            }

            File.WriteAllText(
                Path.Combine(staging.Path, "harness", "adapter_id"),
                request.Harness.AdapterId,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(staging.Path, "harness", "version.txt"),
                harnessVersion,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(staging.Path, "harness", "conversation_id.txt"),
                conversationId!,
                new UTF8Encoding(false));

            var workspaceDir = Path.Combine(staging.Path, "workspace");
            var wsCapture = _workspace.Capture(request.SourceWorkspace, workspaceDir);
            if (!wsCapture.Ok)
                return TerminalFail(wsCapture);

            var wsManifest = JsonSerializer.Deserialize(
                File.ReadAllText(Path.Combine(workspaceDir, "workspace-manifest.json")),
                WorkspaceManifestJsonContext.Default.WorkspaceManifestDto)!;

            var manifest = new WorkPackManifestDto
            {
                Schema = 1,
                WorkId = request.WorkId.Value,
                Generation = sourceGeneration,
                CreatedAt = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
                Harness = new WorkPackHarnessDto
                {
                    AdapterId = request.Harness.AdapterId,
                    HarnessVersion = harnessVersion,
                    ConversationId = conversationId!,
                },
                Workspace = new WorkPackWorkspaceDto
                {
                    Head = wsManifest.Head,
                    Branch = wsManifest.Branch,
                    TrackedDiffSha256 = wsManifest.TrackedDiffSha256,
                    UntrackedSha256 = wsManifest.UntrackedSha256,
                },
                Source = new WorkPackSourceDto
                {
                    MuxEndpoint = request.SourceMuxEndpoint,
                    WorkspacePath = Path.GetFullPath(request.SourceWorkspace),
                },
            };

            var write = _codec.Write(
                _spool.SpoolDirectory,
                manifest,
                Path.Combine(staging.Path, "harness"),
                workspaceDir,
                out packPath);
            if (!write.Ok)
                return TerminalFail(write);

            workPackSha256 = string.IsNullOrWhiteSpace(packPath)
                ? null
                : _codec.HashPackFile(packPath);

            if (destExecutor is not null)
            {
                state = HandoffState.Pulling;
                var destPullAbort = await AbortIfRelayFailedAsync().ConfigureAwait(false);
                if (destPullAbort is not null)
                    return destPullAbort;

                state = HandoffState.Applying;
                var destApplyAbort = await AbortIfRelayFailedAsync().ConfigureAwait(false);
                if (destApplyAbort is not null)
                    return destApplyAbort;

                var dest = await destExecutor.ApplyAsync(
                        new DestApplyInvocation
                        {
                            PackPath = packPath!,
                            Request = new DestApplyRequest
                            {
                                AttemptId = attemptId,
                                WorkId = request.WorkId.Value,
                                DestPlacementId = request.DestPlacementId,
                                DestPaneId = request.DestPaneId,
                                PackSha256 = workPackSha256,
                                HarnessAdapterId = request.Harness.AdapterId,
                            },
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                destResumeEvidence = dest.DestResumeEvidence;
                if (!string.Equals(dest.AttemptId, attemptId, StringComparison.Ordinal))
                {
                    return TerminalFail(Fail(
                        ContinuityReasons.Internal,
                        "dest worker attempt_id mismatch"));
                }

                if (!dest.Ok)
                {
                    return TerminalFail(
                        Fail(
                            dest.Reason ?? ContinuityReasons.Internal,
                            dest.Detail ?? "dest apply failed"),
                        classifyRelay: false);
                }

                state = HandoffState.Probing;
                var destProbeAbort = await AbortIfRelayFailedAsync().ConfigureAwait(false);
                if (destProbeAbort is not null)
                    return destProbeAbort;
                if (dest.DestResumeEvidence < ResumeEvidence.HarnessReported)
                {
                    return TerminalFail(Fail(
                        ContinuityReasons.ResumeUnproven,
                        $"dest evidence {dest.DestResumeEvidence} is below {ResumeEvidence.HarnessReported}"));
                }

                if (!string.Equals(dest.ConversationId, conversationId, StringComparison.Ordinal))
                {
                    return TerminalFail(Fail(
                        ContinuityReasons.ConversationMismatch,
                        $"expected {conversationId} got {dest.ConversationId}"));
                }

                if (dest.DestGeneration != sourceGeneration + 1)
                {
                    return TerminalFail(Fail(
                        ContinuityReasons.Internal,
                        "dest generation is not n+1"));
                }

                state = HandoffState.Fencing;
                var destFenceAbort = await AbortIfRelayFailedAsync().ConfigureAwait(false);
                if (destFenceAbort is not null)
                    return destFenceAbort;

                var destEndpoint = string.IsNullOrWhiteSpace(request.DestMuxEndpoint)
                    ? dest.DestMuxEndpoint
                    : request.DestMuxEndpoint;
                if (string.IsNullOrWhiteSpace(destEndpoint))
                {
                    return TerminalFail(Fail(
                        ContinuityReasons.Internal,
                        "dest mux endpoint is missing"));
                }

                var destFence = await _works.CommitHandoffAsync(
                        request.WorkId,
                        sourceGeneration,
                        request.DestPlacementId,
                        destEndpoint,
                        dest.DestResumeEvidence,
                        attemptId,
                        workPackSha256,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!destFence.Ok)
                {
                    return TerminalFail(Fail(
                        destFence.Reason ?? ContinuityReasons.SourceStillActive,
                        destFence.Detail ?? "fence failed"));
                }

                bool? destSourceStopped = null;
                string? destSourceStopDetail = null;
                if (request.SourceStopper is not null
                    && !string.IsNullOrWhiteSpace(request.SourcePaneId))
                {
                    try
                    {
                        var stop = await request.SourceStopper.StopAsync(
                                new SourceOccupantStopRequest
                                {
                                    SocketPath = StripUnixPrefix(request.SourceMuxEndpoint),
                                    PaneId = request.SourcePaneId!,
                                    WorkId = request.WorkId.Value,
                                    Generation = destFence.Run!.Generation,
                                },
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        destSourceStopped = stop.Ok;
                        destSourceStopDetail = stop.Ok ? null : (stop.Detail ?? stop.Reason);
                    }
                    catch (Exception ex)
                    {
                        destSourceStopped = false;
                        destSourceStopDetail = ex.Message;
                    }
                }

                return new HandoffResult
                {
                    Ok = true,
                    State = HandoffState.Done,
                    PackPath = packPath,
                    DestGeneration = destFence.Run!.Generation,
                    ConversationId = conversationId,
                    DestResumeEvidence = dest.DestResumeEvidence,
                    SourceStopped = destSourceStopped,
                    SourceStopDetail = destSourceStopDetail,
                    AttemptId = attemptId,
                    Stage = HandoffRelayFailure.StageWire(HandoffState.Done),
                    SourceStatus = "dead",
                    DestConfirmation = "confirmed",
                };
            }

            state = HandoffState.Pulling;
            var pullAbort = await AbortIfRelayFailedAsync().ConfigureAwait(false);
            if (pullAbort is not null)
                return pullAbort;
            var destSpool = request.DestSpool ?? _spool;
            if (request.PeerPull is not null)
            {
                if (request.PeerSource is null)
                {
                    return TerminalFail(Fail(
                        ContinuityReasons.Internal,
                        "PeerPull requires PeerSource"));
                }

                var peer = request.PeerPull.Pull(
                    request.WorkId.Value,
                    request.PeerSource,
                    destSpool);
                if (!peer.Ok)
                    return TerminalFail(peer);
            }

            using var pullDir = new TempDir("hypa-pull-");
            var pull = destSpool.Pull(request.WorkId.Value, pullDir.Path, out var pulledPack);
            if (!pull.Ok)
                return TerminalFail(pull);

            using var extractDir = new TempDir("hypa-extract-");
            var read = _codec.Read(pulledPack!, extractDir.Path, out var readManifest);
            if (!read.Ok || readManifest is null)
                return TerminalFail(read);

            if (!string.Equals(readManifest.WorkId, request.WorkId.Value, StringComparison.Ordinal))
            {
                return TerminalFail(Fail(
                    ContinuityReasons.PackInvalid,
                    "work_id does not match Work"));
            }

            if (!string.Equals(
                    readManifest.Harness.AdapterId,
                    request.Harness.AdapterId,
                    StringComparison.Ordinal))
            {
                return TerminalFail(Fail(
                    ContinuityReasons.PackInvalid,
                    "harness.adapter_id does not match dest adapter"));
            }

            var stale = await _works.CheckPackGenerationAsync(
                    request.WorkId,
                    readManifest.Generation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!stale.Ok)
                return TerminalFail(ContinuityOutcome.Failure(stale.Reason!, stale.Detail!));

            if (stale.Work is not null
                && !string.Equals(
                    stale.Work.HarnessAdapterId,
                    readManifest.Harness.AdapterId,
                    StringComparison.Ordinal))
            {
                return TerminalFail(Fail(
                    ContinuityReasons.PackInvalid,
                    "harness.adapter_id does not match Work"));
            }

            state = HandoffState.Applying;
            var applyAbort = await AbortIfRelayFailedAsync().ConfigureAwait(false);
            if (applyAbort is not null)
                return applyAbort;
            // Spec §3.4: never wipe an existing non-empty dest tree.
            // Dest version and dest workspace preflight run before any dest write.
            // Failed dest checks must not create dest HOME or dest workspace dirs.
            // Restore runs after dest version preflight and before dest workspace apply.
            // If dest was missing or empty at start, remove it on any pre-fence failure so retry works (§5.1).
            var destWorkspace = Path.GetFullPath(request.DestWorkspace);
            destWs = destWorkspace;
            destHomeFull = Path.GetFullPath(request.DestHome);
            var destExisted = Directory.Exists(destWorkspace);
            destWasEmpty = !destExisted
                || !Directory.EnumerateFileSystemEntries(destWorkspace).Any();
            if (destExisted && !destWasEmpty)
            {
                return TerminalFail(Fail(
                    ContinuityReasons.WorkspaceUnsupported,
                    "dest workspace is not empty; refuse wipe"));
            }

            var destVersion = request.Harness.ReadVersion(request.DestHome, out var destVersionText);
            if (!destVersion.Ok)
                return TerminalFail(destVersion);
            if (!string.Equals(destVersionText, readManifest.Harness.HarnessVersion, StringComparison.Ordinal)
                || !string.Equals(destVersionText, sourceVersion, StringComparison.Ordinal))
            {
                return TerminalFail(Fail(
                    ContinuityReasons.HarnessVersionIncompatible,
                    "dest harness version envelope mismatch"));
            }

            destHomeSnapshot = SnapshotHomeFiles(destHomeFull);
            destTouched = true;

            var restore = request.Harness.RestoreStore(
                request.DestHome,
                Path.Combine(extractDir.Path, "harness", "store"),
                request.DestWorkspace,
                conversationId!);
            if (!restore.Ok)
            {
                CleanupDest();
                return TerminalFail(restore);
            }

            var destVersionAfter = request.Harness.ReadVersion(request.DestHome, out var destVersionAfterText);
            if (!destVersionAfter.Ok)
            {
                CleanupDest();
                return TerminalFail(destVersionAfter);
            }

            if (!string.Equals(destVersionAfterText, readManifest.Harness.HarnessVersion, StringComparison.Ordinal)
                || !string.Equals(destVersionAfterText, sourceVersion, StringComparison.Ordinal))
            {
                CleanupDest();
                return TerminalFail(Fail(
                    ContinuityReasons.HarnessVersionIncompatible,
                    "dest harness version envelope mismatch"));
            }

            Directory.CreateDirectory(destWorkspace);
            var apply = _workspace.Apply(
                Path.Combine(extractDir.Path, "workspace"),
                destWorkspace);
            if (!apply.Ok)
            {
                CleanupDest();
                return TerminalFail(apply);
            }

            var equiv = _workspace.CheckEquivalence(
                Path.Combine(extractDir.Path, "workspace"),
                destWorkspace);
            if (!equiv.Ok)
            {
                CleanupDest();
                return TerminalFail(equiv);
            }

            state = HandoffState.Starting;
            var startAbort = await AbortIfRelayFailedAsync().ConfigureAwait(false);
            if (startAbort is not null)
                return startAbort;
            ContinuityOutcome startArgsOutcome = request.Harness.BuildStartArgs(
                destWorkspace,
                request.DestHome,
                conversationId!,
                out var startArgs);
            if (!startArgsOutcome.Ok || startArgs is null)
            {
                CleanupDest();
                return TerminalFail(startArgsOutcome);
            }

            IDestOccupantLiveness? destLiveness = null;
            if (request.RequireDestStart)
            {
                if (request.DestStarter is null)
                {
                    CleanupDest();
                    return TerminalFail(Fail(ContinuityReasons.StartFailed, "dest starter missing"));
                }

                if (string.IsNullOrWhiteSpace(request.DestPaneId))
                {
                    CleanupDest();
                    return TerminalFail(Fail(ContinuityReasons.StartFailed, "dest pane_id required"));
                }

                var started = await request.DestStarter.StartResumeAsync(
                        new DestOccupantStartRequest
                        {
                            MuxId = request.DestPlacementId,
                            SocketPath = StripUnixPrefix(request.DestMuxEndpoint),
                            Workspace = destWorkspace,
                            CubeHome = request.DestHome,
                            HarnessId = request.Harness.AdapterId,
                            ResumeArgs = startArgs.Argv,
                            Env = startArgs.Env,
                            PaneId = request.DestPaneId,
                            WorkId = request.WorkId.Value,
                            Generation = sourceGeneration + 1,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!started.Ok)
                {
                    CleanupDest();
                    return TerminalFail(Fail(
                        ContinuityReasons.StartFailed,
                        started.Error ?? "dest start failed"));
                }

                destLiveness = started.Liveness;
            }

            state = HandoffState.Probing;
            var probeAbort = await AbortIfRelayFailedAsync().ConfigureAwait(false);
            if (probeAbort is not null)
                return probeAbort;
            var destProbe = request.RequireDestStart
                ? await request.Harness.WaitForHarnessReportAsync(
                        request.DestHome,
                        destWorkspace,
                        startArgs,
                        request.ResumeReportTimeout,
                        destLiveness,
                        cancellationToken)
                    .ConfigureAwait(false)
                : request.Harness.ProbeConversationId(
                    request.DestHome,
                    destWorkspace);
            destResumeEvidence = destProbe.Evidence;
            var destId = destProbe.ConversationId;
            if (!destProbe.Ok)
            {
                CleanupDest();
                return TerminalFail(destProbe.ToOutcome());
            }

            if (destProbe.Evidence < ResumeEvidence.HarnessReported)
            {
                CleanupDest();
                return TerminalFail(Fail(
                    ContinuityReasons.ResumeUnproven,
                    $"dest evidence {destProbe.Evidence} is below {ResumeEvidence.HarnessReported}"));
            }

            if (!string.Equals(destId, conversationId, StringComparison.Ordinal))
            {
                CleanupDest();
                return TerminalFail(Fail(
                    ContinuityReasons.ConversationMismatch,
                    $"expected {conversationId} got {destId}"));
            }

            state = HandoffState.Fencing;
            var fenceAbort = await AbortIfRelayFailedAsync().ConfigureAwait(false);
            if (fenceAbort is not null)
                return fenceAbort;
            var fence = await _works.CommitHandoffAsync(
                    request.WorkId,
                    sourceGeneration,
                    request.DestPlacementId,
                    request.DestMuxEndpoint,
                    destProbe.Evidence,
                    attemptId,
                    workPackSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!fence.Ok)
            {
                CleanupDest();
                return TerminalFail(Fail(
                    fence.Reason ?? ContinuityReasons.SourceStillActive,
                    fence.Detail ?? "fence failed"));
            }

            // Spec §4.2: best-effort source stop after fence. Do not undo success.
            // Cancellation after commit must not flip ok:true → ok:false.
            bool? sourceStopped = null;
            string? sourceStopDetail = null;
            if (request.SourceStopper is not null
                && !string.IsNullOrWhiteSpace(request.SourcePaneId))
            {
                try
                {
                    var stop = await request.SourceStopper.StopAsync(
                            new SourceOccupantStopRequest
                            {
                                SocketPath = StripUnixPrefix(request.SourceMuxEndpoint),
                                PaneId = request.SourcePaneId!,
                                WorkId = request.WorkId.Value,
                                Generation = fence.Run!.Generation,
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    sourceStopped = stop.Ok;
                    sourceStopDetail = stop.Ok ? null : (stop.Detail ?? stop.Reason);
                }
                catch (Exception ex)
                {
                    sourceStopped = false;
                    sourceStopDetail = ex.Message;
                }
            }

            return new HandoffResult
            {
                Ok = true,
                State = HandoffState.Done,
                PackPath = packPath,
                DestGeneration = fence.Run!.Generation,
                ConversationId = conversationId,
                DestResumeEvidence = destProbe.Evidence,
                SourceStopped = sourceStopped,
                SourceStopDetail = sourceStopDetail,
                AttemptId = attemptId,
                Stage = HandoffRelayFailure.StageWire(HandoffState.Done),
                SourceStatus = "dead",
                DestConfirmation = "confirmed",
            };
        }
        catch (Exception ex)
        {
            CleanupDest();
            return new HandoffResult
            {
                Ok = false,
                State = HandoffState.Failed,
                FailedAt = state,
                Reason = ContinuityReasons.Internal,
                Detail = ex.Message,
                PackPath = packPath,
                ConversationId = conversationId,
                DestResumeEvidence = destResumeEvidence,
                AttemptId = attemptId,
                Stage = HandoffRelayFailure.StageWire(state),
            };
        }
    }

    public async ValueTask<HandoffResult> ObserveDestAuthorityAsync(
        HandoffDestAuthority observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.DestPlacementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.DestMuxEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.AttemptId);

        // Caller evidence is untrusted. Do not probe harness files.
        _ = observation.CallerResumeEvidence;
        var runs = await _works.ListRunsAsync(observation.WorkId, cancellationToken)
            .ConfigureAwait(false);
        RunRecord? dest = null;
        RunRecord? source = null;
        foreach (var run in runs)
        {
            if (run.Generation == observation.SourceGeneration)
                source = run;
            if (string.Equals(run.PlacementId, observation.DestPlacementId, StringComparison.Ordinal)
                && run.Generation > observation.SourceGeneration
                && run.Status == RunStatus.Active)
            {
                dest = run;
            }
        }

        if (dest is not null)
        {
            if (!HandoffIdentityMatches(dest, observation))
            {
                return new HandoffResult
                {
                    Ok = false,
                    State = HandoffState.Failed,
                    FailedAt = HandoffState.Fencing,
                    Reason = ContinuityReasons.UnknownCommit,
                    Detail = "Dest Run does not match this attempt or WorkPack hash.",
                    DestGeneration = dest.Generation,
                    DestResumeEvidence = ResumeEvidence.None,
                    AttemptId = observation.AttemptId,
                    Retryable = false,
                    Stage = HandoffRelayFailure.StageWire(HandoffState.Fencing),
                    SourceStatus = ObserveSourceStatus(source),
                    DestConfirmation = "unknown",
                };
            }

            await _works.RecoverAsync(observation.WorkId, cancellationToken).ConfigureAwait(false);
            var active = await _works.GetActiveRunAsync(observation.WorkId, cancellationToken)
                .ConfigureAwait(false);
            return new HandoffResult
            {
                Ok = false,
                State = HandoffState.Failed,
                FailedAt = HandoffState.Fencing,
                Reason = ContinuityReasons.UnknownCommit,
                Detail = "Source fenced after it saw dest authority. The failed attempt is not success.",
                DestGeneration = active?.Generation ?? dest.Generation,
                DestResumeEvidence = active?.ResumeEvidence ?? dest.ResumeEvidence,
                AttemptId = observation.AttemptId,
                Retryable = false,
                Stage = HandoffRelayFailure.StageWire(HandoffState.Fencing),
                SourceStatus = "dead",
                DestConfirmation = "confirmed",
            };
        }

        if (source is null)
        {
            return new HandoffResult
            {
                Ok = false,
                State = HandoffState.Failed,
                FailedAt = HandoffState.Fencing,
                Reason = ContinuityReasons.UnknownCommit,
                Detail = "dest authority observed but work has no active Run",
                AttemptId = observation.AttemptId,
                Retryable = false,
                Stage = HandoffRelayFailure.StageWire(HandoffState.Fencing),
                SourceStatus = "dead",
                DestConfirmation = "unknown",
            };
        }

        if (source.Status != RunStatus.Active)
        {
            return new HandoffResult
            {
                Ok = false,
                State = HandoffState.Failed,
                FailedAt = HandoffState.Fencing,
                Reason = ContinuityReasons.UnknownCommit,
                Detail = HandoffRelayFailure.SplitBrainWindowDetail,
                DestGeneration = source.Generation,
                DestResumeEvidence = source.ResumeEvidence,
                AttemptId = observation.AttemptId,
                Retryable = false,
                Stage = HandoffRelayFailure.StageWire(HandoffState.Fencing),
                SourceStatus = ObserveSourceStatus(source),
                DestConfirmation = "split_brain",
            };
        }

        return new HandoffResult
        {
            Ok = false,
            State = HandoffState.Failed,
            FailedAt = HandoffState.Fencing,
            Reason = ContinuityReasons.UnknownCommit,
            Detail = "Dest is not the active Run. Caller evidence cannot activate dest.",
            DestGeneration = source.Generation,
            DestResumeEvidence = ResumeEvidence.None,
            AttemptId = observation.AttemptId,
            Retryable = false,
            Stage = HandoffRelayFailure.StageWire(HandoffState.Fencing),
            SourceStatus = "active",
            DestConfirmation = "unknown",
        };
    }

    private static string ObserveSourceStatus(RunRecord? source) =>
        source is { Status: RunStatus.Active or RunStatus.Fencing } ? "active" : "dead";

    private static bool HandoffIdentityMatches(RunRecord dest, HandoffDestAuthority observation)
    {
        if (string.IsNullOrWhiteSpace(observation.AttemptId) || string.IsNullOrWhiteSpace(dest.AttemptId))
            return false;
        if (!string.Equals(dest.AttemptId, observation.AttemptId, StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(observation.WorkPackSha256) || string.IsNullOrWhiteSpace(dest.WorkPackSha256))
            return false;
        return string.Equals(dest.WorkPackSha256, observation.WorkPackSha256, StringComparison.Ordinal);
    }

    private static string StripUnixPrefix(string endpoint)
    {
        const string prefix = "unix:";
        if (endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return endpoint[prefix.Length..];
        return endpoint;
    }

    /// <summary>
    /// Spec §5.1: on pre-fence failure, remove a dest that was missing or empty at start
    /// so the operator can retry. Never delete a dest that already had content.
    /// </summary>
    private static void TryRemoveEmptyDestThisRun(string destWs, bool destWasEmptyAtStart)
    {
        if (!destWasEmptyAtStart)
            return;
        try
        {
            if (Directory.Exists(destWs))
                Directory.Delete(destWs, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, byte[]> SnapshotHomeFiles(string home)
    {
        var snap = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(home))
            return snap;

        foreach (var file in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(home, file).Replace('\\', '/');
            snap[rel] = File.ReadAllBytes(file);
        }

        return snap;
    }

    /// <summary>
    /// Restore dest HOME files written after the pre-restore snapshot.
    /// Leave files that were already on dest. Do not recursive-delete the dest store.
    /// </summary>
    private static void TryRevertDestHome(string destHome, Dictionary<string, byte[]> snapshot)
    {
        try
        {
            if (!Directory.Exists(destHome))
                return;

            foreach (var file in Directory.GetFiles(destHome, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(destHome, file).Replace('\\', '/');
                if (!snapshot.ContainsKey(rel))
                    File.Delete(file);
                else if (!snapshot[rel].AsSpan().SequenceEqual(File.ReadAllBytes(file)))
                    File.WriteAllBytes(file, snapshot[rel]);
            }

            foreach (var (rel, bytes) in snapshot)
            {
                var path = Path.Combine(destHome, rel);
                if (File.Exists(path))
                    continue;
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                File.WriteAllBytes(path, bytes);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
