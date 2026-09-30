using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Mux;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Cli.Attach;

internal sealed class PendingEndpointActivation
{
    private static readonly TimeSpan ActivationTimeout = AttachEndpointProtocol.PhaseTimeout;

    public EndpointActivationLease Source { get; }
    public bool SourceAvailable { get; private set; }
    public EndpointActivationLease Target { get; private set; }
    public EndpointFocusTarget? Focus { get; private set; }
    public bool HostFocused { get; private set; }
    public AttachGeometry Resize { get; private set; }
    public ActivationPhase Phase { get; private set; }
    public DateTimeOffset Deadline { get; private set; }
    public ulong Epoch { get; }
    public EndpointActivationIntent? Successor { get; private set; }
    internal CubesConnectStageClock? StageClock { get; }

    private ulong _nextFocusSerial;
    private string? _rollbackError;

    internal static PendingEndpointActivation ForTests(
        ActivationPhase phase,
        EndpointActivationLease source,
        EndpointActivationLease target,
        bool sourceAvailable = true,
        ulong epoch = 9,
        AttachGeometry? resize = null,
        CubesConnectStageClock? stageClock = null,
        DateTimeOffset? deadline = null) =>
        new(
            source,
            sourceAvailable,
            target,
            null,
            true,
            resize ?? new AttachGeometry
            {
                Columns = 80,
                Rows = 24,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            },
            phase,
            deadline ?? DateTimeOffset.UtcNow + ActivationTimeout,
            epoch,
            stageClock);

    private PendingEndpointActivation(
        EndpointActivationLease source,
        bool sourceAvailable,
        EndpointActivationLease target,
        EndpointFocusTarget? focus,
        bool hostFocused,
        AttachGeometry resize,
        ActivationPhase phase,
        DateTimeOffset deadline,
        ulong epoch,
        CubesConnectStageClock? stageClock = null)
    {
        Source = source;
        SourceAvailable = sourceAvailable;
        Target = target;
        Focus = focus;
        HostFocused = hostFocused;
        Resize = resize;
        Phase = phase;
        Deadline = deadline;
        Epoch = epoch;
        StageClock = stageClock;
    }

    public static Result<PendingEndpointActivation, ActivationBeginError> Begin(
        EndpointActivationBeginRequest request,
        IEndpointRegistryPort endpoints,
        DateTimeOffset now)
    {
        if (request.Geometry.Columns < 1 || request.Geometry.Rows < 1)
        {
            return Result<PendingEndpointActivation, ActivationBeginError>.Fail(
                new ActivationBeginError.Preflight(
                    "endpoint activation did not include a surface resize"));
        }

        var source = request.SourceAvailable && request.Source is not null
            ? request.Source
            : DisconnectedLease(request.Source?.EndpointId ?? endpoints.ActiveId, request.ClientId);
        var sourceAvailable = request.SourceAvailable && request.Source is not null;
        var target = request.TargetLease;

        if (sourceAvailable
            && !endpoints.SupportsSurfaceInterest(source.EndpointId))
        {
            return Result<PendingEndpointActivation, ActivationBeginError>.Fail(
                new ActivationBeginError.Preflight(
                    "endpoint must be updated before it can join the selected surface"));
        }

        if (!endpoints.SupportsSurfaceInterest(target.EndpointId))
        {
            return Result<PendingEndpointActivation, ActivationBeginError>.Fail(
                new ActivationBeginError.Preflight(
                    "endpoint must be updated before it can join the selected surface"));
        }

        var sourceIsTarget = string.Equals(source.EndpointId, target.EndpointId, StringComparison.Ordinal);
        var focus = request.Target.FocusTarget();

        // Validate every typed lifecycle envelope before the first transport write.
        try
        {
            BuildSurfaceInterestRequest(target.BootId, SurfaceRequestId(request.Epoch, "off"), false);
            BuildSurfaceInterestRequest(target.BootId, SurfaceRequestId(request.Epoch, "on"), true);
            if (focus is not null)
                BuildFocusRequest(target.BootId, $"client-shell-focus:{request.Epoch}:1", focus);
        }
        catch (InvalidOperationException ex)
        {
            return Result<PendingEndpointActivation, ActivationBeginError>.Fail(
                new ActivationBeginError.Preflight(ex.Message));
        }

        endpoints.FreezeInput();
        var activation = new PendingEndpointActivation(
            source,
            sourceAvailable,
            target,
            focus,
            request.HostFocused,
            request.Geometry,
            new ActivationPhase.ReleasingSource(SurfaceRequestId(request.Epoch, "off")),
            now + ActivationTimeout,
            request.Epoch,
            request.Target.StageClock);

        activation.StageClock?.Stamp(CubesConnectStages.Begin);

        if (sourceIsTarget || !sourceAvailable)
        {
            var start = activation.StartTarget(endpoints);
            if (!start.IsOk)
            {
                return Result<PendingEndpointActivation, ActivationBeginError>.Fail(
                    new ActivationBeginError.Partial(activation, start.Error));
            }
        }
        else
        {
            var focusOffId = $"client-shell-focus:{request.Epoch}:off";
            if (endpoints.SendTo(source.EndpointId, new EndpointActivationMessage.FocusRevoke(focusOffId))
                != EndpointSendOutcome.Sent)
            {
                return Result<PendingEndpointActivation, ActivationBeginError>.Fail(
                    new ActivationBeginError.Partial(
                        activation,
                        "source endpoint focus revoke could not be sent"));
            }

            var offId = SurfaceRequestId(request.Epoch, "off");
            if (endpoints.SendTo(
                    source.EndpointId,
                    new EndpointActivationMessage.SurfaceInterest(offId, false))
                != EndpointSendOutcome.Sent)
            {
                return Result<PendingEndpointActivation, ActivationBeginError>.Fail(
                    new ActivationBeginError.Partial(
                        activation,
                        "source endpoint release could not be sent"));
            }

            // Source inactive before acknowledgement.
            endpoints.SetSurfaceActive(source.EndpointId, false);
        }

        return Result<PendingEndpointActivation, ActivationBeginError>.Ok(activation);
    }

    public string? PresentationSyncEndpoint() => Phase switch
    {
        ActivationPhase.ActivatingTarget => Target.EndpointId,
        ActivationPhase.RestoringSource => Source.EndpointId,
        _ => null,
    };

    public string? SourceCommandLane() =>
        SourceAvailable
        && !string.Equals(Source.EndpointId, Target.EndpointId, StringComparison.Ordinal)
            ? Source.EndpointId
            : null;

    public bool CanRetarget(string endpointId) =>
        string.Equals(Target.EndpointId, endpointId, StringComparison.Ordinal)
        && Successor is null
        && Phase is ActivationPhase.ReleasingSource or ActivationPhase.ActivatingTarget;

    public ActivationRollback Supersede(
        EndpointActivationIntent intent,
        IEndpointRegistryPort endpoints)
    {
        Successor = intent;
        var sourceRestorationInFlight = Phase is ActivationPhase.RestoringSource
            || (Phase is ActivationPhase.SynchronizingPresentation sync
                && sync.Completion is ActivationCompletion.RestoredSource);
        if (sourceRestorationInFlight)
            return new ActivationRollback.Pending();

        return Rollback(endpoints, "endpoint handoff superseded by a newer selection", false);
    }

    public bool AcceptsEndpoint(string endpointId, ulong generation) =>
        (SourceAvailable
            && string.Equals(Source.EndpointId, endpointId, StringComparison.Ordinal)
            && Source.ConnectionGeneration == generation)
        || (string.Equals(Target.EndpointId, endpointId, StringComparison.Ordinal)
            && Target.ConnectionGeneration == generation);

    public bool InvolvesEndpoint(string endpointId) =>
        string.Equals(Source.EndpointId, endpointId, StringComparison.Ordinal)
        || string.Equals(Target.EndpointId, endpointId, StringComparison.Ordinal);

    public bool AcceptsResponse(
        string endpointId,
        ulong generation,
        string bootId,
        string requestId)
    {
        return Phase switch
        {
            ActivationPhase.ReleasingSource off =>
                EndpointMatches(Source, endpointId, generation, bootId)
                && off.RequestId == requestId,
            ActivationPhase.ActivatingTarget target =>
                EndpointMatches(Target, endpointId, generation, bootId)
                && (target.RequestId == requestId || target.FocusRequestId == requestId),
            ActivationPhase.ReleasingTargetForRollback rollback =>
                EndpointMatches(Target, endpointId, generation, bootId)
                && rollback.RequestId == requestId,
            ActivationPhase.RestoringSource restore =>
                EndpointMatches(Source, endpointId, generation, bootId)
                && restore.RequestId == requestId,
            ActivationPhase.SynchronizingPresentation sync =>
                EndpointMatches(sync.Lease, endpointId, generation, bootId)
                && sync.RequestId == requestId,
            ActivationPhase.AwaitingPresentationEffects awaiting =>
                EndpointMatches(awaiting.Lease, endpointId, generation, bootId)
                && awaiting.Token == requestId,
            _ => false,
        };
    }

    public bool Expired(DateTimeOffset now) => now >= Deadline;

    public SurfaceActivationProgress ReceiveResponseForBoot(
        string endpointId,
        ulong generation,
        string bootId,
        string requestId,
        EndpointSurfaceControlResult result,
        IEndpointRegistryPort endpoints)
    {
        if (!AcceptsResponse(endpointId, generation, bootId, requestId))
            return new SurfaceActivationProgress.Stale();

        if (!result.Ok)
        {
            return new SurfaceActivationProgress.Rejected(
                result.ErrorMessage ?? "endpoint activation rejected",
                Phase is ActivationPhase.ReleasingSource && result.HasErrorCode);
        }

        return Phase switch
        {
            ActivationPhase.ReleasingSource => HandleSourceReleaseAck(endpoints),
            ActivationPhase.ActivatingTarget target when target.RequestId == requestId =>
                HandleTargetSurfaceAck(target, result),
            ActivationPhase.ActivatingTarget target when target.FocusRequestId == requestId =>
                HandleFocusAck(target, endpoints),
            ActivationPhase.ReleasingTargetForRollback => HandleTargetReleaseAck(endpoints),
            ActivationPhase.RestoringSource restore => HandleRestoreAck(restore, result),
            ActivationPhase.SynchronizingPresentation sync => HandleSyncAck(sync, result),
            ActivationPhase.AwaitingPresentationEffects => new SurfaceActivationProgress.Rejected(
                result.ErrorMessage ?? "endpoint presentation effects fence rejected",
                false),
            _ => new SurfaceActivationProgress.Stale(),
        };
    }

    public SurfaceActivationProgress ReceiveSnapshot(
        string endpointId,
        ulong generation,
        AttachSnapshotEvidence snapshot)
    {
        var lease = Phase switch
        {
            ActivationPhase.ActivatingTarget => Target,
            ActivationPhase.RestoringSource => Source,
            ActivationPhase.SynchronizingPresentation sync => sync.Lease,
            _ => null,
        };
        if (lease is null
            || !EndpointMatches(lease, endpointId, generation, snapshot.BootId)
            || snapshot.Revision < lease.MinimumProjectionRevision)
            return new SurfaceActivationProgress.Stale();

        var evidence = Phase switch
        {
            ActivationPhase.ActivatingTarget target
                when EndpointMatches(Target, endpointId, generation, snapshot.BootId) => target.Evidence,
            ActivationPhase.RestoringSource restore
                when EndpointMatches(Source, endpointId, generation, snapshot.BootId) => restore.Evidence,
            ActivationPhase.SynchronizingPresentation sync
                when EndpointMatches(sync.Lease, endpointId, generation, snapshot.BootId) => sync.Evidence,
            _ => null,
        };
        if (evidence is null)
            return new SurfaceActivationProgress.Stale();

        evidence.RecordSnapshot(endpointId, generation, snapshot);
        return Progress();
    }

    public SurfaceActivationProgress ReceiveSurface(
        string endpointId,
        ulong generation,
        AttachSurfaceEvidence surface)
    {
        var lease = Phase switch
        {
            ActivationPhase.ActivatingTarget => Target,
            ActivationPhase.RestoringSource => Source,
            ActivationPhase.SynchronizingPresentation sync => sync.Lease,
            _ => null,
        };
        if (lease is null
            || !EndpointMatches(lease, endpointId, generation, surface.BootId)
            || surface.ProjectionRevision < lease.MinimumProjectionRevision)
            return new SurfaceActivationProgress.Stale();
        if (surface.Columns != Resize.Columns || surface.Rows != Resize.Rows)
            return new SurfaceActivationProgress.Pending();

        switch (Phase)
        {
            case ActivationPhase.ActivatingTarget target:
                target.Evidence.RecordSurface(surface);
                break;
            case ActivationPhase.RestoringSource restore:
                restore.Evidence.RecordSurface(surface);
                break;
            case ActivationPhase.SynchronizingPresentation sync:
                sync.Evidence.RecordSurface(surface);
                break;
            default:
                return new SurfaceActivationProgress.Stale();
        }

        return Progress();
    }

    public SurfaceActivationProgress ReceivePresentationEffectsReady(
        string endpointId,
        ulong generation,
        string token)
    {
        if (Phase is not ActivationPhase.AwaitingPresentationEffects awaiting)
            return new SurfaceActivationProgress.Stale();
        if (!EndpointMatches(awaiting.Lease, endpointId, generation, awaiting.Lease.BootId)
            || awaiting.Token != token)
            return new SurfaceActivationProgress.Stale();

        Phase = awaiting with { Ready = true };
        return new SurfaceActivationProgress.Ready();
    }

    public Result<Unit, string> Retarget(EndpointFocusTarget? focus, IEndpointRegistryPort endpoints)
    {
        Focus = focus;
        if (Phase is ActivationPhase.ActivatingTarget target)
        {
            var noFocus = focus is null && target.FocusRequestId is null;
            Phase = target with { FocusAcknowledged = noFocus };
        }

        return SendLatestFocus(endpoints);
    }

    public Result<Unit, string> UpdateResize(AttachGeometry geometry, IEndpointRegistryPort endpoints)
    {
        if (geometry.Columns < 1 || geometry.Rows < 1)
            return Result<Unit, string>.Fail("endpoint activation did not include a surface resize");

        Resize = geometry;
        if (Phase is ActivationPhase.AwaitingPresentationEffects awaiting)
        {
            if (endpoints.SendTo(awaiting.Lease.EndpointId, ResizeFrame(geometry))
                != EndpointSendOutcome.Sent)
                return Result<Unit, string>.Fail("pending endpoint resize could not be sent");
            return StartPresentationSync(endpoints, awaiting.Lease, awaiting.Completion);
        }

        InvalidateCurrentEvidence();
        var destination = Phase switch
        {
            ActivationPhase.ActivatingTarget => Target.EndpointId,
            ActivationPhase.RestoringSource => Source.EndpointId,
            ActivationPhase.SynchronizingPresentation sync => sync.Lease.EndpointId,
            _ => null,
        };
        if (destination is not null
            && endpoints.SendTo(destination, ResizeFrame(geometry))
                != EndpointSendOutcome.Sent)
            return Result<Unit, string>.Fail("pending endpoint resize could not be sent");

        return Result<Unit, string>.Ok(default);
    }

    public ActivationRollback EndpointDisconnected(
        IEndpointRegistryPort endpoints,
        string endpointId,
        string error)
    {
        _rollbackError = error;
        // Target loss
        // calls start_source_restore unless the phase is already
        // RestoringSource. There is no source_available test. The
        // "previous endpoint is no longer connected" sentence is the
        // rollback branch at activation.rs:768-774.
        if (string.Equals(Target.EndpointId, endpointId, StringComparison.Ordinal)
            && !string.Equals(Source.EndpointId, endpointId, StringComparison.Ordinal))
        {
            if (Phase is ActivationPhase.RestoringSource)
                return new ActivationRollback.Pending();
            var restore = StartSourceRestore(endpoints);
            return restore.IsOk
                ? new ActivationRollback.Pending()
                : new ActivationRollback.Unavailable(
                    $"{error}; source endpoint could not be restored safely: {restore.Error}");
        }

        if (!string.Equals(Source.EndpointId, endpointId, StringComparison.Ordinal))
            return new ActivationRollback.Unavailable(error);

        SourceAvailable = false;
        if (!string.Equals(Source.EndpointId, Target.EndpointId, StringComparison.Ordinal))
        {
            if (Phase is ActivationPhase.ReleasingSource)
            {
                var start = StartTarget(endpoints);
                return start.IsOk
                    ? new ActivationRollback.Pending()
                    : new ActivationRollback.Unavailable(start.Error);
            }

            if (Phase is ActivationPhase.ActivatingTarget)
                return new ActivationRollback.Pending();

            if (Phase is ActivationPhase.SynchronizingPresentation sync
                && string.Equals(sync.Lease.EndpointId, Target.EndpointId, StringComparison.Ordinal))
                return new ActivationRollback.Pending();

            if (Phase is ActivationPhase.AwaitingPresentationEffects awaiting
                && string.Equals(awaiting.Lease.EndpointId, Target.EndpointId, StringComparison.Ordinal))
                return new ActivationRollback.Pending();
        }

        return Phase switch
        {
            ActivationPhase.ReleasingSource => new ActivationRollback.Unavailable(error),
            ActivationPhase.ActivatingTarget => TargetReleaseRollback(endpoints, error),
            ActivationPhase.ReleasingTargetForRollback => new ActivationRollback.Pending(),
            ActivationPhase.RestoringSource => new ActivationRollback.Unavailable(error),
            ActivationPhase.SynchronizingPresentation sync
                when string.Equals(sync.Lease.EndpointId, Target.EndpointId, StringComparison.Ordinal) =>
                TargetReleaseRollback(endpoints, error),
            ActivationPhase.AwaitingPresentationEffects awaiting
                when string.Equals(awaiting.Lease.EndpointId, Target.EndpointId, StringComparison.Ordinal) =>
                TargetReleaseRollback(endpoints, error),
            ActivationPhase.SynchronizingPresentation
                or ActivationPhase.AwaitingPresentationEffects =>
                new ActivationRollback.Unavailable(error),
            _ => new ActivationRollback.Unavailable(error),
        };
    }

    private ActivationRollback TargetReleaseRollback(IEndpointRegistryPort endpoints, string error)
    {
        var release = StartTargetRelease(endpoints);
        return release.IsOk
            ? new ActivationRollback.Pending()
            : new ActivationRollback.Unavailable(
                $"{error}; target endpoint could not be released safely: {release.Error}");
    }

    public ActivationRollback Rollback(
        IEndpointRegistryPort endpoints,
        string error,
        bool sourceReleaseRejected)
    {
        _rollbackError = error;
        if (Phase is ActivationPhase.ReleasingSource && sourceReleaseRejected)
        {
            return StartSourceRestore(endpoints) is { IsOk: true }
                ? new ActivationRollback.Pending()
                : new ActivationRollback.Unavailable(
                    $"{error}; source endpoint could not resume: restore failed");
        }

        Result<Unit, string> result = Phase switch
        {
            ActivationPhase.ReleasingSource => SourceAvailable
                ? StartSourceRestore(endpoints)
                : Result<Unit, string>.Fail("the previous endpoint is no longer connected"),
            ActivationPhase.ActivatingTarget => StartTargetRelease(endpoints),
            ActivationPhase.ReleasingTargetForRollback => HandleUnacknowledgedTargetRelease(endpoints, error),
            ActivationPhase.RestoringSource => Result<Unit, string>.Fail("source endpoint could not be restored"),
            ActivationPhase.SynchronizingPresentation or ActivationPhase.AwaitingPresentationEffects
                when LeaseFromPhase() is { } lease
                && string.Equals(lease.EndpointId, Target.EndpointId, StringComparison.Ordinal) =>
                StartTargetRelease(endpoints),
            ActivationPhase.SynchronizingPresentation or ActivationPhase.AwaitingPresentationEffects =>
                Result<Unit, string>.Fail("source endpoint presentation could not be synchronized"),
            _ => Result<Unit, string>.Fail("invalid rollback phase"),
        };

        return result switch
        {
            { IsOk: true } => new ActivationRollback.Pending(),
            { IsOk: false } => new ActivationRollback.Unavailable(
                Phase is ActivationPhase.ReleasingTargetForRollback
                    ? $"{error}; the target connection was closed because no presentation owner could be proven"
                    : $"{error}; source endpoint could not be restored safely: {result.Error}"),
        };
    }

    public Result<ActivationCompletion, string> Complete(
        AttachLiveState live,
        IEndpointRegistryPort endpoints)
    {
        if (Phase is ActivationPhase.SynchronizingPresentation sync)
        {
            if (sync.AcknowledgedRevision is { } syncAck
                && live.InstalledSurfaceSatisfies(sync.Lease, syncAck, Resize))
            {
                // dest attach surface lease is not the input lease, so the
                // effects fence cannot be proven. The activation already
                // installed matching cells at this geometry.
                return Result<ActivationCompletion, string>.Ok(sync.Completion);
            }

            var syncSurface = TryCoherentCompletionSurface(live, sync.Lease, sync.Evidence, sync.AcknowledgedRevision);
            if (!syncSurface.IsOk)
                return Result<ActivationCompletion, string>.Fail(syncSurface.Error);
            if (!string.Equals(endpoints.ActiveId, sync.Lease.EndpointId, StringComparison.Ordinal)
                || !live.EndpointProjectionAvailable(sync.Lease.EndpointId)
                || !AttachSession.TryActivateEndpointProjection(live, sync.Lease.EndpointId))
            {
                return Result<ActivationCompletion, string>.Fail(
                    "endpoint became unavailable during presentation synchronization");
            }

            live.SetPaneSurface(syncSurface.Value, sync.Lease);
            return StartPresentationEffectsFence(endpoints, sync.Lease, sync.Completion)
                .Map(_ => (ActivationCompletion)new ActivationCompletion.AwaitingPresentationEffects());
        }

        if (Phase is ActivationPhase.AwaitingPresentationEffects { Ready: true } awaiting)
            return Result<ActivationCompletion, string>.Ok(awaiting.Completion);

        var (lease, evidence, ack, completion) = Phase switch
        {
            ActivationPhase.ActivatingTarget target => (
                Target,
                target.Evidence,
                target.AcknowledgedRevision,
                (ActivationCompletion)new ActivationCompletion.Activated()),
            ActivationPhase.RestoringSource restore => (
                Source,
                restore.Evidence,
                restore.AcknowledgedRevision,
                (ActivationCompletion)new ActivationCompletion.RestoredSource(
                    _rollbackError ?? "endpoint handoff was rolled back",
                    Successor)),
            _ => (null, null, null, null),
        };

        if (lease is null || evidence is null || completion is null)
            return Result<ActivationCompletion, string>.Fail(
                "endpoint activation completed in an invalid phase");

        var coherent = TryCoherentCompletionSurface(live, lease, evidence, ack);
        if (!coherent.IsOk)
            return Result<ActivationCompletion, string>.Fail(coherent.Error);

        endpoints.SetSurfaceActive(lease.EndpointId, true);
        live.SetEndpointStatus(lease.EndpointId, ClientEndpointStatus.Online);
        live.StatusError = null;
        if (!live.EndpointProjectionAvailable(lease.EndpointId)
            || !endpoints.SetActive(lease.EndpointId))
        {
            return Result<ActivationCompletion, string>.Fail(
                "endpoint became unavailable during activation");
        }

        if (!AttachSession.TryActivateEndpointProjection(live, lease.EndpointId))
        {
            return Result<ActivationCompletion, string>.Fail(
                "endpoint became unavailable during activation");
        }

        live.SetPaneSurface(coherent.Value, lease);
        var started = StartPresentationSync(endpoints, lease, completion);
        if (!started.IsOk)
            return Result<ActivationCompletion, string>.Fail(started.Error);
        if (completion is ActivationCompletion.Activated)
            StageClock?.Stamp(CubesConnectStages.Ready);
        return started.Map(_ => (ActivationCompletion)new ActivationCompletion.AwaitingPresentationSync(
            Source.EndpointId,
            lease.EndpointId));
    }

    private SurfaceActivationProgress HandleSourceReleaseAck(IEndpointRegistryPort endpoints)
    {
        StageClock?.Stamp(CubesConnectStages.SourceReleased);
        var start = StartTarget(endpoints);
        if (!start.IsOk)
        {
            return new SurfaceActivationProgress.Rejected(start.Error, false);
        }

        return new SurfaceActivationProgress.Pending();
    }

    private SurfaceActivationProgress HandleTargetSurfaceAck(
        ActivationPhase.ActivatingTarget target,
        EndpointSurfaceControlResult result)
    {
        // per-client floor.
        // the lease minimum are Stale. Seed dest min at 0. Raise on dest
        // surface-on ack. Do not reseed on dest boot change.
        if (result.ProjectionRevision > Target.MinimumProjectionRevision
            || (!string.IsNullOrWhiteSpace(result.LeaseId)
                && !string.Equals(Target.LeaseId, result.LeaseId, StringComparison.Ordinal)))
        {
            Target = Target with
            {
                MinimumProjectionRevision = result.ProjectionRevision > Target.MinimumProjectionRevision
                    ? result.ProjectionRevision
                    : Target.MinimumProjectionRevision,
                LeaseId = string.IsNullOrWhiteSpace(result.LeaseId) ? Target.LeaseId : result.LeaseId,
            };
        }
        Phase = target with { AcknowledgedRevision = result.ProjectionRevision };
        return Progress();
    }

    private SurfaceActivationProgress HandleFocusAck(
        ActivationPhase.ActivatingTarget target,
        IEndpointRegistryPort endpoints)
    {
        if (target.FocusRequestTarget is null)
            return new SurfaceActivationProgress.Stale();

        var requested = target.FocusRequestTarget;
        Phase = target with
        {
            FocusRequestId = null,
            FocusRequestTarget = null,
        };

        if (Focus?.Kind != requested.Kind || Focus?.Id != requested.Id)
        {
            Phase = ((ActivationPhase.ActivatingTarget)Phase) with { FocusAcknowledged = false };
            var send = SendLatestFocus(endpoints);
            if (!send.IsOk)
                return new SurfaceActivationProgress.Rejected(send.Error, false);
            return Progress();
        }

        Phase = ((ActivationPhase.ActivatingTarget)Phase) with { FocusAcknowledged = true };
        return Progress();
    }

    private SurfaceActivationProgress HandleTargetReleaseAck(IEndpointRegistryPort endpoints)
    {
        endpoints.SetSurfaceActive(Target.EndpointId, false);
        if (!SourceAvailable)
        {
            return new SurfaceActivationProgress.Rejected(
                _rollbackError ?? "the previous endpoint is no longer connected",
                false);
        }

        var restore = StartSourceRestore(endpoints);
        if (!restore.IsOk)
            return new SurfaceActivationProgress.Rejected(restore.Error, false);
        return new SurfaceActivationProgress.Pending();
    }

    private SurfaceActivationProgress HandleRestoreAck(
        ActivationPhase.RestoringSource restore,
        EndpointSurfaceControlResult result)
    {
        Phase = restore with { AcknowledgedRevision = result.ProjectionRevision };
        return Progress();
    }

    private SurfaceActivationProgress HandleSyncAck(
        ActivationPhase.SynchronizingPresentation sync,
        EndpointSurfaceControlResult result)
    {
        Phase = sync with { AcknowledgedRevision = result.ProjectionRevision };
        return Progress();
    }

    /// Phase becomes <c>ActivatingTarget</c>, then
    /// <c>send_surface_activation</c> (<c>protocol.rs:116-140</c>) writes
    /// resize, surface-on, and the focus baseline to the target lease.
    internal Result<Unit, string> StartTarget(IEndpointRegistryPort endpoints)
    {
        StageClock?.Stamp(CubesConnectStages.TargetStarted);
        var requestId = SurfaceRequestId(Epoch, "on");
        Deadline = DateTimeOffset.UtcNow + ActivationTimeout;
        Phase = new ActivationPhase.ActivatingTarget(
            requestId,
            null,
            null,
            null,
            Focus is null,
            new EndpointActivationEvidence());

        var activation = SendSurfaceActivation(endpoints, Target, requestId, Resize, HostFocused);
        if (!activation.IsOk)
            return activation;

        return SendLatestFocus(endpoints);
    }

    private Result<Unit, string> StartPresentationSync(
        IEndpointRegistryPort endpoints,
        EndpointActivationLease lease,
        ActivationCompletion completion)
    {
        var requestId = SurfaceRequestId(Epoch, "presentation-sync");
        Phase = new ActivationPhase.SynchronizingPresentation(
            lease,
            requestId,
            null,
            new EndpointActivationEvidence(),
            completion);
        Deadline = DateTimeOffset.UtcNow + ActivationTimeout;
        if (endpoints.SendTo(
                lease.EndpointId,
                new EndpointActivationMessage.PresentationSync(requestId))
            != EndpointSendOutcome.Sent)
            return Result<Unit, string>.Fail("endpoint presentation synchronization could not be sent");
        return Result<Unit, string>.Ok(default);
    }

    private Result<Unit, string> StartPresentationEffectsFence(
        IEndpointRegistryPort endpoints,
        EndpointActivationLease lease,
        ActivationCompletion completion)
    {
        var token = $"{Epoch}:{lease.ConnectionGeneration}:{lease.BootId}";
        Phase = new ActivationPhase.AwaitingPresentationEffects(lease, token, false, completion);
        Deadline = DateTimeOffset.UtcNow + ActivationTimeout;
        if (endpoints.SendTo(
                lease.EndpointId,
                new EndpointActivationMessage.PresentationEffectsFence(token))
            != EndpointSendOutcome.Sent)
            return Result<Unit, string>.Fail("endpoint presentation effects fence could not be sent");
        return Result<Unit, string>.Ok(default);
    }

    private Result<Unit, string> StartTargetRelease(IEndpointRegistryPort endpoints)
    {
        var requestId = SurfaceRequestId(Epoch, "rollback-target-off");
        Phase = new ActivationPhase.ReleasingTargetForRollback(requestId);
        Deadline = DateTimeOffset.UtcNow + ActivationTimeout;
        if (endpoints.SendTo(
                Target.EndpointId,
                new EndpointActivationMessage.SurfaceInterest(requestId, false))
            != EndpointSendOutcome.Sent)
            return Result<Unit, string>.Fail("target endpoint release could not be sent");
        return Result<Unit, string>.Ok(default);
    }

    /// <c>start_source_restore</c>. Phase is RestoringSource before
    /// <c>send_surface_activation</c>. The request id is
    /// <c>client-shell-surface:{epoch}:rollback-source-on</c>.
    private Result<Unit, string> StartSourceRestore(IEndpointRegistryPort endpoints)
    {
        var requestId = SurfaceRequestId(Epoch, "rollback-source-on");
        Phase = new ActivationPhase.RestoringSource(
            requestId,
            null,
            new EndpointActivationEvidence());
        Deadline = DateTimeOffset.UtcNow + ActivationTimeout;
        return SendSurfaceActivation(endpoints, Source, requestId, Resize, HostFocused);
    }

    private Result<Unit, string> SendLatestFocus(IEndpointRegistryPort endpoints)
    {
        if (Focus is null)
        {
            if (Phase is ActivationPhase.ActivatingTarget target && target.FocusRequestId is null)
                Phase = target with { FocusAcknowledged = true };
            return Result<Unit, string>.Ok(default);
        }

        if (Phase is not ActivationPhase.ActivatingTarget { FocusRequestId: null })
            return Result<Unit, string>.Ok(default);

        var requestId = NextFocusRequestId();
        if (requestId is null)
            return Result<Unit, string>.Ok(default);

        Phase = ((ActivationPhase.ActivatingTarget)Phase) with
        {
            FocusAcknowledged = false,
            FocusRequestId = requestId,
            FocusRequestTarget = Focus,
        };

        if (endpoints.SendTo(
                Target.EndpointId,
                new EndpointActivationMessage.NavigationFocus(requestId, Focus))
            != EndpointSendOutcome.Sent)
            return Result<Unit, string>.Fail("endpoint focus could not be sent");
        return Result<Unit, string>.Ok(default);
    }

    internal SurfaceActivationProgress CurrentProgress() => Progress();

    internal string DumpFocusAcknowledged() =>
        Phase is ActivationPhase.ActivatingTarget target
            ? (target.FocusAcknowledged ? "1" : "0")
            : "n/a";

    internal string DumpTargetMatches()
    {
        if (Phase is not ActivationPhase.ActivatingTarget target)
            return "n/a";
        if (target.AcknowledgedRevision is not { } revision
            || target.Evidence.CoherentSurface(revision, Resize) is not { } surface)
            return Focus is null ? "1" : "0";
        return TargetMatches(target.Evidence, surface) ? "1" : "0";
    }

    private SurfaceActivationProgress Progress()
    {
        return Phase switch
        {
            ActivationPhase.ActivatingTarget target
                when target.AcknowledgedRevision is { } revision
                && target.FocusAcknowledged
                && target.Evidence.CoherentSurface(revision, Resize) is { } surface
                && TargetMatches(target.Evidence, surface) =>
                new SurfaceActivationProgress.Ready(),
            ActivationPhase.RestoringSource restore
                when restore.AcknowledgedRevision is { } revision
                && restore.Evidence.CoherentSurface(revision, Resize) is not null =>
                new SurfaceActivationProgress.Ready(),
            ActivationPhase.SynchronizingPresentation sync
                when sync.AcknowledgedRevision is { } syncRevision
                && sync.Evidence.CoherentSurface(syncRevision, Resize) is not null =>
                new SurfaceActivationProgress.Ready(),
            _ => new SurfaceActivationProgress.Pending(),
        };
    }

    private Result<Unit, string> HandleUnacknowledgedTargetRelease(
        IEndpointRegistryPort endpoints,
        string error)
    {
        endpoints.Fail(Target.EndpointId, "endpoint did not acknowledge surface revocation");
        if (!SourceAvailable)
            return Result<Unit, string>.Fail(error);
        return StartSourceRestore(endpoints);
    }

    private void InvalidateCurrentEvidence()
    {
        switch (Phase)
        {
            case ActivationPhase.ActivatingTarget target:
                target.Evidence.InvalidateSurface();
                break;
            case ActivationPhase.RestoringSource restore:
                restore.Evidence.InvalidateSurface();
                break;
            case ActivationPhase.SynchronizingPresentation sync:
                sync.Evidence.InvalidateSurface();
                break;
        }
    }

    private Result<AttachSurfaceEvidence, string> TryCoherentCompletionSurface(
        AttachLiveState live,
        EndpointActivationLease lease,
        EndpointActivationEvidence evidence,
        ulong? acknowledgementRevision)
    {
        if (acknowledgementRevision is null)
            return Result<AttachSurfaceEvidence, string>.Fail(
                "endpoint activation completed without a surface acknowledgement");
        if (!evidence.HasCoherentSnapshotSurfacePair())
            return Result<AttachSurfaceEvidence, string>.Fail(
                "endpoint activation lost its coherent snapshot/surface pair");
        var surface = evidence.CoherentSurface(acknowledgementRevision.Value, Resize);
        if (surface is null || surface.ProjectionRevision < acknowledgementRevision.Value)
            return Result<AttachSurfaceEvidence, string>.Fail(
                "endpoint activation lost its acknowledged surface evidence");
        if (!evidence.EndpointSnapshotMatches(lease, surface, Resize))
            return Result<AttachSurfaceEvidence, string>.Fail(
                "endpoint activation lost its coherent snapshot/surface pair");
        if (!EndpointActivationProjection.LiveEndpointSnapshotMatches(live, lease, surface))
            return Result<AttachSurfaceEvidence, string>.Fail(
                "endpoint activation lost its coherent snapshot/surface pair");
        return Result<AttachSurfaceEvidence, string>.Ok(surface);
    }

    private bool TargetMatches(EndpointActivationEvidence evidence, AttachSurfaceEvidence surface)
    {
        if (Focus is null)
            return true;
        return Focus.Kind switch
        {
            EndpointFocusTargetKind.Pane =>
                string.Equals(evidence.PaneId, Focus.Id, StringComparison.Ordinal)
                && surface.Focused
                && string.Equals(surface.FocusedPaneId, Focus.Id, StringComparison.Ordinal),
            EndpointFocusTargetKind.Tab =>
                string.Equals(evidence.TabId, Focus.Id, StringComparison.Ordinal),
            EndpointFocusTargetKind.Workspace =>
                string.Equals(evidence.WorkspaceId, Focus.Id, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <c>send_surface_activation</c>. Resize, then surface-on, then focus
    /// baseline. Each <c>send_to</c> must be accepted before the next. The
    /// function returns before any of those replies.
    private Result<Unit, string> SendSurfaceActivation(
        IEndpointRegistryPort endpoints,
        EndpointActivationLease target,
        string requestId,
        AttachGeometry geometry,
        bool focused)
    {
        if (endpoints.SendTo(target.EndpointId, ResizeFrame(geometry))
            != EndpointSendOutcome.Sent)
            return Result<Unit, string>.Fail("endpoint resize could not be sent");

        if (endpoints.SendTo(
                target.EndpointId,
                new EndpointActivationMessage.SurfaceInterest(requestId, true))
            != EndpointSendOutcome.Sent)
            return Result<Unit, string>.Fail("endpoint activation could not be sent");

        if (endpoints.SendTo(
                target.EndpointId,
                new EndpointActivationMessage.HostFocusBaseline(focused, FocusBaselineId()))
            != EndpointSendOutcome.Sent)
            return Result<Unit, string>.Fail("endpoint focus baseline could not be sent");

        return Result<Unit, string>.Ok(default);
    }

    /// The id written here is the id a later reply carries.
    private EndpointActivationMessage.Resize ResizeFrame(AttachGeometry geometry) =>
        new(geometry, $"client-shell-resize:{Epoch}:{geometry.GeometryRevision}");

    private string FocusBaselineId() => $"client-shell-focus:{Epoch}:baseline";

    private string? NextFocusRequestId()
    {
        if (Focus is null)
            return null;
        _nextFocusSerial = checked(_nextFocusSerial + 1);
        return $"client-shell-focus:{Epoch}:{_nextFocusSerial}";
    }

    private static string SurfaceRequestId(ulong epoch, string suffix) =>
        $"client-shell-surface:{epoch}:{suffix}";

    private static EndpointActivationLease DisconnectedLease(string endpointId, string clientId) =>
        new()
        {
            EndpointId = endpointId,
            ConnectionGeneration = 0,
            BootId = string.Empty,
            MinimumProjectionRevision = 0,
            ClientId = clientId,
            LeaseId = string.Empty,
        };

    private static bool EndpointMatches(
        EndpointActivationLease lease,
        string endpointId,
        ulong generation,
        string bootId) =>
        string.Equals(lease.EndpointId, endpointId, StringComparison.Ordinal)
        && lease.ConnectionGeneration == generation
        && string.Equals(lease.BootId, bootId, StringComparison.Ordinal);

    private EndpointActivationLease? LeaseFromPhase() =>
        Phase switch
        {
            ActivationPhase.SynchronizingPresentation sync => sync.Lease,
            ActivationPhase.AwaitingPresentationEffects awaiting => awaiting.Lease,
            _ => null,
        };

    private static void BuildSurfaceInterestRequest(string bootId, string requestId, bool active)
    {
        if (string.IsNullOrWhiteSpace(bootId))
            throw new InvalidOperationException("endpoint boot id is missing");
        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("surface request id is missing");
        _ = active;
    }

    private static void BuildFocusRequest(string bootId, string requestId, EndpointFocusTarget target)
    {
        if (string.IsNullOrWhiteSpace(bootId))
            throw new InvalidOperationException("endpoint boot id is missing");
        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("focus request id is missing");
        if (string.IsNullOrWhiteSpace(target.Id))
            throw new InvalidOperationException("focus target id is missing");
    }
}
