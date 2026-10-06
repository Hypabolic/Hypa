using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Cli.Attach;

public sealed partial class AttachSession
{
    internal static async Task ApplyCubesConnectAsync(
        MouseEngineResult result,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        control = LiveControlPort(live, control);

        var placementId = result.PlacementId;
        if (string.IsNullOrWhiteSpace(placementId))
            return;

        var cube = live.Cubes.FirstOrDefault(
            item => string.Equals(item.Id, placementId, StringComparison.Ordinal));
        if (cube is null)
            return;

        // pending activation; shell_runtime.rs:721-724 skip other host effects.
        // A queued local cube is not a retarget and must not reset the latch.
        if (cube.Kind == SidebarCubeKind.Local
            && (BlocksPresentationInput(live) || live.PendingActivation is not null))
            return;
        if (BlocksPresentationInput(live) && live.PendingActivation is null)
            return;

        // Local restores log no stages, so they get no clock.
        var stageWatch = cube.Kind == SidebarCubeKind.Local ? null : Stopwatch.StartNew();
        AttachProcessLog.CubesRequested(
            live.ProcessLog,
            "connect",
            live.SessionName,
            live.AttachClientId);
        live.CubesConnectOutcomeRecorded = false;

        if (cube.Kind == SidebarCubeKind.Local)
        {

            await RestoreLocalCubeProjectionAsync(live, cube, control, tty, ct)
                .ConfigureAwait(false);
            RecordCubesConnectOutcome(live, ProcessLogEvents.OutcomeOk);
            return;
        }

        if (live.CubesConnect is not { } cubesConnect)
        {
            RecordCubesConnectOutcome(live, ProcessLogEvents.OutcomeRejected);

            return;
        }

        var intent = new EndpointActivationIntent
        {
            EndpointId = placementId,
            StageClock = new CubesConnectStageClock(
                stageWatch!,
                live.ProcessLog,
                placementId,
                live.SessionName,
                live.AttachClientId),
        };
        await BeginEndpointActivationAsync(live, cube, intent, control, tty, ct).ConfigureAwait(false);
        if (tty is not null && live.ChromeEnabled)
            PaintChrome(tty, live);
    }

    internal static async Task BeginEndpointActivationAsync(
        AttachLiveState live,
        SidebarCubeItem cube,
        EndpointActivationIntent intent,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct,
        bool force = false,
        bool restore = false)
    {
        string? pendingOutcome = null;
        lock (live.ActivationGate)
        {
            if (live.PendingActivation is { } pending)
            {
                if (pending.CanRetarget(intent.EndpointId))
                {
                    var port = CreateRegistryPort(live, pending);
                    var retarget = pending.Retarget(intent.FocusTarget(), port);
                    if (!retarget.IsOk)
                    {
                        RollbackEndpointActivation(live, retarget.Error, false);
                        pendingOutcome = ProcessLogEvents.OutcomeError;
                    }
                    else
                    {
                        pendingOutcome = ProcessLogEvents.OutcomeOk;
                    }
                }
                else
                {
                    var port = CreateRegistryPort(live, pending);
                    var supersede = pending.Supersede(intent, port);
                    if (supersede is ActivationRollback.Unavailable unavailable)
                    {
                        live.PendingActivation = null;
                        PresentHandoffUnavailable(live, unavailable.Message);
                        pendingOutcome = ProcessLogEvents.OutcomeError;
                    }
                    else
                    {
                        pendingOutcome = ProcessLogEvents.OutcomeRejected;
                    }
                }
            }
        }

        if (pendingOutcome is not null)
        {
            RecordCubesConnectOutcome(live, pendingOutcome);
            return;
        }

        var reconnectDead = cube.Kind != SidebarCubeKind.Local
            && string.Equals(live.ConnectedPlacementId, intent.EndpointId, StringComparison.Ordinal)
            && live.PendingActivation is null
            && !CommittedEndpointIsLive(live, intent.EndpointId);
        if (!force
            && !reconnectDead
            && string.Equals(live.ConnectedPlacementId, intent.EndpointId, StringComparison.Ordinal)
            && live.GetEndpointSurfaceActive(intent.EndpointId)
            && live.PendingActivation is null)
        {
            if (intent.FocusTarget() is { } focus)
            {
                var port = CreateRegistryPortForCommitted(live);
                var requestId = $"client-shell-focus:{live.NextSurfaceSerial}:already-active";
                _ = port.SendTo(
                    intent.EndpointId,
                    new EndpointActivationMessage.NavigationFocus(requestId, focus));
            }

            if (tty is not null && live.ChromeEnabled)
                PaintChrome(tty, live);
            RecordCubesConnectOutcome(live, ProcessLogEvents.OutcomeOk);
            return;
        }

        if (cube.Kind == SidebarCubeKind.Local)
        {
            await RestoreLocalCubeProjectionAsync(live, cube, control, tty, ct)
                .ConfigureAwait(false);
            return;
        }

        if (live.CubesConnect is null)
            return;

        if (ct.IsCancellationRequested)
        {
            RecordCubesConnectOutcome(live, ProcessLogEvents.OutcomeRejected);
            return;
        }

        _ = restore;

        if (reconnectDead && live.Cubes.FirstOrDefault(item => item.Kind == SidebarCubeKind.Local) is { } local)
        {
            // The peer went away (reboot, network loss) and its transport
            // closed. Leave the dead view, then dial the peer again.
            await RestoreLocalCubeProjectionAsync(live, local, control, tty, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                RecordCubesConnectOutcome(live, ProcessLogEvents.OutcomeRejected);
                return;
            }
        }

        // spawn_blocking. Dest resolver, ConnectAsync, preflight, and lease
        // claim leave the input task. Completion is a pump event
        ScheduleDestConnectPrep(live, cube, intent, control, tty, ct);
    }

    /// Identical dest shares the active prep. Local, detach, and a newer dest
    /// retire it. A stale completion disposes dest and does not install.
    private static void ScheduleDestConnectPrep(
        AttachLiveState live,
        SidebarCubeItem cube,
        EndpointActivationIntent intent,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        if (live.CubesConnect is not { } cubesConnect)
            return;

        CancellationTokenSource cancel;
        ulong generation;
        TaskCompletionSource tcs;
        lock (live.ActivationGate)
        {
            if (live.DestConnectAttempt is { } inflight
                && string.Equals(inflight.EndpointId, cube.Id, StringComparison.Ordinal))
            {
                return;
            }

            // generation. Retire bumps DestConnectGeneration even when the
            // attempt is already null so Local / detach / a newer dest can
            // still invalidate a completion that has not committed.
            RetireDestConnectAttemptLocked(live);
            generation = live.DestConnectGeneration;
            cancel = new CancellationTokenSource();
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            live.DestConnectAttempt = new AttachDestConnectAttempt
            {
                Generation = generation,
                EndpointId = cube.Id,
                PrepTask = tcs.Task,
                Cancel = cancel,
            };
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunDestConnectPrepAsync(
                        live,
                        cubesConnect,
                        cube,
                        intent,
                        control,
                        tty,
                        ct,
                        generation,
                        cancel.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                tcs.TrySetResult();
                try
                {
                    cancel.Dispose();
                }
                catch
                {
                    /* ignore */
                }
            }
        });
    }

    private static async Task RunDestConnectPrepAsync(
        AttachLiveState live,
        ICubesConnectRetargeter cubesConnect,
        SidebarCubeItem cube,
        EndpointActivationIntent intent,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken sessionCt,
        ulong generation,
        CancellationToken attemptCt)
    {
        var config = live.AttachConfig ?? AttachClientConfig.Default;
        var hypaEnv = live.AttachEnvironment?.GetVariable(NestedAttachGuard.EnvName)
            ?? Environment.GetEnvironmentVariable(NestedAttachGuard.EnvName);
        CubesConnectSshAttempt? sshAttempt = IsSshCube(cube) ? new CubesConnectSshAttempt() : null;
        var hostGeometry = BuildAttachHostGeometry(live);
        var sourceEndpointId = live.ConnectedPlacementId;
        var connectRequest = BuildConnectRequest(
            live,
            cube,
            control,
            config,
            hypaEnv,
            sshAttempt,
            hostGeometry,
            sourceEndpointId,
            intent.StageClock) with
        {
            StageClock = intent.StageClock,
        };

        CubesConnectRetargetOutcome? outcome = null;
        string? timeoutError = null;
        var canceled = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionCt, attemptCt);
            outcome = await cubesConnect.ConnectAsync(connectRequest, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = sessionCt.IsCancellationRequested || attemptCt.IsCancellationRequested;
            if (!canceled)
                timeoutError = "destination connect timed out";
        }
        catch (Exception ex)
        {
            timeoutError = FormatStatus(ex.Message);
        }

        if (attemptCt.IsCancellationRequested || sessionCt.IsCancellationRequested)
            canceled = true;

        var completion = new AttachDestConnectCompletion
        {
            Generation = generation,
            Cube = cube,
            Intent = intent,
            Request = connectRequest,
            HostGeometry = hostGeometry,
            Control = control,
            SessionCt = sessionCt,
            Outcome = outcome,
            TimeoutError = timeoutError,
            Canceled = canceled,
            Tty = tty,
        };
        lock (live.ActivationGate)
        {
            live.DestConnectCompletions.Enqueue(completion);
            (connectRequest.StageClock ?? intent.StageClock)?.Stamp(CubesConnectStages.Queued);
        }

        live.SignalActivationPump();
    }

    internal static void RetireDestConnectAttempt(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        lock (live.ActivationGate)
            RetireDestConnectAttemptLocked(live);
    }

    /// task does not send Connected (<c>supervisor.rs:180-182</c>). The
    /// spawn task still owns the result and drops the stream. Attach Run
    /// must await that owner and drain leftover completions because the
    /// render reader can return on detach before the pump runs.
    /// event consumer on the client loop. After that loop returns
    /// (<c>mod.rs:2050-2055</c>) there is no second drain. Hypa shutdown
    /// waits on <see cref="AttachLiveState.ActivationPumpGate"/> before
    /// dest-completion apply so it cannot overlap <c>RunTick</c>.
    internal static async Task ShutdownDestConnectPrepAsync(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        Task? prep;
        lock (live.ActivationGate)
        {
            prep = live.DestConnectAttempt?.PrepTask;
            RetireDestConnectAttemptLocked(live);
        }

        if (prep is not null)
        {
            try
            {
                await prep.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (ControlPlaneException)
            {
            }
            catch (ControlPlaneClientTimeoutException)
            {
            }
        }

        await live.ActivationPumpGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DrainDestConnectCompletions(live, tty: null).ConfigureAwait(false);
        }
        finally
        {
            live.ActivationPumpGate.Release();
        }
    }

    private static void RetireDestConnectAttemptLocked(AttachLiveState live)
    {
        var attempt = live.DestConnectAttempt;
        live.DestConnectAttempt = null;
        live.DestConnectGeneration = checked(live.DestConnectGeneration + 1);
        if (attempt is null)
            return;
        try
        {
            attempt.Cancel.Cancel();
        }
        catch
        {
            /* already disposed */
        }
    }

    // / <c>state.generation != Some(generation)</c>.
    // / applies Connected only after that check.
    /// <c>catalog_reload.rs:44-65</c> fences dest Connected after
    /// reconcile. Call under ActivationGate. A missed retire still cannot
    /// commit when the owner is unavailable or the source client is gone.
    private static bool IsLiveDestConnectCompletion(
        AttachLiveState live,
        AttachDestConnectCompletion completion)
    {
        return live.DestConnectAttempt is { } attempt
            && attempt.Generation == completion.Generation
            && live.DestConnectGeneration == completion.Generation
            && string.Equals(attempt.EndpointId, completion.Cube.Id, StringComparison.Ordinal)
            && !live.PlacementOwnerUnavailable
            && live.ControlSlot?.Client is not { IsDisposed: true }
            && !PendingCatalogFencesDestConnect(live, completion);
    }

    /// a catalog event already in the loop fences Connected.
    private static bool PendingCatalogFencesDestConnect(
        AttachLiveState live,
        AttachDestConnectCompletion completion)
    {
        if (!live.TryPeekCatalogReload(out var items, out _, out var retiredPlacementId))
            return false;
        if (retiredPlacementId is not null
            && string.Equals(live.ConnectedPlacementId, retiredPlacementId, StringComparison.Ordinal))
            return true;
        return !items.Any(item =>
            string.Equals(item.Id, completion.Cube.Id, StringComparison.Ordinal)
            && item.ConnectEnabled);
    }

    private static void ClearDestConnectAttemptIfCurrent(AttachLiveState live, ulong generation)
    {
        if (live.DestConnectAttempt is { } attempt && attempt.Generation == generation)
            live.DestConnectAttempt = null;
    }

    internal static async Task DrainDestConnectCompletions(AttachLiveState live, UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        // before Connected on the same loop.
        ApplyPendingCatalogFence(live);
        List<AttachDestConnectCompletion> batch;
        lock (live.ActivationGate)
        {
            if (live.DestConnectCompletions.Count == 0)
                return;
            batch = [.. live.DestConnectCompletions];
            live.DestConnectCompletions.Clear();
        }

        foreach (var completion in batch)
        {
            await ApplyDestConnectCompletion(live, completion, tty ?? completion.Tty)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Test helper: wait for in-flight dest prep, then drain the pump event.
    /// Product attach never awaits dest prep from the input task.
    /// </summary>
    internal static async Task FlushDestConnectPrepForTests(
        AttachLiveState live,
        UnixRawTerminal? tty = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        Task? prep;
        lock (live.ActivationGate)
            prep = live.DestConnectAttempt?.PrepTask;
        if (prep is not null)
            await prep.ConfigureAwait(false);
        await DrainDestConnectCompletions(live, tty).ConfigureAwait(false);
    }

    private static async Task ApplyDestConnectCompletion(
        AttachLiveState live,
        AttachDestConnectCompletion completion,
        UnixRawTerminal? tty)
    {
        var outcome = completion.Outcome;
        var cube = completion.Cube;
        var intent = completion.Intent;
        var control = completion.Control;
        var connectRequest = completion.Request;
        var hostGeometry = completion.HostGeometry;
        var ct = completion.SessionCt;
        var presentFailure = false;
        string? failureCopy = null;
        string? outcomeRecord = null;
        var shouldCommit = false;

        if (completion.Canceled)
        {
            outcomeRecord = ProcessLogEvents.OutcomeRejected;
        }
        else if (completion.TimeoutError is { Length: > 0 } timeoutError)
        {
            presentFailure = true;
            failureCopy = timeoutError;
            outcomeRecord = ProcessLogEvents.OutcomeError;
        }
        else if (outcome is null || !outcome.Ok || outcome.DestClient is null)
        {
            presentFailure = true;
            failureCopy = outcome?.Detail ?? outcome?.Reason;
            outcomeRecord = ProcessLogEvents.OutcomeError;
        }
        else if (!HasRequiredDestSubscribe(outcome))
        {
            presentFailure = true;
            failureCopy = AttachEndpointUserCopy.Unavailable;
            outcomeRecord = ProcessLogEvents.OutcomeError;
        }
        else if (outcome.SshConnectGeneration > 0
            && outcome.DestEndpoint is SshAttachEndpoint sshEndpoint
            && live.SshEndpoints is not null
            && !live.SshEndpoints.IsConnectActive(
                cube.Id,
                outcome.SshConnectGeneration,
                sshEndpoint))
        {
            outcomeRecord = ProcessLogEvents.OutcomeError;
        }
        else
        {
            intent = new EndpointActivationIntent
            {
                EndpointId = intent.EndpointId,
                WorkspaceId = outcome.DestWorkspaceId,
                TabId = outcome.DestTabId,
                PaneId = outcome.DestPaneId,
                StageClock = intent.StageClock ?? connectRequest.StageClock,
            };
            shouldCommit = true;
        }

        var postConnect = PostConnectActivationResult.None;
        var current = false;
        lock (live.ActivationGate)
        {
            // apply.
            // generation before Connected. Hypa re-checks under the same
            // ActivationGate as CommitActivationAfterConnect.
            current = IsLiveDestConnectCompletion(live, completion);
            if (!current)
            {
                presentFailure = false;
                outcomeRecord = null;
                shouldCommit = false;
            }
            else if (shouldCommit)
            {
                postConnect = CommitActivationAfterConnect(
                    live,
                    cube,
                    intent,
                    control,
                    tty,
                    ct,
                    connectRequest,
                    outcome!,
                    hostGeometry);
                ClearDestConnectAttemptIfCurrent(live, completion.Generation);
            }
            else
            {
                ClearDestConnectAttemptIfCurrent(live, completion.Generation);
            }
        }

        if (!current)
        {
            if (outcome is not null)
                await DisposeDestinationResourcesAsync(outcome).ConfigureAwait(false);
            return;
        }

        var keepDest = postConnect == PostConnectActivationResult.Installed
            || postConnect == PostConnectActivationResult.PartialRolledBack;
        if (!keepDest && outcome is not null)
            await DisposeDestinationResourcesAsync(outcome).ConfigureAwait(false);

        if (presentFailure)
            PresentConnectFailure(live, failureCopy, tty);

        switch (postConnect)
        {
            case PostConnectActivationResult.Installed:
                if (outcome?.DestSnapshot is { } destSnap)
                    ApplySelectedCubeSnapshot(live, cube.Id, destSnap);
                ProcessActivationCompletions(live, tty);
                _ = MarkDestinationReachableAsync(live, cube, ct);
                break;
            case PostConnectActivationResult.SourceSnapshotUnready:
                PresentConnectFailure(
                    live,
                    live.StatusError ?? "endpoint metadata is not ready for this connection",
                    tty);
                outcomeRecord = ProcessLogEvents.OutcomeError;
                break;
            case PostConnectActivationResult.PreflightFailed:
                PresentConnectFailure(
                    live,
                    AttachEndpointUserCopy.Incompatible(cube.Name),
                    tty);
                outcomeRecord = ProcessLogEvents.OutcomeError;
                break;
            case PostConnectActivationResult.Superseded:
                outcomeRecord = ProcessLogEvents.OutcomeRejected;
                break;
            case PostConnectActivationResult.PartialRolledBack:
            case PostConnectActivationResult.None when shouldCommit:
                outcomeRecord = ProcessLogEvents.OutcomeError;
                break;
        }

        if (outcomeRecord is not null)
            RecordCubesConnectOutcome(live, outcomeRecord);
    }

    internal enum PostConnectActivationResult
    {
        None,
        Installed,
        Superseded,
        PartialRolledBack,
        SourceSnapshotUnready,
        PreflightFailed,
    }

    /// <c>shell_runtime.rs:260-271</c> install then rollback on Partial.
    private static PostConnectActivationResult CommitActivationAfterConnect(
        AttachLiveState live,
        SidebarCubeItem cube,
        EndpointActivationIntent intent,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct,
        CubesConnectRequest connectRequest,
        CubesConnectRetargetOutcome outcome,
        AttachGeometry hostGeometry)
    {
        ArgumentNullException.ThrowIfNull(outcome.DestClient);
        if (live.PendingActivation is { } pending)
        {
            _ = outcome.DestClient.DisposeAsync();
            var port = CreateRegistryPort(live, pending);
            var supersede = pending.Supersede(intent, port);
            if (supersede is ActivationRollback.Unavailable unavailable)
            {
                live.PendingActivation = null;
                PresentHandoffUnavailable(live, unavailable.Message);
            }

            return PostConnectActivationResult.Superseded;
        }

        if (!TryBuildSourceLease(live, connectRequest, outcome, out var sourceLease, out var sourceLeaseError))
        {
            live.StatusError = sourceLeaseError ?? "endpoint metadata is not ready for this connection";
            return PostConnectActivationResult.SourceSnapshotUnready;
        }

        var targetLease = BuildTargetLease(live, cube, outcome);
        live.ConnectedBootId = targetLease.BootId;
        intent = live.ClientViewHints.Present(intent, targetLease.BootId);
        var beginRequest = new EndpointActivationBeginRequest
        {
            ClientId = live.EndpointClientId,
            Geometry = hostGeometry,
            Target = intent,
            Source = sourceLease,
            SourceAvailable = connectRequest.SourceSurfaceActive && sourceLease is not null,
            HostFocused = true,
            Epoch = live.NextSurfaceSerial,
            TargetLease = targetLease,
        };

        var registry = CreateRegistryPort(live, outcome.DestClient, sourceLease, targetLease);
        var begin = PendingEndpointActivation.Begin(beginRequest, registry, DateTimeOffset.UtcNow);
        if (!begin.IsOk)
        {
            return begin.Error switch
            {
                ActivationBeginError.Preflight => PostConnectActivationResult.PreflightFailed,
                ActivationBeginError.Partial partial => InstallPartialActivation(
                    live,
                    cube,
                    control,
                    tty,
                    ct,
                    outcome,
                    partial),
                _ => PostConnectActivationResult.None,
            };
        }

        StorePendingConnect(live, cube, control, tty, ct, outcome);
        InstallPendingActivation(live, begin.Value, cube.Name);
        return PostConnectActivationResult.Installed;
    }

    private static PostConnectActivationResult InstallPartialActivation(
        AttachLiveState live,
        SidebarCubeItem cube,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct,
        CubesConnectRetargetOutcome outcome,
        ActivationBeginError.Partial partial)
    {
        StorePendingConnect(live, cube, control, tty, ct, outcome);
        InstallPendingActivation(live, partial.Activation, cube.Name);
        RollbackEndpointActivation(live, partial.Message, false);
        return PostConnectActivationResult.PartialRolledBack;
    }

    private static void StorePendingConnect(
        AttachLiveState live,
        SidebarCubeItem cube,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct,
        CubesConnectRetargetOutcome outcome)
    {
        live.PendingConnectOutcome = outcome;
        live.PendingConnectCube = cube;
        live.PendingConnectControl = control;
        live.PendingConnectTty = tty;
        live.PendingConnectCt = ct;
        live.PendingTargetSessionSnapshot = outcome.DestSnapshot;
        live.PendingTargetSessionSnapshotGeneration = outcome.TargetConnectionGeneration;
        if (outcome.DestClient is { } dest)
            ArmActivationPumpForDestClient(live, dest);
    }

    /// <summary>
    /// A destination event is queued on that client. The render reader does
    /// not take it. Wake the pump so <c>DrainPendingAllEvents</c> runs.
    /// Commit clears this callback before the health monitor starts.
    /// </summary>
    internal static void ArmActivationPumpForDestClient(AttachLiveState live, ControlPlaneClient dest)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(dest);
        dest.SetEventAdmitted(live.SignalActivationPump);
    }

    internal static void ProcessActivationCompletionsForPump(AttachLiveState live, UnixRawTerminal? tty) =>
        ProcessActivationCompletions(live, tty);

    internal static void EnqueueActivationCompletion(
        AttachLiveState live,
        EndpointActivationRpcCompletion completion)
    {
        lock (live.ActivationGate)
            live.ActivationCompletions.Enqueue(completion);
        live.SignalActivationPump();
    }

    internal static void OnEndpointResponse(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        string bootId,
        string requestId,
        EndpointSurfaceControlResult result,
        UnixRawTerminal? tty)
    {
        lock (live.ActivationGate)
        {
            if (live.PendingActivation?.AcceptsResponse(endpointId, generation, bootId, requestId) != true)
                return;
            var port = CreateRegistryPort(live, live.PendingActivation);
            var progress = live.PendingActivation.ReceiveResponseForBoot(
                endpointId,
                generation,
                bootId,
                requestId,
                result,
                port);
            if (progress is SurfaceActivationProgress.Pending
                && ActivationInstalledSurfaceSatisfiesSync(live))
                progress = new SurfaceActivationProgress.Ready();
            HandleActivationProgress(live, progress, tty);
        }
    }

    internal static void OnEndpointSurface(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        AttachSurfaceEvidence surface,
        UnixRawTerminal? tty)
    {
        lock (live.ActivationGate)
        {
            if (live.PendingActivation?.AcceptsEndpoint(endpointId, generation) != true)
                return;
            var progress = live.PendingActivation.ReceiveSurface(endpointId, generation, surface);
            live.NoteActivationAdmit(
                endpointId,
                $"surface:{progress.GetType().Name}:{surface.Columns}x{surface.Rows}:p{surface.ProjectionRevision}");
            AttachSession.TryBindUnboundActivationRender(live, endpointId, generation);
            HandleActivationProgress(live, progress, tty);
        }
    }

    internal static void OnEndpointSnapshot(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        AttachSnapshotEvidence snapshot,
        UnixRawTerminal? tty)
    {
        lock (live.ActivationGate)
        {
            if (live.PendingActivation?.AcceptsEndpoint(endpointId, generation) != true)
                return;
            if (live.PendingConnectCube is { } cube
                && string.Equals(cube.Id, endpointId, StringComparison.Ordinal)
                && live.PendingTargetSessionSnapshot is { } current)
            {
                // not replace the cached topology.
                live.PendingTargetSessionSnapshot = current.ValueKind == JsonValueKind.Object
                    ? EndpointActivationProjection.MergeSnapshotIdentity(current, snapshot)
                    : EndpointActivationProjection.SnapshotIdentityElement(snapshot);
                live.PendingTargetSessionSnapshotGeneration = generation;
            }

            var progress = live.PendingActivation.ReceiveSnapshot(endpointId, generation, snapshot);
            live.NoteActivationAdmit(
                endpointId,
                $"snapshot:{progress.GetType().Name}:r{snapshot.Revision}:boot={snapshot.BootId}");
            AttachSession.TryBindUnboundActivationRender(live, endpointId, generation);
            HandleActivationProgress(live, progress, tty);
        }
    }

    internal static void OnPresentationEffectsReady(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        string token,
        UnixRawTerminal? tty)
    {
        lock (live.ActivationGate)
        {
            if (live.PendingActivation is null)
                return;
            var progress = live.PendingActivation.ReceivePresentationEffectsReady(
                endpointId,
                generation,
                token);
            HandleActivationProgress(live, progress, tty);
        }
    }


    /// connection still exists and does not accept the stored generation.
    /// The generation passed on is <c>failure.generation</c>.
    internal static void DrainQueuedEndpointFailures(AttachLiveState live, UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        while (true)
        {
            string endpointId;
            ulong generation;
            string error;
            lock (live.ActivationGate)
            {
                if (live.EndpointFailures.Count == 0)
                    return;
                (endpointId, generation, error) = live.EndpointFailures.Dequeue();
                if (ClientForEndpoint(live, endpointId) is not null
                    && !ConnectionAccepts(live, endpointId, generation))
                {
                    AttachProcessLog.Failed(
                        live.ProcessLog,
                        "endpoint_loss_superseded",
                        live.SessionName,
                        live.AttachClientId,
                        $"{endpointId} gen {generation}: {error}");
                    continue;
                }
            }

            AttachProcessLog.Failed(
                live.ProcessLog,
                "endpoint_loss_handled",
                live.SessionName,
                live.AttachClientId,
                $"{endpointId} gen {generation} live {LiveEndpointGeneration(live, endpointId)} connected {live.ConnectedPlacementId}: {error}");

            HandleEndpointDisconnect(live, endpointId, generation, error, tty);
            // A frozen presentation paints chrome only when asked. Without
            // this, the lost-connection notice never reaches the screen.
            if (tty is not null && live.ChromeEnabled)
                PaintChrome(tty, live, requestRepaint: true);
        }
    }

    /// <summary>
    /// Stores the generation captured when the write was accepted, then
    /// disconnects that one transport. Does not call
    /// <c>handle_endpoint_disconnect</c>. A stale generation, including 0 when
    /// the live generation is different, does not dispose the surviving client.
    /// A source-id failure does not call <see cref="ReleasePendingConnectDestination"/>.
    /// </summary>
    internal static void RecordEndpointTransportFailure(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        string error,
        ControlPlaneClient? failedConnection = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        lock (live.ActivationGate)
        {
            var mapped = ClientForEndpoint(live, endpointId);
            if (mapped is not null
                && failedConnection is not null
                && !ReferenceEquals(mapped, failedConnection))
                return;

            var connection = mapped ?? failedConnection;
            if (connection is null)
                return;

            if (DisposalGenerationRejected(live, endpointId, generation))
                return;

            DisposeFailedEndpointClient(live, endpointId, connection);
            live.EndpointFailures.Enqueue((endpointId, generation, error));
        }
    }

    private static bool ConnectionAccepts(AttachLiveState live, string endpointId, ulong generation)
    {
        if (ClientForEndpoint(live, endpointId) is null)
            return false;
        var liveGeneration = LiveEndpointGeneration(live, endpointId);
        return liveGeneration == generation;
    }


    /// <summary>
    /// <c>ReleasingSource</c> calls <c>start_target</c> and returns Pending.
    /// Source loss during <c>ActivatingTarget</c> returns Pending.
    /// Socket removal is <c>record_failure</c> and <c>disconnect</c>.
    /// </summary>
    internal static void HandleEndpointDisconnect(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        string notice,
        UnixRawTerminal? tty)
    {
        _ = tty;
        lock (live.ActivationGate)
        {
            if (IsStaleEndpointFailure(live, endpointId, generation))
            {
                AttachProcessLog.Failed(
                    live.ProcessLog,
                    "endpoint_loss_stale",
                    live.SessionName,
                    live.AttachClientId,
                    $"{endpointId} gen {generation} live {LiveEndpointGeneration(live, endpointId)}");
                return;
            }

            Interlocked.Increment(ref live.EndpointDisconnectCount);

            // Dest prep is that in-flight supervisor.
            if (ShouldRetireDestConnectOnDisconnect(live, endpointId))
                RetireDestConnectAttemptLocked(live);

            // This id only. The other endpoint's frames stay.
            RetireEndpointGraphics(live, endpointId);

            var activationUnavailable = false;
            if (live.PendingActivation is { } pending && pending.InvolvesEndpoint(endpointId))
            {
                // The port is the activation
                // registry. start_source_restore writes before this handler
                // returns. Pending keeps the activation. Unavailable clears it.
                var port = CreateRegistryPort(live, pending);
                var rollback = pending.EndpointDisconnected(
                    port,
                    endpointId,
                    "endpoint connection was lost while activating " + notice);
                if (rollback is ActivationRollback.Pending)
                {
                    ProcessActivationProgressUnderLock(
                        live,
                        new SurfaceActivationProgress.Pending(),
                        tty);
                }
                else if (rollback is ActivationRollback.Unavailable unavailable)
                {
                    live.PendingActivation = null;
                    PresentHandoffUnavailable(live, unavailable.Message);
                    RecordCubesConnectOutcome(live, ProcessLogEvents.OutcomeError);
                    activationUnavailable = true;
                }
            }

            var endpointWasActive = string.Equals(
                live.ConnectedPlacementId,
                endpointId,
                StringComparison.Ordinal);
            var cancelled = live.EndpointCommands.Disconnect(endpointId);
            foreach (var requestId in cancelled)
                live.EndpointCommands.CancelEndpointRequest(endpointId, requestId);

            if (endpointWasActive && !activationUnavailable)
            {
                PresentHandoffUnavailable(live, EndpointLabel(live, endpointId) + " " + notice);
            }
            else
            {
                live.HostEncoder.RequestRepaint();
            }
        }
    }

    // A reported
    /// generation that is not the live one does not apply. Generation 0 is
    /// unspecified and still runs this handler. Disposal rejects generation 0
    /// when the live generation is different.
    private static bool IsStaleEndpointFailure(AttachLiveState live, string endpointId, ulong reported)
    {
        if (reported == 0)
            return false;
        var liveGeneration = LiveEndpointGeneration(live, endpointId);
        return liveGeneration != 0 && reported != liveGeneration;
    }

    // Generation 0 must not dispose a
    /// client whose live generation is different.
    private static bool DisposalGenerationRejected(AttachLiveState live, string endpointId, ulong reported)
    {
        var liveGeneration = LiveEndpointGeneration(live, endpointId);
        if (liveGeneration == 0)
            return false;
        return reported != liveGeneration;
    }

    /// the reader. <c>transport.rs:61-65</c> carries that generation on
    /// <c>ServerMessage</c>. Pending target, then the pending outcome, then
    /// the source backup envelope, then <c>TransportEnvelope</c> for the
    /// connected placement.
    internal static ulong LiveEndpointGeneration(AttachLiveState live, string endpointId)
    {
        if (live.PendingActivation is { } pending)
        {
            if (string.Equals(pending.Source.EndpointId, endpointId, StringComparison.Ordinal))
                return pending.Source.ConnectionGeneration;
            if (string.Equals(pending.Target.EndpointId, endpointId, StringComparison.Ordinal))
                return pending.Target.ConnectionGeneration;
        }

        if (live.PendingConnectOutcome is { } outcome
            && string.Equals(live.PendingConnectCube?.Id, endpointId, StringComparison.Ordinal))
            return outcome.TargetConnectionGeneration;

        if (live.SourceBackup is { } backup
            && string.Equals(backup.ConnectedPlacementId, endpointId, StringComparison.Ordinal))
            return live.SourceTransportEnvelope.Generation;

        if (string.Equals(live.ConnectedPlacementId, endpointId, StringComparison.Ordinal))
            return live.TransportEnvelope.Generation;

        return 0;
    }

    /// Drops captured frames and unbound renders whose owner is this endpoint id.
    private static void RetireEndpointGraphics(AttachLiveState live, string endpointId) =>
        live.RetireEndpointActivationGraphicsCore(endpointId);

    private static string EndpointLabel(AttachLiveState live, string endpointId)
    {
        foreach (var cube in live.Cubes)
        {
            if (!string.Equals(cube.Id, endpointId, StringComparison.Ordinal))
                continue;
            return string.IsNullOrWhiteSpace(cube.Name) ? endpointId : cube.Name;
        }

        return endpointId;
    }

    // Retire dest prep only when that
    /// attempt's endpoint id is the one that failed.
    private static bool ShouldRetireDestConnectOnDisconnect(AttachLiveState live, string endpointId) =>
        live.DestConnectAttempt is { } attempt
        && string.Equals(attempt.EndpointId, endpointId, StringComparison.Ordinal);

    /// <summary>
    /// removed connection. The other client stays open. A source-id failure
    /// does not call <see cref="ReleasePendingConnectDestination"/>.
    /// </summary>
    private static void DisposeFailedEndpointClient(
        AttachLiveState live,
        string endpointId,
        ControlPlaneClient? failedConnection = null)
    {
        var failed = failedConnection ?? ClientForEndpoint(live, endpointId);
        if (failed is null)
            return;

        if (!failed.IsDisposed)
            _ = failed.DisposeAsync();

        if (live.SourceBackup is { } backup && ReferenceEquals(backup.SourceClient, failed))
            live.SourceBackup = backup with { SourceClient = null };

        if (ReferenceEquals(live.ControlSlot?.Client, failed))
            live.ControlSlot = null;

        if (live.PendingConnectOutcome is { } outcome && ReferenceEquals(outcome.DestClient, failed))
        {
            if (outcome.DestEndpoint is IAsyncDisposable endpoint)
                _ = endpoint.DisposeAsync();
            live.PendingConnectOutcome = null;
            live.PendingTargetSessionSnapshot = null;
            live.PendingTargetSessionSnapshotGeneration = null;
        }
    }

    /// <summary>
    /// False when the committed client for this endpoint is gone, never
    /// connected, or its transport closed. A click on that cube must dial
    /// again, not no-op.
    /// </summary>
    internal static bool CommittedEndpointIsLive(AttachLiveState live, string endpointId)
    {
        lock (live.ActivationGate)
        {
            return ClientForEndpoint(live, endpointId) is
            {
                IsDisposed: false,
                IsTransportClosed: false,
                HasOpenConnection: true,
            };
        }
    }

    private static ControlPlaneClient? ClientForEndpoint(AttachLiveState live, string endpointId)
    {
        if (live.SourceBackup is { SourceClient: { } source } backup
            && string.Equals(backup.ConnectedPlacementId, endpointId, StringComparison.Ordinal))
            return source;

        if (live.PendingConnectOutcome?.DestClient is { } pending
            && (string.Equals(live.PendingActivation?.Target.EndpointId, endpointId, StringComparison.Ordinal)
                || string.Equals(live.PendingConnectCube?.Id, endpointId, StringComparison.Ordinal)))
            return pending;

        if (live.ControlSlot?.Client is { } committed
            && string.Equals(live.ConnectedPlacementId, endpointId, StringComparison.Ordinal)
            && !ReferenceEquals(committed, live.SourceBackup?.SourceClient))
            return committed;

        if (live.ControlSlot?.Client is { } slot
            && string.Equals(live.ConnectedPlacementId, endpointId, StringComparison.Ordinal))
            return slot;

        return null;
    }

    /// retire a dest that is disabled or removed so
    /// <c>supervisor.rs:187-199</c> cannot apply Connected.
    internal static void RetireDestConnectIfCatalogStale(
        AttachLiveState live,
        IReadOnlyList<SidebarCubeItem> items)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(items);
        lock (live.ActivationGate)
        {
            if (ShouldRetireDestConnectOnCatalog(live, items))
                RetireDestConnectAttemptLocked(live);
        }
    }

    private static bool ShouldRetireDestConnectOnCatalog(
        AttachLiveState live,
        IReadOnlyList<SidebarCubeItem> items)
    {
        if (live.DestConnectAttempt is not { } attempt)
            return false;
        return !items.Any(item =>
            string.Equals(item.Id, attempt.EndpointId, StringComparison.Ordinal)
            && item.ConnectEnabled);
    }

    internal static void TickEndpointActivation(AttachLiveState live, DateTimeOffset now, UnixRawTerminal? tty)
    {
        ProcessActivationCompletions(live, tty);
        lock (live.ActivationGate)
        {
            if (live.PendingActivation?.Expired(now) == true)
            {
                DumpActivationGate(live, AttachEndpointUserCopy.ActivationTimeout);
                RollbackEndpointActivation(
                    live,
                    AttachEndpointUserCopy.ActivationTimeout,
                    false);
            }
        }
    }

    private static void DumpActivationGate(AttachLiveState live, string reason)
    {
        try
        {
            File.AppendAllText(
                "/tmp/hypa-activation-timeout.txt",
                FormatActivationGateDump(live, reason) + Environment.NewLine);
        }
        catch
        {
            // Diagnostic only. Activation continues.
        }
    }

    /// Matching capture, bind identity, and evidence identity for live diagnosis.
    internal static string FormatActivationGateDump(AttachLiveState live, string reason)
    {
        var pending = live.PendingActivation;
        var phase = pending?.Phase.GetType().Name ?? "none";
        var resize = pending is null
            ? "none"
            : $"{pending.Resize.Columns}x{pending.Resize.Rows}";
        var target = pending?.Target;
        var source = pending?.Source;
        var evidence = pending?.Phase switch
        {
            ActivationPhase.ActivatingTarget activating => activating.Evidence,
            ActivationPhase.RestoringSource restoring => restoring.Evidence,
            ActivationPhase.SynchronizingPresentation sync => sync.Evidence,
            _ => null,
        };
        var ack = pending?.Phase switch
        {
            ActivationPhase.ActivatingTarget activating => activating.AcknowledgedRevision?.ToString() ?? "none",
            ActivationPhase.RestoringSource restoring => restoring.AcknowledgedRevision?.ToString() ?? "none",
            ActivationPhase.SynchronizingPresentation sync => sync.AcknowledgedRevision?.ToString() ?? "none",
            _ => "n/a",
        };
        var surfaceEv = evidence?.Surface;
        var capture = target is not null
            && surfaceEv is not null
            && live.HasMatchingActivationCapturedFrame(target, surfaceEv);
        var bind = "none";
        if (target is not null
            && live.TryGetActivationCaptureBinding(target.EndpointId, out var binding)
            && binding is not null)
        {
            bind = $"{binding.ProjectionRevision}:{binding.SurfaceRevision}";
        }

        var ev = surfaceEv is null
            ? "none"
            : $"{surfaceEv.ProjectionRevision}:{surfaceEv.SurfaceRevision}";
        var dest = live.PendingConnectOutcome?.DestClient is not null;
        var events = live.ActivationAdmitLog.Count == 0
            ? "none"
            : string.Join("|", live.ActivationAdmitLog);
        return
            $"{DateTimeOffset.UtcNow:o} reason={reason} phase={phase} resize={resize} " +
            $"src={source?.EndpointId}:{source?.ConnectionGeneration}:{source?.BootId} " +
            $"dst={target?.EndpointId}:{target?.ConnectionGeneration}:{target?.BootId} " +
            $"ack={ack} snap={evidence?.SnapshotRevision?.ToString() ?? "none"} " +
            $"surface={surfaceEv?.ProjectionRevision.ToString() ?? "none"}:" +
            $"{surfaceEv?.Columns.ToString() ?? "-"}x{surfaceEv?.Rows.ToString() ?? "-"} " +
            $"capture={capture} bind={bind} ev={ev} " +
            $"focusAck={pending?.DumpFocusAcknowledged() ?? "n/a"} " +
            $"targetMatch={pending?.DumpTargetMatches() ?? "n/a"} " +
            $"destClient={dest} connected={live.ConnectedPlacementId ?? "none"} " +
            $"min={target?.MinimumProjectionRevision.ToString() ?? "none"} " +
            $"focus={pending?.Focus?.Kind}:{pending?.Focus?.Id ?? "none"} " +
            $"events={events}";
    }

    internal static EndpointActivationIntent? CompleteEndpointActivation(
        AttachLiveState live,
        UnixRawTerminal? tty)
    {
        EndpointActivationIntent? successor = null;
        lock (live.ActivationGate)
        {
            ApplyPendingCatalogFence(live);
            if (live.PendingActivation is null)
                return null;
            if (live.PlacementOwnerUnavailable)
            {
                AbortPendingActivationForUnavailableOwner(live);
                return null;
            }
            var port = CreateRegistryPort(live, live.PendingActivation);
            if (live.PendingActivation.PresentationSyncEndpoint() is { } syncEndpoint)
                ReplayHostTheme(live, port, syncEndpoint);
            var completion = live.PendingActivation.Complete(live, port);
            if (!completion.IsOk)
            {
                // receive_endpoint_unavailable and returns. It does not
                // rollback. Rollback after set_active tears dest down and
                // can close the attach TTY while mux stays up.
                DumpActivationGate(live, completion.Error);
                live.StatusError = completion.Error;
                live.FrozenChromePaintArmed = true;
                live.HostEncoder?.RequestRepaint();
                AttachProcessLog.CubesOutcome(
                    live.ProcessLog,
                    "connect",
                    ProcessLogEvents.OutcomeError,
                    live.SessionName,
                    live.AttachClientId);
                live.CubesConnectOutcomeRecorded = true;
                return null;
            }

            switch (completion.Value)
            {
                case ActivationCompletion.AwaitingPresentationSync syncing:
                    // when the committed endpoint is not the source.
                    if (!string.Equals(syncing.Previous, syncing.Endpoint, StringComparison.Ordinal))
                        RetireEndpointGraphics(live, syncing.Previous);
                    live.PresentationFrozen = false;
                    // pending.take on Activated / RestoredSource.
                    live.InputFrozen = true;
                    if (IsSourceRestorePresentationSync(live)
                        && !TryClaimRestoredSourceLeases(live))
                    {
                        FailClosedUnavailableSource(live);
                        PresentActivationFrame(live, tty);
                        return null;
                    }

                    AttachProcessLog.CubesOutcome(
                        live.ProcessLog,
                        "sync",
                        ProcessLogEvents.OutcomeOk,
                        live.SessionName,
                        live.AttachClientId);
                    live.CubesConnectOutcomeRecorded = true;

                    PresentActivationFrame(live, tty);
                    return null;
                case ActivationCompletion.AwaitingPresentationEffects:
                    return null;
                case ActivationCompletion.Activated:
                    live.PendingActivation = null;
                    live.PendingTargetSessionSnapshot = null;
                    live.InputFrozen = false;
                    live.PresentationFrozen = false;
                    FinalizeConnectCommit(live, tty);
                    AttachShellEndpointDispatch.DispatchSendNext(live);
                    live.SignalReaderSwitch();
                    PresentActivationFrame(live, tty);
                    return null;
                case ActivationCompletion.RestoredSource restored:
                    live.PendingActivation = null;
                    live.PendingTargetSessionSnapshot = null;
                    if (restored.Successor is null)
                        live.StatusError = AttachEndpointUserCopy.ActivationFailed;
                    else
                        successor = restored.Successor;

                    if (!TryClaimRestoredSourceLeases(live))
                    {
                        FailClosedUnavailableSource(live);
                        DisconnectPendingConnectTransport(live);
                        PresentActivationFrame(live, tty);
                        return successor;
                    }

                    live.InputFrozen = false;
                    live.PresentationFrozen = false;
                    if (successor is null)
                        AttachShellEndpointDispatch.DispatchSendNext(live);
                    FinalizeConnectRestore(live, tty, successor is null);

                    DisconnectPendingConnectTransport(live);
                    PresentActivationFrame(live, tty);
                    return successor;
                default:
                    return null;
            }
        }
    }

    internal static void RollbackEndpointActivation(
        AttachLiveState live,
        string error,
        bool sourceReleaseRejected)
    {
        if (live.PendingActivation is null)
            return;
        live.ClearActivationCapturedFrames();
        var port = CreateRegistryPort(live, live.PendingActivation);
        var rollback = live.PendingActivation.Rollback(port, error, sourceReleaseRejected);
        if (rollback is ActivationRollback.Pending)
            live.PresentationFrozen = true;
        else if (rollback is ActivationRollback.Unavailable unavailable)
        {
            live.PendingActivation = null;
            PresentHandoffUnavailable(live, unavailable.Message);
            RecordCubesConnectOutcome(live, ProcessLogEvents.OutcomeError);
        }
    }

    /// Freeze presentation and compose chrome. Do not disconnect transport.
    internal static void PresentHandoffUnavailable(AttachLiveState live, string message)
    {
        live.PresentationFrozen = true;
        // Keep input frozen when restore already cleared a gone source.
        // Chrome retry stays live when the source control slot remains.
        // Catalog owner freeze is a stronger latch than committed-unavailable.
        // Do not drop the owner-unavailable key latch.
        if (!live.PlacementOwnerUnavailable
            && live.ControlSlot?.Client is { IsDisposed: false })
            live.InputFrozen = false;
        live.InputSender?.DiscardQueued();
        live.StatusError = string.IsNullOrWhiteSpace(message)
            ? AttachEndpointUserCopy.Unavailable
            : message;
        live.FrozenChromePaintArmed = true;
        live.HostEncoder?.RequestRepaint();
    }

    /// before freeze. Hypa dest pending is not a supervisor. Dispose dest
    /// transport and drop pending so set-active cannot steal dest routing
    /// after the source owner was declared unavailable. Caller holds
    /// <see cref="AttachLiveState.ActivationGate"/>.
    internal static void AbortPendingActivationForUnavailableOwner(AttachLiveState live)
    {
        live.PendingActivation = null;
        live.PendingTargetSessionSnapshot = null;
        live.ClearActivationCapturedFramesCore();
        ReleasePendingConnectDestination(live);
    }

    internal static void DisconnectPendingConnectTransport(AttachLiveState live)
    {
        if (live.PendingConnectOutcome is not { } outcome)
            return;

        var keepActive = outcome.DestClient is { } active
            && ReferenceEquals(active, live.ControlSlot?.Client);
        if (outcome.DestClient is { } dest && !keepActive)
            _ = dest.DisposeAsync();

        if (outcome.DestEndpoint is IAsyncDisposable endpoint && !keepActive)
            _ = endpoint.DisposeAsync();

        live.PendingConnectOutcome = null;
        live.PendingTargetSessionSnapshot = null;
        live.PendingTargetSessionSnapshotGeneration = null;
    }

    /// Drop a dest that never became the active control client.
    // / Catalog owner abort uses this.
    /// does not.
    internal static void ReleasePendingConnectDestination(AttachLiveState live)
    {
        DisconnectPendingConnectTransport(live);
        live.PendingConnectCube = null;
        live.PendingConnectControl = null;
        live.PendingConnectTty = null;
        live.PendingConnectCt = default;
    }

    internal static bool ApplyEndpointActivationSetActive(
        AttachLiveState live,
        string endpointId,
        string sourceId,
        string targetId)
    {
        if (string.Equals(targetId, endpointId, StringComparison.Ordinal)
            && live.PendingConnectOutcome is { } outcome
            && live.PendingConnectCube is { } cube
            && live.PendingConnectControl is { } control)
        {
            if (live.PlacementOwnerUnavailable)
                return false;

            live.SetEndpointSurfaceActive(targetId, true);
            ApplyCubesConnectOutcome(live, control, cube, outcome, live.SourceBackup);
            if (outcome.DestClient is not null && live.InputSender is not null)
                live.InputSender.Retarget(outcome.DestClient, () =>
                {
                    if (outcome.DestPaneId is { Length: > 0 } paneId)
                        live.PaneId = paneId;
                    live.InputLease = outcome.DestInputLease ?? string.Empty;
                });
            return true;
        }

        if (string.Equals(sourceId, endpointId, StringComparison.Ordinal)
            && live.SourceBackup is { } backup)
        {
            if (backup.SourceClient is { IsDisposed: true })
            {
                FailClosedUnavailableSource(live);
                return false;
            }

            live.SetEndpointSurfaceActive(sourceId, true);
            // Do not
            // blank a restore epoch that already claimed fresh leases.
            if (!SourceRestorePresentationAlreadyBound(live, backup))
            {
                ApplySourcePresentationSnapshot(live, backup);
                RetargetInputSenderToSource(live, backup);
            }

            return true;
        }

        return false;
    }

    internal static void InstallPendingActivationForTests(
        AttachLiveState live,
        PendingEndpointActivation activation,
        string label) =>
        InstallPendingActivation(live, activation, label);

    private static void InstallPendingActivation(
        AttachLiveState live,
        PendingEndpointActivation activation,
        string label)
    {
        if (activation.SourceCommandLane() is { } sourceEndpointId)
        {
            if (string.IsNullOrEmpty(live.EndpointCommands.SnapshotBootId)
                && activation.Source.BootId.Length > 0)
                live.EndpointCommands.SnapshotBootId = activation.Source.BootId;

            foreach (var requestId in live.EndpointCommands.RetireLane(sourceEndpointId))
                live.EndpointCommands.CancelEndpointRequest(sourceEndpointId, requestId);

            if (live.ControlSlot?.Client is not null)
                live.SetEndpointSurfaceActive(sourceEndpointId, false);
        }

        live.NextSurfaceSerial = checked(live.NextSurfaceSerial + 1);
        live.PresentationFrozen = true;
        live.InputSender?.DiscardQueued();
        live.ClearActivationCapturedFramesCore();
        live.ActivationSnapshotAssembler.Reset();
        live.StatusError = AttachEndpointUserCopy.Connecting(label);
        live.FrozenChromePaintArmed = true;
        live.PendingActivation = activation;
    }

    /// <c>final_chunk</c> is true.
    internal static EndpointCommandAdmission AdmitShellEndpointResponse(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        string bootId,
        string requestId,
        bool finalChunk,
        byte[] data)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(data);
        return live.EndpointCommands.ReceiveChunk(
            endpointId,
            generation,
            bootId,
            requestId,
            finalChunk,
            data);
    }

    /// <summary>
    /// <c>ClientShellEndpointResponseChunk</c>. <c>final_chunk</c> comes from
    // A line with no flag is one
    /// final chunk. A server error stays an error
    /// <c>endpoint_cancelled</c> is only the retire result.
    /// After a final completion, <c>send_next</c> runs again
    /// </summary>
    internal static EndpointCommandAdmission? TryAdmitReadLoopShellResponse(
        AttachLiveState live,
        ControlPlaneClient client,
        JsonElement ev)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(client);
        if (ev.ValueKind != JsonValueKind.Object || ev.TryGetProperty("event", out _))
            return null;
        if (!ev.TryGetProperty("id", out var idEl))
            return null;
        var requestId = idEl.ValueKind switch
        {
            JsonValueKind.String => idEl.GetString(),
            JsonValueKind.Number => idEl.GetRawText(),
            _ => null,
        };
        if (string.IsNullOrEmpty(requestId))
            return null;
        if (!live.EndpointCommands.TryLocate(client, requestId, out var located))
            return null;

        // Absent final_chunk is one complete RPC body.
        var finalChunk = true;
        if (ev.TryGetProperty("final_chunk", out var flag) && flag.ValueKind == JsonValueKind.False)
            finalChunk = false;

        // data is this chunk of the response
        // body. A line with no data field is one complete body.
        var chunk = ShellResponseChunk(ev);
        live.EndpointCommands.NotePumpedReply();
        var admission = AdmitShellEndpointResponse(
            live,
            located.EndpointId,
            located.Generation,
            located.BootId,
            requestId,
            finalChunk,
            chunk);
        // A non-final chunk is not the result.
        if (admission is not EndpointCommandAdmission.Completed completed)
            return admission;

        // Parse the
        // accumulated body, then record that result.
        var parsed = ParseAccumulatedShellResponse(completed.Response, requestId);
        if (parsed.Error is ControlPlaneException ex)
        {
            var code = string.IsNullOrEmpty(ex.ErrorCode) ? "invalid_response" : ex.ErrorCode;
            _ = live.EndpointCommands.HandleEndpointResult(
                located.EndpointId,
                located.BootId,
                requestId,
                code,
                ex.Message);
        }
        else
        {
            _ = live.EndpointCommands.HandleEndpointResult(
                located.EndpointId,
                located.BootId,
                requestId,
                null,
                null);
        }

        live.EndpointCommands.CompleteWait(located.EndpointId, requestId, parsed);
        AttachShellEndpointDispatch.DispatchSendNext(live);
        return admission;
    }

    /// <summary>
    /// <c>data</c> and parses that body only after <c>final_chunk</c>.
    /// A server error in that body stays an error
    /// </summary>
    private static byte[] ShellResponseChunk(JsonElement ev)
    {
        if (ev.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
            return Encoding.UTF8.GetBytes(data.GetString() ?? string.Empty);
        return Encoding.UTF8.GetBytes(ev.GetRawText());
    }

    private static ControlPlaneCallResult ParseAccumulatedShellResponse(byte[] body, string requestId)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ParseShellRpcResult(doc.RootElement, requestId);
        }
        catch (JsonException ex)
        {
            return new ControlPlaneCallResult(
                default,
                requestId,
                new ControlPlaneException(1, ex.Message, null));
        }
    }

    private static ControlPlaneCallResult ParseShellRpcResult(JsonElement root, string requestId)
    {
        try
        {
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var messageEl)
                    && messageEl.ValueKind == JsonValueKind.String
                    ? messageEl.GetString() ?? "request failed"
                    : "request failed";
                var code = error.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var parsed)
                    ? parsed
                    : 1;
                string? errorCode = null;
                if (error.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("error_code", out var errorCodeEl)
                    && errorCodeEl.ValueKind == JsonValueKind.String)
                {
                    errorCode = errorCodeEl.GetString();
                }

                return new ControlPlaneCallResult(
                    default,
                    requestId,
                    new ControlPlaneException(code, message, errorCode));
            }

            var result = root.TryGetProperty("result", out var resultEl)
                ? resultEl.Clone()
                : default;
            return new ControlPlaneCallResult(result, requestId, null);
        }
        catch (JsonException ex)
        {
            return new ControlPlaneCallResult(
                default,
                requestId,
                new ControlPlaneException(1, ex.Message, null));
        }
    }

    private static void ReplayHostTheme(
        AttachLiveState live,
        IEndpointRegistryPort endpoints,
        string endpointId)
    {
        if (live.HostTheme == HostTerminalTheme.Empty)
            return;
        _ = endpoints.SendTo(
            endpointId,
            new EndpointActivationMessage.HostTheme(
                ToHostThemeSetParams(live.HostTheme),
                "client-host-theme:baseline"));
    }

    /// take_pending_graphics_cleanup, present_graphics, then one host frame.
    private static void PresentActivationFrame(AttachLiveState live, UnixRawTerminal? tty)
    {
        var cleanup = live.TakePendingGraphicsCleanup();
        PresentGraphics(live, tty, cleanup);
        PresentHostFrameAfterActivation(live, tty);
    }

    private static void PresentGraphics(AttachLiveState live, UnixRawTerminal? tty, byte[] graphics)
    {
        if (live.PresentationFrozen || graphics.Length == 0 || tty is null)
            return;
        tty.WriteBytes(graphics);
    }

    private static void PresentHostFrameAfterActivation(AttachLiveState live, UnixRawTerminal? tty)
    {
        live.HostEncoder?.RequestRepaint();
        if (tty is not null && live.ChromeEnabled)
            PaintChrome(tty, live, requestRepaint: true);
    }

    private static bool ActivationInstalledSurfaceSatisfiesSync(AttachLiveState live)
    {
        if (live.PendingActivation is not { Phase: ActivationPhase.SynchronizingPresentation sync })
            return false;
        if (sync.AcknowledgedRevision is null)
            return false;
        return live.InstalledSurfaceSatisfies(
            sync.Lease,
            sync.AcknowledgedRevision.Value,
            live.PendingActivation.Resize);
    }

    internal static void RetryActivationCompleteAfterCapture(
        AttachLiveState live,
        UnixRawTerminal? tty)
    {
        lock (live.ActivationGate)
        {
            if (live.PendingActivation is null)
                return;
            // Progress. Do not synthesize Ready.
            ProcessActivationProgressUnderLock(
                live,
                live.PendingActivation.CurrentProgress(),
                tty);
        }
    }

    private static void HandleActivationProgress(
        AttachLiveState live,
        SurfaceActivationProgress progress,
        UnixRawTerminal? tty)
    {
        lock (live.ActivationGate)
            ProcessActivationProgressUnderLock(live, progress, tty);
    }

    private static void ProcessActivationProgressUnderLock(
        AttachLiveState live,
        SurfaceActivationProgress progress,
        UnixRawTerminal? tty)
    {
        switch (progress)
        {
            case SurfaceActivationProgress.Ready:
                var successor = CompleteEndpointActivation(live, tty);
                if (successor is not null)
                {
                    var successorCt = live.PendingConnectCt;
                    var successorCube = live.Cubes.FirstOrDefault(item =>
                        string.Equals(item.Id, successor.EndpointId, StringComparison.Ordinal));
                    if (successorCube is not null
                        && !successorCt.IsCancellationRequested)
                    {
                        _ = BeginEndpointActivationAsync(
                            live,
                            successorCube,
                            successor,
                            live.PendingConnectControl!,
                            tty,
                            successorCt,
                            force: true);
                    }
                }

                break;
            case SurfaceActivationProgress.Rejected rejected:
                RollbackEndpointActivation(live, rejected.Message, rejected.SourceReleaseRejected);
                break;
        }
    }

    private static void ProcessActivationCompletions(AttachLiveState live, UnixRawTerminal? tty)
    {
        List<EndpointActivationRpcCompletion> batch;
        lock (live.ActivationGate)
        {
            if (live.ActivationCompletions.Count == 0)
                return;
            batch = new List<EndpointActivationRpcCompletion>(live.ActivationCompletions.Count);
            while (live.ActivationCompletions.Count > 0)
                batch.Add(live.ActivationCompletions.Dequeue());
        }

        foreach (var completion in batch)
        {
            switch (completion)
            {
                case EndpointActivationRpcCompletion.Control(var endpointId, var generation, var result):
                    OnEndpointResponse(
                        live,
                        endpointId,
                        generation,
                        result.BootId ?? string.Empty,
                        result.RequestId ?? string.Empty,
                        EndpointActivationRpc.FromControl(result),
                        tty);
                    break;
                case EndpointActivationRpcCompletion.Surface(var endpointId, var generation, var requestId, var result):
                    OnEndpointResponse(
                        live,
                        endpointId,
                        generation,
                        result.BootId,
                        requestId,
                        EndpointActivationRpc.FromSurface(result),
                        tty);
                    break;
                case EndpointActivationRpcCompletion.PresentationSync(var endpointId, var generation, var requestId, var sync):
                    OnEndpointResponse(
                        live,
                        endpointId,
                        generation,
                        sync.BootId,
                        requestId,
                        EndpointActivationRpc.FromSync(sync),
                        tty);
                    break;
                case EndpointActivationRpcCompletion.EffectsReady(var endpointId, var generation, var token, _):
                    OnPresentationEffectsReady(
                        live,
                        endpointId,
                        generation,
                        token,
                        tty);
                    break;
            }
        }
    }

    private static AttachEndpointRegistryPort CreateRegistryPort(
        AttachLiveState live,
        PendingEndpointActivation pending) =>
        CreateRegistryPort(live, ResolveTargetRpc(live, pending.Target), pending);

    /// <summary>
    /// The source lease id uses the source backup client. The target lease id
    /// uses the pending destination client, or the committed destination
    /// client after commit. A missing client stays null so send returns
    /// not-connected. Do not write the target message on the source client.
    /// </summary>
    private static AttachEndpointRegistryPort CreateRegistryPort(
        AttachLiveState live,
        AttachEndpointRpcClient? targetRpc,
        PendingEndpointActivation pending)
    {
        // SourceAvailable does not hide a live source connection.
        var sourceRpc = ResolveSourceRpc(live, pending.Source);
        targetRpc ??= ResolveTargetRpc(live, pending.Target);
        return new AttachEndpointRegistryPort(
            live,
            sourceRpc,
            targetRpc,
            pending.Source,
            pending.Target,
            completion => EnqueueActivationCompletion(live, completion),
            (endpointId, generation, error) =>
                RecordEndpointTransportFailure(live, endpointId, generation, error));
    }

    // The source lease id is the backup
    /// client. The control slot is not a fallback.
    private static AttachEndpointRpcClient? ResolveSourceRpc(
        AttachLiveState live,
        EndpointActivationLease source)
    {
        if (live.SourceBackup is { SourceClient: { IsDisposed: false } backup } stored
            && string.Equals(stored.ConnectedPlacementId, source.EndpointId, StringComparison.Ordinal))
            return new AttachEndpointRpcClient(backup);

        return null;
    }

    private static AttachEndpointRpcClient? ResolveTargetRpc(
        AttachLiveState live,
        EndpointActivationLease target)
    {
        if (live.PendingConnectOutcome?.DestClient is { IsDisposed: false } pending
            && (string.Equals(live.PendingConnectCube?.Id, target.EndpointId, StringComparison.Ordinal)
                || string.Equals(live.PendingActivation?.Target.EndpointId, target.EndpointId, StringComparison.Ordinal)))
            return new AttachEndpointRpcClient(pending);

        if (live.ControlSlot?.Client is { IsDisposed: false } committed
            && string.Equals(live.ConnectedPlacementId, target.EndpointId, StringComparison.Ordinal)
            && !ReferenceEquals(committed, live.SourceBackup?.SourceClient))
            return new AttachEndpointRpcClient(committed);

        return null;
    }

    private static IEndpointRegistryPort CreateRegistryPortForCommitted(AttachLiveState live)
    {
        var endpointId = live.ConnectedPlacementId ?? "local";
        EndpointActivationProjection.TryParseSnapshotIdentity(
            live.LastSnapshot,
            out var bootId,
            out var minimumRevision);
        var lease = new EndpointActivationLease
        {
            EndpointId = endpointId,
            ConnectionGeneration = live.TransportEnvelope.Generation,
            BootId = bootId,
            MinimumProjectionRevision = minimumRevision,
            ClientId = live.EndpointClientId,
            LeaseId = string.Empty,
        };
        return new AttachEndpointRegistryPort(
            live,
            live.ControlSlot?.Client is { } c ? new AttachEndpointRpcClient(c) : null,
            live.ControlSlot?.Client is { } t ? new AttachEndpointRpcClient(t) : throw new InvalidOperationException(),
            lease,
            lease,
            _ => { },
            (_, _, _) => { });
    }

    private static AttachEndpointRegistryPort CreateRegistryPort(
        AttachLiveState live,
        ControlPlaneClient destClient,
        EndpointActivationLease? sourceLease,
        EndpointActivationLease targetLease)
    {
        var sourceRpc = sourceLease is null ? null : ResolveSourceRpc(live, sourceLease);
        return new AttachEndpointRegistryPort(
            live,
            sourceRpc,
            new AttachEndpointRpcClient(destClient),
            sourceLease,
            targetLease,
            completion => EnqueueActivationCompletion(live, completion),
            (endpointId, generation, error) =>
                RecordEndpointTransportFailure(live, endpointId, generation, error));
    }

    internal static void NotifyActivationResize(
        AttachLiveState live,
        int cols,
        int rows,
        UnixRawTerminal? tty)
    {
        lock (live.ActivationGate)
        {
            if (live.PendingActivation is not { } pending)
                return;
            var geometry = new AttachGeometry
            {
                Columns = (ushort)Math.Clamp(cols, 1, ushort.MaxValue),
                Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = checked(pending.Resize.GeometryRevision + 1),
            };
            var port = CreateRegistryPort(live, pending);
            var result = pending.UpdateResize(geometry, port);
            if (!result.IsOk)
                RollbackEndpointActivation(live, result.Error, false);
            else
                ProcessActivationProgressUnderLock(live, new SurfaceActivationProgress.Pending(), tty);
        }
    }

    private static CubesConnectRequest BuildConnectRequest(
        AttachLiveState live,
        SidebarCubeItem cube,
        IAttachCommandPort control,
        AttachClientConfig config,
        string? hypaEnv,
        CubesConnectSshAttempt? sshAttempt,
        AttachGeometry hostGeometry,
        string? sourceEndpointId,
        CubesConnectStageClock? stages = null) =>
        new()
        {
            Destination = cube,
            SourceControl = control,
            SourceInputLease = live.InputLease,
            SourceResizeLease = live.ResizeLease,
            SourceAlive = live.SourceAlive,
            Config = config,
            HypaEnv = hypaEnv,
            AuthorizeConnectAsync = token => AuthorizePlacementConnectAsync(
                cube.Id,
                token,
                stages,
                live.PlacementStoreDir),
            SshAttempt = sshAttempt,
            ClientId = live.EndpointClientId,
            HostGeometry = hostGeometry,
            SourceEndpointId = sourceEndpointId,
            SourceClient = live.ControlSlot?.Client,
            SourceSurfaceActive = !live.PlacementOwnerUnavailable
                && !string.IsNullOrWhiteSpace(sourceEndpointId),
            TargetTransportEnvelope = live.TransportEnvelope,
            SourceTransportEnvelope = live.SourceTransportEnvelope,
            ExistingSourceConnectionGeneration = live.SourceTransportEnvelope.Generation,
            ExistingSourceBootId = live.SourceEndpointBootId,
            OnEndpointResolved = IsSshCube(cube)
                ? endpoint =>
                {
                    if (endpoint is SshAttachEndpoint sshEndpoint && live.SshEndpoints is not null)
                    {
                        var generation = live.SshEndpoints.RegisterConnect(cube.Id, sshEndpoint);
                        if (sshAttempt is not null)
                            sshAttempt.Generation = generation;
                    }
                }
            : null,
        };

    internal static EndpointActivationLease? BuildSourceLeaseForTests(
        AttachLiveState live,
        CubesConnectRequest request,
        CubesConnectRetargetOutcome outcome)
    {
        _ = TryBuildSourceLease(live, request, outcome, out var lease, out _);
        return lease;
    }

    internal static bool TryBuildSourceLeaseForTests(
        AttachLiveState live,
        CubesConnectRequest request,
        CubesConnectRetargetOutcome outcome,
        out EndpointActivationLease? lease,
        out string? error) =>
        TryBuildSourceLease(live, request, outcome, out lease, out error);

    internal static PostConnectActivationResult CommitActivationAfterConnectForTests(
        AttachLiveState live,
        SidebarCubeItem cube,
        EndpointActivationIntent intent,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct,
        CubesConnectRequest connectRequest,
        CubesConnectRetargetOutcome outcome,
        AttachGeometry hostGeometry) =>
        CommitActivationAfterConnect(
            live,
            cube,
            intent,
            control,
            tty,
            ct,
            connectRequest,
            outcome,
            hostGeometry);

    internal static EndpointActivationLease BuildTargetLeaseForTests(
        AttachLiveState live,
        SidebarCubeItem cube,
        CubesConnectRetargetOutcome outcome) =>
        BuildTargetLease(live, cube, outcome);

    internal static bool TryResolveRenderConnectionIdentity(
        AttachLiveState live,
        bool sourceConnection,
        out string endpointId,
        out ulong generation)
    {
        endpointId = string.Empty;
        generation = 0;
        if (live.PendingActivation is not { } pending)
            return false;
        var lease = sourceConnection ? pending.Source : pending.Target;
        endpointId = lease.EndpointId;
        generation = lease.ConnectionGeneration;
        return true;
    }

    internal static bool TryBuildEffectsFenceRequest(
        AttachLiveState live,
        EndpointActivationLease? sourceLease,
        EndpointActivationLease targetLease,
        string sourceEndpointId,
        string endpointId,
        string token,
        string clientId,
        out AttachPresentationSyncRequest request,
        out string error)
    {
        request = null!;
        error = string.Empty;
        EndpointActivationLease complete;
        if (string.Equals(sourceEndpointId, endpointId, StringComparison.Ordinal))
        {
            if (sourceLease is null || string.IsNullOrWhiteSpace(sourceLease.LeaseId))
            {
                error = "source endpoint presentation effects fence requires a source lease";
                return false;
            }

            complete = sourceLease;
        }
        else if (string.Equals(targetLease.EndpointId, endpointId, StringComparison.Ordinal))
        {
            var dest = live.PendingActivation is { } pending
                && string.Equals(pending.Target.EndpointId, endpointId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(pending.Target.LeaseId)
                    ? pending.Target
                    : targetLease;
            if (string.IsNullOrWhiteSpace(dest.LeaseId))
            {
                error = "target endpoint presentation effects fence requires a destination lease";
                return false;
            }

            complete = dest;
        }
        else
        {
            error = "endpoint presentation effects fence endpoint is unknown";
            return false;
        }

        var surface = live.CoherentPaneSurface;
        request = new AttachPresentationSyncRequest
        {
            RequestId = token,
            ClientId = clientId,
            LeaseId = complete.LeaseId,
            BootId = complete.BootId,
            ProjectionRevision = surface?.ProjectionRevision ?? 0,
            SurfaceRevision = surface?.SurfaceRevision ?? surface?.ProjectionRevision ?? 0,
        };
        return true;
    }

    internal static AttachPresentationSyncRequest BuildEffectsFenceRequest(
        AttachLiveState live,
        EndpointActivationLease? sourceLease,
        EndpointActivationLease targetLease,
        string sourceEndpointId,
        string endpointId,
        string token,
        string clientId)
    {
        if (!TryBuildEffectsFenceRequest(
                live,
                sourceLease,
                targetLease,
                sourceEndpointId,
                endpointId,
                token,
                clientId,
                out var request,
                out var error))
            throw new InvalidOperationException(error);

        return request;
    }

    /// <summary>
    /// and <c>:267-287</c> <c>send</c>/<c>send_to</c>. Dest subscribe is the
    /// dest routing key. Fail closed if that id is missing.
    /// </summary>
    internal static bool HasRequiredDestSubscribe(CubesConnectRetargetOutcome outcome) =>
        !string.IsNullOrWhiteSpace(outcome.DestSubscribeId);

    private static void RecordCubesConnectOutcome(AttachLiveState live, string outcome)
    {
        if (live.CubesConnectOutcomeRecorded)
            return;
        live.CubesConnectOutcomeRecorded = true;
        AttachProcessLog.CubesOutcome(
            live.ProcessLog,
            "connect",
            outcome,
            live.SessionName,
            live.AttachClientId);
    }

    private static bool TryBuildSourceLease(
        AttachLiveState live,
        CubesConnectRequest request,
        CubesConnectRetargetOutcome outcome,
        out EndpointActivationLease? lease,
        out string? error)
    {
        lease = null;
        error = null;
        if (!request.SourceSurfaceActive || request.SourceClient is null)
            return true;

        if (!EndpointActivationProjection.TryResolveSourceSnapshotIdentity(
                live,
                outcome.SourceConnectionGeneration,
                outcome.SourceBootId,
                out var minimumRevision))
        {
            error = "endpoint metadata is not ready for this connection";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(outcome.SourceBootId))
            live.SourceEndpointBootId = outcome.SourceBootId;

        live.SourceBackup = CaptureRetargetPresentationBackup(live, request.SourceControl!);
        lease = new EndpointActivationLease
        {
            EndpointId = request.SourceEndpointId ?? "local",
            ConnectionGeneration = outcome.SourceConnectionGeneration,
            BootId = outcome.SourceBootId ?? string.Empty,
            MinimumProjectionRevision = minimumRevision,
            ClientId = request.ClientId ?? NewAttachClientId(),
            LeaseId = request.SourceInputLease ?? live.InputLease ?? string.Empty,
        };
        return true;
    }

    private static EndpointActivationLease BuildTargetLease(
        AttachLiveState live,
        SidebarCubeItem cube,
        CubesConnectRetargetOutcome outcome) =>
        new()
        {
            EndpointId = cube.Id,
            ConnectionGeneration = outcome.TargetConnectionGeneration,
            BootId = outcome.TargetBootId ?? outcome.PreflightBootId ?? string.Empty,
            // Dest hello starts at 0. Raise the floor on dest surface-on ack.
            MinimumProjectionRevision = 0,
            ClientId = live.EndpointClientId,
            LeaseId = outcome.DestInputLease ?? string.Empty,
        };

    private static void FinalizeConnectCommit(AttachLiveState live, UnixRawTerminal? tty)
    {
        if (live.PendingConnectOutcome is { } outcome
            && live.PendingConnectCube is { } cube
            && outcome.DestClient is not null)
        {
            _ = StartEndpointHealthMonitorAsync(live, outcome.DestClient, cube.Id);
            live.PendingChromeRefresh = true;
            live.CommitChromeRefreshPending = true;
            if (tty is not null && live.ChromeEnabled)
                PaintChrome(tty, live);
        }

        live.CubesConnectAction = CubesConnectActions.Retargeted;

        AttachProcessLog.CubesOutcome(
            live.ProcessLog,
            "commit",
            ProcessLogEvents.OutcomeOk,
            live.SessionName,
            live.AttachClientId);
        live.CubesConnectOutcomeRecorded = true;
    }

    private static void FinalizeConnectRestore(AttachLiveState live, UnixRawTerminal? tty, bool terminal)
    {
        live.CubesConnectAction = CubesConnectActions.Noop;
        ActivateSourceCubeProjection(live);
        PresentHostFrameAfterActivation(live, tty);
        if (terminal)
            RecordCubesConnectOutcome(live, ProcessLogEvents.OutcomeError);
    }

    /// and <c>state.rs:1295</c> after source restore.
    private static void ActivateSourceCubeProjection(AttachLiveState live)
    {
        var sourceId = live.SourceBackup?.ConnectedPlacementId ?? live.ConnectedPlacementId;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            live.PendingVisibleObserve = true;
            return;
        }

        live.SetEndpointSurfaceActive(sourceId, true);
        SidebarCubeItem? cube = null;
        foreach (var item in live.Cubes)
        {
            if (!string.Equals(item.Id, sourceId, StringComparison.Ordinal))
                continue;
            cube = item;
            break;
        }

        AssembledSnapshot? sourceFrame = null;
        if (live.PaneId is { Length: > 0 } paneId)
            _ = live.TryGetPaneFrame(paneId, out sourceFrame);
        if (cube is not null)
            SelectCube(live, cube);
        if (sourceFrame is not null)
            live.SetPaneFrame(sourceFrame);
        if (!string.IsNullOrWhiteSpace(live.PaneId))
            live.PendingObservePaneId = live.PaneId;
        live.PendingVisibleObserve = true;
    }

    /// <summary>
    /// not dest <c>ConnectAsync</c>.
    /// </summary>
    internal static async Task RestoreLocalCubeProjectionAsync(
        AttachLiveState live,
        SidebarCubeItem cube,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(cube);
        ArgumentNullException.ThrowIfNull(control);
        control = LiveControlPort(live, control);
        RetireDestConnectAttempt(live);

        lock (live.ActivationGate)
        {
            if (live.PendingActivation is { } pending)
            {
                var intent = new EndpointActivationIntent { EndpointId = cube.Id };
                var port = CreateRegistryPort(live, pending);
                if (pending.CanRetarget(intent.EndpointId))
                {
                    var retarget = pending.Retarget(intent.FocusTarget(), port);
                    if (!retarget.IsOk)
                        RollbackEndpointActivation(live, retarget.Error, false);
                }
                else
                {
                    var supersede = pending.Supersede(intent, port);
                    if (supersede is ActivationRollback.Unavailable unavailable)
                    {
                        live.PendingActivation = null;
                        PresentHandoffUnavailable(live, unavailable.Message);
                    }
                }

                return;
            }
        }

        if (!live.PresentationFrozen
            && string.Equals(live.ConnectedPlacementId, cube.Id, StringComparison.Ordinal)
            && live.GetEndpointSurfaceActive(cube.Id))
        {
            SelectCube(live, cube);
            if (tty is not null && live.ChromeEnabled)
                PaintChrome(tty, live);
            return;
        }

        await StopDestinationHealthMonitorAsync(live).ConfigureAwait(false);
        var destToRetire = DestinationClientToRetire(live);
        ApplyPlacementCopy(live, cube);
        AssembledSnapshot? sourceFrame = null;
        var sourceUnavailable = false;
        if (live.SourceBackup is { } backup)
        {
            if (backup.SourceClient is { } source && source.IsDisposed)
            {
                live.StatusError = "the previous endpoint is no longer connected";
                live.SourceBackup = backup with { SourceClient = null };
                FailClosedUnavailableSource(live);
                sourceUnavailable = true;
            }
            else
            {
                if (backup.PaneId is { Length: > 0 } paneId)
                    _ = live.TryGetPaneFrame(paneId, out sourceFrame);
                ApplySourcePresentationSnapshot(live, backup);
                RetargetInputSenderToSource(live, backup);
                if (!await ResetSourceLeasesForRestoreAsync(live, backup, ct)
                        .ConfigureAwait(false))
                {
                    live.StatusError = "the previous endpoint is no longer connected";
                    sourceUnavailable = true;
                }
            }
        }

        RetireDestinationAfterSourceRestore(live, destToRetire);

        if (sourceUnavailable)
        {
            live.SetEndpointSurfaceActive(cube.Id, false);
            live.PresentationFrozen = true;
            live.InputFrozen = true;
            SelectCube(live, cube);
            if (tty is not null && live.ChromeEnabled)
                PaintChrome(tty, live, requestRepaint: true);
            return;
        }

        live.SetEndpointSurfaceActive(cube.Id, true);
        live.PresentationFrozen = false;
        live.InputFrozen = false;
        live.PlacementOwnerUnavailable = false;
        if (live.ControlSlot?.Client is not { IsDisposed: true })
            live.StatusError = null;
        SelectCube(live, cube);
        if (sourceFrame is not null)
            live.SetPaneFrame(sourceFrame);
        live.CubesConnectAction = CubesConnectActions.Noop;

        if (live.ControlSlot?.Client is { } client
            && !client.IsDisposed
            && !string.IsNullOrWhiteSpace(live.AttachClientId))
        {
            try
            {
                await ActivateLocalAttachSurfaceAsync(client, live, tty, ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                live.StatusError = FormatStatus(ex);
            }
            catch (ControlPlaneException ex)
            {
                live.StatusError = FormatStatus(ex);
            }
            catch (IOException ex)
            {
                live.StatusError = FormatStatus(ex.Message);
            }
        }

        var observe = live.RenderPort ?? control;
        if (!string.IsNullOrWhiteSpace(live.RenderSub)
            && live.ControlSlot?.Client is not { IsDisposed: true })
        {
            try
            {
                await ObserveVisiblePanesAsync(observe, live, ct).ConfigureAwait(false);
                await RefreshChromeAsync(observe, live, tty, ct).ConfigureAwait(false);
                live.PendingChromeRefresh = false;
            }
            catch (ControlPlaneException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
        }
        else if (!string.IsNullOrWhiteSpace(live.PaneId))
        {
            live.PendingObservePaneId = live.PaneId;
            live.PendingVisibleObserve = true;
        }

        if (tty is not null && live.ChromeEnabled)
            PaintChrome(tty, live, requestRepaint: true);
    }

    private static async Task MarkDestinationReachableAsync(
        AttachLiveState live,
        SidebarCubeItem cube,
        CancellationToken ct)
    {
        if (cube.Kind == SidebarCubeKind.Local)
            return;
        if (string.IsNullOrWhiteSpace(cube.ProviderSuffix)
            || !cube.ProviderSuffix.StartsWith("QUIC", StringComparison.Ordinal))
            return;
        if (!PlacementId.TryParse(cube.Id, out var placementId))
            return;
        if (!ProcessLocalOperatorIdentity.TryResolve(out var owner))
            return;

        try
        {
            var root = string.IsNullOrWhiteSpace(live.PlacementStoreDir)
                ? PlacementStatePaths.ResolveFromEnvironment()
                : live.PlacementStoreDir;
            var directory = new PlacementDirectoryService(new FilePlacementDirectoryStore(root));
            var updated = await directory
                .SetReachabilityAsync(owner, placementId, PlacementReachability.Reachable, ct)
                .ConfigureAwait(false);
            if (!updated.Ok)
                return;
            await ReloadCubeCatalogAsync(live, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidDataException)
        {
        }
        catch (JsonException)
        {
        }
    }

    internal static async Task StartEndpointHealthMonitorAsync(
        AttachLiveState live,
        ControlPlaneClient client,
        string endpointId)
    {
        await StopDestinationHealthMonitorAsync(live).ConfigureAwait(false);
        // The committed client is the live source. Its reader drives ticks.
        client.ClearEventAdmitted();
        var monitor = CreateEndpointHealthMonitor(live, client, endpointId);
        monitor.Start();
        live.HealthMonitor = monitor;
    }

    /// installed. This monitor starts from commit, so it is already ready.
    /// The callback records one destination failure. It does not dispose
    /// the source client and it does not clear <see cref="AttachLiveState.SourceBackup"/>.
    internal static AttachEndpointHealthMonitor CreateEndpointHealthMonitor(
        AttachLiveState live,
        ControlPlaneClient client,
        string endpointId,
        Func<AttachHealthRequest, CancellationToken, Task<AttachHealthResult>>? health = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(client);
        var watchedId = endpointId;
        var generation = live.TransportEnvelope.Generation;
        var monitor = health is null
            ? new AttachEndpointHealthMonitor(new AttachEndpointRpcClient(client), live.TransportEnvelope, time)
            : new AttachEndpointHealthMonitor(health, live.TransportEnvelope, time);
        monitor.ConnectionGenerationInvalidated += () =>
            RecordEndpointTransportFailure(
                live,
                watchedId,
                generation,
                "health timed out",
                client);
        return monitor;
    }

    private static async Task StopDestinationHealthMonitorAsync(AttachLiveState live)
    {
        if (live.HealthMonitor is null)
            return;
        try { await live.HealthMonitor.DisposeAsync().ConfigureAwait(false); }
        catch { /* monitor already stopped */ }
        live.HealthMonitor = null;
    }

    internal static void ApplyCubesConnectOutcome(
        AttachLiveState live,
        IAttachCommandPort control,
        SidebarCubeItem cube,
        CubesConnectRetargetOutcome outcome,
        AttachRetargetPresentationBackup? sourceBackup = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(cube);
        ArgumentNullException.ThrowIfNull(outcome);
        control = LiveControlPort(live, control);
        // The backup
        // names that client so later Local restore can retarget it.
        if (sourceBackup is not null && live.SourceBackup is null)
            live.SourceBackup = sourceBackup;

        if (outcome.Action != CubesConnectActions.Retargeted)
        {
            live.CubesConnectAction = outcome.Action;
            ApplyPlacementCopy(live, cube);
            live.SelectedCubeId = cube.Id;
            live.PaintedMuxLabel = outcome.PaintedMuxLabel;
            live.ActiveMuxSocketPath = outcome.DestEndpoint switch
            {
                SshAttachEndpoint ssh => ssh.SocketPath,
                UnixAttachEndpoint unix => unix.SocketPath,
                _ => live.ActiveMuxSocketPath,
            };
            return;
        }

        // send/send_to. Dest subscribe is the dest routing key. Commit
        // without it is a partial set_active. Keep the source.
        // Do not copy dest events.subscribe connection id onto logical
        // attach client id.
        if (!HasRequiredDestSubscribe(outcome))
        {
            live.StatusError = AttachEndpointUserCopy.Unavailable;
            return;
        }

        live.CubesConnectAction = outcome.Action;
        ApplyPlacementCopy(live, cube);
        live.SelectedCubeId = cube.Id;
        live.PaintedMuxLabel = outcome.PaintedMuxLabel;
        live.ActiveMuxSocketPath = outcome.DestEndpoint switch
        {
            SshAttachEndpoint ssh => ssh.SocketPath,
            UnixAttachEndpoint unix => unix.SocketPath,
            _ => live.ActiveMuxSocketPath,
        };

        var oldInput = live.InputLease;
        var oldResize = live.ResizeLease;
        if (outcome.DestWorkspaceId is not null)
            live.WorkspaceId = outcome.DestWorkspaceId;
        if (outcome.DestTabId is not null)
            live.TabId = outcome.DestTabId;
        live.ResizeLease = outcome.DestResizeLease ?? string.Empty;
        if (outcome.DestSession is not null)
            live.SessionName = outcome.DestSession;
        if (outcome.DestPaneId is { Length: > 0 } paneId)
            live.PaneId = paneId;
        live.InputLease = outcome.DestInputLease ?? string.Empty;

        // Dest commit must not release source input or resize leases.
        live.ControlSub = outcome.DestSubscribeId!;
        live.RenderSub = outcome.DestSubscribeId;

        live.Dispatcher.RebindTarget(
            live.WorkspaceId,
            live.TabId,
            live.PaneId,
            live.ResizeLease);
        if (outcome.DestClient is not null)
        {
            var destCommand = new ControlPlaneAttachCommandPort(outcome.DestClient);
            destCommand.UseShellLane(live);
            IAttachCommandPort destPort = new LoggingAttachCommandPort(destCommand, live);
            live.Dispatcher.RebindPort(destPort);
            live.RenderPort = destPort;
            live.WakeRender = outcome.DestClient.WakeRead;
            if (live.ControlSlot is { } slot)
                slot.Client = outcome.DestClient;
            else
                live.ControlSlot = new AttachControlSlot { Client = outcome.DestClient };
            if (outcome.TargetConnectionGeneration > 0)
                live.TransportEnvelope.StampServerGeneration(outcome.TargetConnectionGeneration);
            live.SetEndpointSurfaceActive(cube.Id, true);
        }

        if (live.Renew is not null)
        {
            live.Renew.Untrack(oldInput);
            live.Renew.Untrack(oldResize);
            live.Renew.Track(live.InputLease);
            live.Renew.Track(live.ResizeLease);
        }

        if (outcome.DestSnapshot is { } snapshot)
        {
            live.LastSnapshotConnectionGeneration = outcome.TargetConnectionGeneration;
            HydratePaneRightClick(live, snapshot);
            ApplyPopupFromSnapshot(live, snapshot);
            ActivateEndpointProjection(live, cube.Id, snapshot);
        }
        else
            ActivateEndpointProjection(live, cube.Id);
    }

    /// <summary>
    /// <c>select_unavailable_local</c> and
    /// <c>src/client/endpoint/registry.rs:130-133</c> Local plus frozen
    /// input. Keep the painted pane on the connected endpoint. A selected
    /// unconnected cube is pending. Do not auto-connect.
    /// </summary>
    internal static void SelectCube(AttachLiveState live, SidebarCubeItem cube)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(cube);
        live.SelectedCubeId = cube.Id;
        PersistClientViewPreferences(live);
        var connected = string.Equals(live.ConnectedPlacementId, cube.Id, StringComparison.Ordinal);
        if (connected)
        {
            ActivateEndpointProjection(live, cube.Id, SnapshotForSelectedCube(live));
            return;
        }

        RecomposeLiveSidebar(live);
        live.PendingChromeRefresh = true;
        live.HostEncoder?.RequestRepaint();
        live.InvalidateChrome();
    }

    /// <summary>
    /// <c>apply_active_snapshot</c> after dest connect returns a
    /// snapshot. Do not wait for surface <c>SetActive</c>.
    /// </summary>
    internal static void ApplySelectedCubeSnapshot(
        AttachLiveState live,
        string cubeId,
        JsonElement snapshot)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(cubeId))
            return;
        live.SelectedCubeId = cubeId;
        RememberCubeSnapshot(live, cubeId, snapshot);
        PersistClientViewPreferences(live);
        ActivateEndpointProjection(live, cubeId, snapshot);
    }

    /// <c>activate_endpoint_projection</c>.
    internal static bool TryActivateEndpointProjection(AttachLiveState live, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.GetEndpointStatus(endpointId) != ClientEndpointStatus.Online)
            return false;
        if (!live.TryCopyEndpointSnapshot(endpointId, out var snapshot))
            return false;
        ActivateEndpointProjection(live, endpointId, snapshot);
        return true;
    }

    /// <c>activate_endpoint_projection</c>.
    internal static void ActivateEndpointProjection(
        AttachLiveState live,
        string endpointId,
        JsonElement? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(endpointId))
            return;

        if (!string.Equals(live.ActiveProjectionEndpointId, endpointId, StringComparison.Ordinal))
        {
            live.DiscardLiveFrame();
            live.DropStoredPaneFrames();
            // The server keeps pane sizes per connection. Publish them again.
            lock (live.ChromeStateGate)
            {
                live.LastSentPaneSizes.Clear();
                live.DeclinedPaneResizes.Clear();
                live.PaneGeometryOwners.Clear();
                live.OwnPaneResizeSentAt.Clear();
            }
            live.ResetAppliedBlit();
            live.ChromeSeed = null;
            live.TabHits = [];
            live.ActiveProjectionEndpointId = endpointId;
        }

        if (snapshot is { } selected)
        {
            RememberCubeSnapshot(live, endpointId, selected);
            live.LastSnapshot = selected;
            RebuildSidebar(
                live,
                selected,
                requestGit: false,
                hydratePicker: false,
                snapshotOwner: endpointId);
        }
        else
            RecomposeLiveSidebar(live);

        live.PendingChromeRefresh = true;
        live.HostEncoder?.RequestRepaint();
        RecomputeLiveChromeFromSidebar(live);
        live.InvalidateChrome();
    }

    internal static bool TryResolveRestoredCubeConnect(
        AttachLiveState live,
        out SidebarCubeItem? cube)
    {
        ArgumentNullException.ThrowIfNull(live);
        cube = null;
        if (live.RestoredCubeConnectAttempted)
            return false;
        if (string.IsNullOrWhiteSpace(live.SelectedCubeId))
            return false;
        foreach (var item in live.Cubes)
        {
            if (!string.Equals(item.Id, live.SelectedCubeId, StringComparison.Ordinal))
                continue;
            if (item.Kind == SidebarCubeKind.Local)
                return false;
            if (live.GetEndpointSurfaceActive(item.Id)
                && string.Equals(live.ConnectedPlacementId, item.Id, StringComparison.Ordinal))
            {
                return false;
            }

            live.RestoredCubeConnectAttempted = true;
            cube = item;
            return true;
        }

        return false;
    }

    internal static async Task MaybeRestoreSelectedCubeConnectAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        if (live.CubesConnect is null)
            return;

        // Keep the last cube selected (prefs + workspaces). Do not
        // auto-connect. Paint the connected local mux until the operator
        // clicks Connect. Burn the one-shot only when a cube is returned.
        _ = TryResolveRestoredCubeConnect(live, out _);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Same path as a sidebar Connect click. <c>--connect-placement</c>
    /// is the agent drive for that hit.
    /// </summary>
    internal static async Task MaybeBeginRequestedCubeConnectAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        var placementId = live.RequestedConnectPlacementId;
        if (string.IsNullOrWhiteSpace(placementId))
            return;
        live.RequestedConnectPlacementId = null;

        for (var i = 0; i < 50 && !live.Cubes.Any(item =>
                 string.Equals(item.Id, placementId, StringComparison.Ordinal)); i++)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        await ApplyCubesConnectAsync(
                new MouseEngineResult(
                    MouseCommandKind.ApplyMenu,
                    PlacementId: placementId),
                live,
                control,
                tty,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Dest connect can fail before activation is installed. Keep the live
    /// source. Show the typed copy and repaint chrome. Do not freeze.
    /// not this path.
    /// </summary>
    internal static void PresentConnectFailure(
        AttachLiveState live,
        string? error,
        UnixRawTerminal? tty = null)
    {
        live.StatusError = error;
        live.CubesConnectAction = CubesConnectActions.Noop;
        live.HostEncoder.RequestRepaint();
        if (tty is not null && live.ChromeEnabled)
            PaintChrome(tty, live, requestRepaint: true);
    }

    internal static bool IsSshCube(SidebarCubeItem cube) =>
        !string.IsNullOrWhiteSpace(cube.ProviderSuffix)
        && cube.ProviderSuffix.StartsWith("SSH", StringComparison.Ordinal);

    internal static AttachGeometry BuildAttachHostGeometry(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var cols = live.Host.Cols;
        var rows = live.Host.Rows;
        if (cols < 1 || rows < 1)
        {
            cols = live.Chrome?.FocusedContent?.Cols ?? 80;
            rows = live.Chrome?.FocusedContent?.Rows ?? 24;
        }

        return new AttachGeometry
        {
            Columns = (ushort)Math.Clamp(cols, 1, ushort.MaxValue),
            Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
            CellWidthPx = 8,
            CellHeightPx = 16,
            GeometryRevision = 1,
        };
    }

    internal static AttachRetargetPresentationBackup CaptureRetargetPresentationBackup(
        AttachLiveState live,
        IAttachCommandPort control) =>
        new()
        {
            SourceClient = live.ControlSlot?.Client,
            SourcePort = control,
            ControlSub = live.ControlSub,
            RenderSub = live.RenderSub,
            AttachClientId = live.AttachClientId,
            PaneId = live.PaneId,
            InputLease = live.InputLease,
            ResizeLease = live.ResizeLease,
            WorkspaceId = live.WorkspaceId,
            TabId = live.TabId,
            SessionName = live.SessionName,
            ConnectedPlacementId = live.ConnectedPlacementId,
            PlacementKind = live.PlacementKind,
            PlacementDisplayName = live.PlacementDisplayName,
            ConnectedSshPlacement = live.ConnectedSshPlacement,
            RemoteDestination = live.RemoteDestination,
            ActiveMuxSocketPath = live.ActiveMuxSocketPath,
            PaintedMuxLabel = live.PaintedMuxLabel,
        };

    internal static void ApplySourcePresentationSnapshot(
        AttachLiveState live,
        AttachRetargetPresentationBackup backup)
    {
        if (backup.SourceClient is { IsDisposed: true })
        {
            FailClosedUnavailableSource(live);
            live.SourceBackup = backup with { SourceClient = null };
            return;
        }

        live.PaneId = backup.PaneId ?? "";
        live.InputLease = "";
        live.ResizeLease = "";
        live.WorkspaceId = backup.WorkspaceId;
        live.TabId = backup.TabId;
        live.SessionName = backup.SessionName;
        live.ConnectedPlacementId = backup.ConnectedPlacementId;
        live.PlacementKind = backup.PlacementKind;
        live.PlacementDisplayName = backup.PlacementDisplayName;
        live.ConnectedSshPlacement = backup.ConnectedSshPlacement;
        live.RemoteDestination = backup.RemoteDestination;
        live.ControlSub = backup.ControlSub ?? "";
        live.RenderSub = backup.RenderSub;
        live.AttachClientId = backup.AttachClientId;
        live.ActiveMuxSocketPath = backup.ActiveMuxSocketPath;
        live.PaintedMuxLabel = backup.PaintedMuxLabel;
        live.Dispatcher.RebindTarget(
            live.WorkspaceId,
            live.TabId,
            live.PaneId,
            live.ResizeLease);
        live.Dispatcher.RebindPort(backup.SourcePort);
        live.RenderPort = backup.SourcePort;
        if (backup.SourceClient is { } sourceClient)
        {
            live.WakeRender = sourceClient.WakeRead;
            if (live.ControlSlot is { } slot)
                slot.Client = sourceClient;
            else
                live.ControlSlot = new AttachControlSlot { Client = sourceClient };
            live.SignalReaderSwitch();
            var restoredEndpoint = live.ActiveProjectionEndpointId
                ?? live.ConnectedPlacementId
                ?? "local";
            live.ActiveProjectionEndpointId = restoredEndpoint;
            live.SetEndpointSurfaceActive(restoredEndpoint, true);
            if (backup.SourcePort is ControlPlaneAttachCommandPort restoredPort)
                restoredPort.UseShellLane(live);
        }
        else
        {
            live.WakeRender = null;
            live.ControlSlot = null;
            live.PresentationFrozen = true;
        }
    }

    internal static void RetargetInputSenderToSource(
        AttachLiveState live,
        AttachRetargetPresentationBackup backup)
    {
        if (live.InputSender is null)
        {
            if (backup.PaneId is { Length: > 0 })
                live.PaneId = backup.PaneId;
            return;
        }

        if (backup.SourceClient is { IsDisposed: false })
            live.InputSender.Retarget(backup.SourceClient, () =>
            {
                if (backup.PaneId is { Length: > 0 })
                    live.PaneId = backup.PaneId;
            });
        else
            live.InputSender.DiscardQueued();
    }

    /// <summary>
    /// stores the transport only.
    /// gates on <c>connection.surface_active</c>. Restore observes. It does
    /// not claim exclusive source pane leases.
    /// </summary>
    internal static async Task<bool> ResetSourceLeasesForRestoreAsync(
        AttachLiveState live,
        AttachRetargetPresentationBackup backup,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(backup);
        var sourcePort = backup.SourcePort ?? live.RenderPort;
        if (sourcePort is null || string.IsNullOrWhiteSpace(live.PaneId))
            return false;

        await ReleaseLeaseQuietAsync(sourcePort, backup.InputLease, ct).ConfigureAwait(false);
        await ReleaseLeaseQuietAsync(sourcePort, backup.ResizeLease, ct).ConfigureAwait(false);

        if (live.Renew is not null)
        {
            if (backup.InputLease is { Length: > 0 } oldInput)
                live.Renew.Untrack(oldInput);
            if (backup.ResizeLease is { Length: > 0 } oldResize)
                live.Renew.Untrack(oldResize);
        }

        live.InputLease = "";
        live.ResizeLease = "";
        live.Dispatcher.RebindTarget(
            live.WorkspaceId,
            live.TabId,
            live.PaneId,
            live.ResizeLease);
        return true;
    }

    // Restore
    /// observes without a source pane claim.
    internal static bool TryClaimRestoredSourceLeases(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.SourceBackup is not { } backup)
            return true;
        if (backup.SourceClient is { IsDisposed: true })
            return false;
        if (HasFreshSourceRestoreLeases(live, backup))
            return true;
        if (backup.SourcePort is null || string.IsNullOrWhiteSpace(live.PaneId))
            return false;

        var claimed = ResetSourceLeasesForRestoreAsync(
            live,
            backup,
            CancellationToken.None);
        return claimed.GetAwaiter().GetResult();
    }

    private static bool IsSourceRestorePresentationSync(AttachLiveState live) =>
        live.PendingActivation?.Phase is ActivationPhase.SynchronizingPresentation
        {
            Completion: ActivationCompletion.RestoredSource
        };

    private static bool SourceRestorePresentationAlreadyBound(
        AttachLiveState live,
        AttachRetargetPresentationBackup backup) =>
        HasFreshSourceRestoreLeases(live, backup);

    private static bool HasFreshSourceRestoreLeases(
        AttachLiveState live,
        AttachRetargetPresentationBackup backup)
    {
        if (!string.Equals(
                live.ConnectedPlacementId,
                backup.ConnectedPlacementId,
                StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(live.InputLease)
            || string.IsNullOrWhiteSpace(live.ResizeLease))
            return false;
        if (backup.InputLease is { Length: > 0 }
            && string.Equals(live.InputLease, backup.InputLease, StringComparison.Ordinal))
            return false;
        if (backup.ResizeLease is { Length: > 0 }
            && string.Equals(live.ResizeLease, backup.ResizeLease, StringComparison.Ordinal))
            return false;
        return true;
    }

    private static void FailClosedUnavailableSource(AttachLiveState live)
    {
        ClearDestRoutingForUnavailableSource(live);
        live.InputSender?.DiscardQueued();
        live.StatusError = "the previous endpoint is no longer connected";
    }

    private static ControlPlaneClient? DestinationClientToRetire(AttachLiveState live)
    {
        if (live.ControlSlot?.Client is { } held
            && !ReferenceEquals(held, live.SourceBackup?.SourceClient))
            return held;
        return live.PendingConnectOutcome?.DestClient;
    }

    private static void RetireDestinationAfterSourceRestore(
        AttachLiveState live,
        ControlPlaneClient? dest)
    {
        DisconnectPendingConnectTransport(live);
        if (dest is null || dest.IsDisposed)
            return;
        if (ReferenceEquals(dest, live.ControlSlot?.Client)
            || ReferenceEquals(dest, live.SourceBackup?.SourceClient))
            return;
        _ = dest.DisposeAsync();
    }

    private static void ClearDestRoutingForUnavailableSource(AttachLiveState live)
    {
        live.ControlSlot = null;
        live.WakeRender = null;
        live.RenderPort = null;
        live.ControlSub = "";
        live.RenderSub = null;
        live.InputLease = "";
        live.ResizeLease = "";
        live.InputFrozen = true;
        live.PresentationFrozen = true;
    }

    /// Mux <c>OnClientDisconnected</c> drops dest leases when the socket
    /// closes. Do not await <c>lease.release</c> here: the default control
    /// plane call timeout is 30s and must not stall the attach pump.
    private static async Task DisposeDestinationResourcesAsync(CubesConnectRetargetOutcome outcome)
    {
        if (outcome.DestClient is { } destClient)
        {
            try { await destClient.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        if (outcome.DestEndpoint is IAsyncDisposable endpoint)
        {
            try { await endpoint.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }

    internal static ValueTask<bool> AuthorizePlacementConnectAsync(
        string placementId,
        CancellationToken cancellationToken,
        CubesConnectStageClock? stages = null,
        string? placementStoreDir = null,
        Func<IPlacementDirectory>? openDirectory = null)
    {
        if (stages is not null && stages.DirectoryUnchanged(placementId))
            return ValueTask.FromResult(true);

        if (stages is not null
            && stages.TryRememberedDirectory(placementId, out var directory, out var original)
            && directory is not null
            && original is not null)
        {
            return RevalidateRememberedPlacementAsync(
                directory,
                original,
                placementId,
                stages,
                cancellationToken);
        }

        return TryAuthorizePlacementConnectAsync(
            placementId,
            requireEnabledSsh: false,
            cancellationToken,
            placementStoreDir,
            openDirectory);
    }

    private static async ValueTask<bool> RevalidateRememberedPlacementAsync(
        IPlacementDirectory directory,
        PlacementRecord original,
        string placementId,
        CubesConnectStageClock stages,
        CancellationToken cancellationToken)
    {
        if (!ProcessLocalOperatorIdentity.TryResolve(out var requester))
            return false;
        if (!PlacementId.TryParse(placementId, out var parsed))
            return false;

        return await ConnectPlacementRecheck.StillCurrentAsync(
                new ConnectPlacementHold(directory, requester, parsed, original, Stamp: null),
                stages,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<bool> TryAuthorizePlacementConnectAsync(
        string placementId,
        bool requireEnabledSsh,
        CancellationToken cancellationToken,
        string? placementStoreDir = null,
        Func<IPlacementDirectory>? openDirectory = null)
    {
        if (!ProcessLocalOperatorIdentity.TryResolve(out var requester))
            return false;
        if (!PlacementId.TryParse(placementId, out var parsed))
            return false;

        try
        {
            IPlacementDirectory directory;
            if (openDirectory is not null)
            {
                directory = openDirectory();
            }
            else
            {
                var root = string.IsNullOrWhiteSpace(placementStoreDir)
                    ? PlacementStatePaths.ResolveFromEnvironment()
                    : placementStoreDir;
                directory = new PlacementDirectoryService(new FilePlacementDirectoryStore(root));
            }
            var found = await directory
                .GetForConnectAsync(requester, parsed, cancellationToken)
                .ConfigureAwait(false);
            if (!found.Ok || found.Value is null)
                return false;
            return !requireEnabledSsh || found.Value is { Ssh: { Enabled: true } };
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

internal static class EndpointActivationRpc
{
    internal static EndpointSurfaceControlResult FromControl(AttachControlResult result) =>
        new()
        {
            Ok = result.Error is null,
            ProjectionRevision = result.ProjectionRevision,
            BootId = result.BootId,
            RequestId = result.RequestId,
            HasErrorCode = result.Error is not null,
            ErrorMessage = result.Error?.Message,
        };

    internal static EndpointSurfaceControlResult FromSurface(AttachSurfaceInterestResult result) =>
        new()
        {
            Ok = true,
            ProjectionRevision = result.MinimumProjectionRevision,
            LeaseId = result.LeaseId,
            BootId = result.BootId,
            RequestId = result.RequestId,
        };

    internal static EndpointSurfaceControlResult FromSync(AttachPresentationSync sync) =>
        new()
        {
            Ok = true,
            ProjectionRevision = sync.ProjectionRevision,
            LeaseId = sync.LeaseId,
            BootId = sync.BootId,
            RequestId = sync.RequestId,
        };
}
