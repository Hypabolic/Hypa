using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class EndpointActivationTests
{
    [Fact]
    public void SourceOffRequestIsDistinctAndPrecedesTargetOnPhase()
    {
        var activation = AtPhase(new ActivationPhase.ReleasingSource("client-shell-surface:9:off"));
        Assert.True(activation.AcceptsResponse("local", 1, "local-boot", "client-shell-surface:9:off"));
        Assert.False(activation.AcceptsResponse("remote", 7, "remote-boot", "client-shell-surface:9:on"));
    }

    [Fact]
    public void ObservedBeginWriteFailureReturnsPartial()
    {
        var registry = new FakeRegistry { FailAfterWrite = true };
        var request = SampleRequest(sourceAvailable: true);
        var result = PendingEndpointActivation.Begin(request, registry, DateTimeOffset.UtcNow);
        Assert.False(result.IsOk);
        Assert.IsType<ActivationBeginError.Partial>(result.Error);
        var partial = (ActivationBeginError.Partial)result.Error;
        Assert.Contains("focus revoke", partial.Message);
        Assert.Single(registry.Sent);
        Assert.IsType<EndpointActivationMessage.FocusRevoke>(registry.Sent.First().Message);
        Assert.Empty(registry.TargetSent);
        Assert.IsType<ActivationRollback.Unavailable>(
            partial.Activation.Rollback(registry, partial.Message, false));
    }

    [Fact]
    public void SourceReleaseIsSentAndAcknowledgedBeforeTargetActivation()
    {
        var registry = new FakeRegistry();
        var request = SampleRequest(sourceAvailable: true, epoch: 11);
        var activation = PendingEndpointActivation.Begin(request, registry, DateTimeOffset.UtcNow).Value;
        Assert.Equal(2, registry.Sent.Count);
        Assert.IsType<EndpointActivationMessage.FocusRevoke>(registry.Sent[0].Message);
        Assert.False(registry.SurfaceState["local"]);
        Assert.Empty(registry.TargetSent);

        var progress = activation.ReceiveResponseForBoot(
            "local",
            1,
            "local-boot",
            "client-shell-surface:11:off",
            SurfaceOff("client-shell-surface:11:off"),
            registry);
        Assert.IsType<SurfaceActivationProgress.Pending>(progress);
        Assert.Contains(registry.TargetSent, s => s.Message is EndpointActivationMessage.Resize);
        Assert.Contains(registry.TargetSent, s => s.Message is EndpointActivationMessage.SurfaceInterest);
    }

    [Fact]
    public void ActivationRequiresExactSnapshotSurfaceRevisionPair()
    {
        var activation = AtPhase(new ActivationPhase.ActivatingTarget(
            "on",
            1,
            null,
            null,
            true,
            new EndpointActivationEvidence()));
        Assert.IsType<SurfaceActivationProgress.Pending>(
            activation.ReceiveSnapshot("remote", 7, Snapshot("remote-boot", 2)));
        Assert.IsType<SurfaceActivationProgress.Pending>(
            activation.ReceiveSurface("remote", 7, Surface("remote-boot", 1)));
        Assert.IsType<SurfaceActivationProgress.Ready>(
            activation.ReceiveSurface("remote", 7, Surface("remote-boot", 2)));
    }

    [Fact]
    public void ProjectionSnapshotEventRecordsCoherentPairFromSurfaceOn()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PendingActivation = AtPhase(new ActivationPhase.ActivatingTarget(
            "on",
            5,
            null,
            null,
            true,
            new EndpointActivationEvidence()));
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "event":"{{AttachEndpointProtocol.ProjectionSnapshotEvent}}",
              "params":{
                "boot_id":"remote-boot",
                "revision":2,
                "projection_revision":2,
                "surface_revision":2,
                "columns":80,
                "rows":24,
                "focused":true,
                "focused_pane_id":"pane-a",
                "pane_id":"pane-a"
              }
            }
            """);
        AttachEndpointActivationPump.AdmitAttachEvent(
            live,
            "remote",
            7,
            doc.RootElement,
            tty: null);
        Assert.True(live.PendingActivation.Phase is ActivationPhase.ActivatingTarget target
            && target.Evidence.HasCoherentSnapshotSurfacePair()
            && target.Evidence.Surface is { ProjectionRevision: 2, Focused: true, FocusedPaneId: "pane-a" });
    }

    [Fact]
    public void RollbackFromActivatingTargetSendsTargetOffFirst()
    {
        var registry = new FakeRegistry();
        var activation = AtPhase(new ActivationPhase.ActivatingTarget(
            "client-shell-surface:3:on",
            1,
            null,
            null,
            true,
            new EndpointActivationEvidence()));
        var rollback = activation.Rollback(registry, "superseded", false);
        Assert.IsType<ActivationRollback.Pending>(rollback);
        Assert.Contains(
            registry.TargetSent,
            s => s.Message is EndpointActivationMessage.SurfaceInterest(var id, false)
                && id.Contains("rollback-target-off"));
    }

    [Fact]
    public void SupersedeDuringSourceRestoreReturnsPending()
    {
        var registry = new FakeRegistry();
        var activation = AtPhase(new ActivationPhase.RestoringSource(
            "rollback-source-on",
            null,
            new EndpointActivationEvidence()));
        var rollback = activation.Supersede(new EndpointActivationIntent { EndpointId = "remote" }, registry);
        Assert.IsType<ActivationRollback.Pending>(rollback);
        Assert.Equal("remote", activation.Successor?.EndpointId);
    }

    [Fact]
    public void StaleResponseBootIsNotConsumed()
    {
        var activation = AtPhase(new ActivationPhase.ReleasingSource("client-shell-surface:9:off"));
        var progress = activation.ReceiveResponseForBoot(
            "local",
            1,
            "stale-boot",
            "client-shell-surface:9:off",
            SurfaceOff("client-shell-surface:9:off"),
            new FakeRegistry());
        Assert.IsType<SurfaceActivationProgress.Stale>(progress);
    }

    [Fact]
    public void DisconnectedSourcePermitsDirectTargetActivation()
    {
        var registry = new FakeRegistry();
        var request = SampleRequest(sourceAvailable: false);
        var result = PendingEndpointActivation.Begin(request, registry, DateTimeOffset.UtcNow);
        Assert.True(result.IsOk);
        Assert.Contains(registry.TargetSent, s => s.Message is EndpointActivationMessage.SurfaceInterest);
    }

    [Fact]
    public void SourceCommandLaneRetiresOnlyWhenSourceDiffersFromTarget()
    {
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ReleasingSource("off"),
            SourceLease(),
            TargetLease(),
            sourceAvailable: true);
        Assert.Equal("local", activation.SourceCommandLane());
        var sameTarget = PendingEndpointActivation.ForTests(
            new ActivationPhase.ReleasingSource("off"),
            SourceLease(),
            SourceLease(),
            sourceAvailable: true);
        Assert.Null(sameTarget.SourceCommandLane());
    }

    [Fact]
    public void PresentationFrozenDropsPresentationEffectEvents()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PresentationFrozen = true;
        Assert.True(AttachEndpointActivationPump.ShouldDropPresentationEffect(
            live,
            ProtocolEventTypes.ClientWindowTitleChanged));
        Assert.False(AttachEndpointActivationPump.ShouldDropPresentationEffect(
            live,
            ProtocolEventTypes.TerminalRender));
    }

    [Fact]
    public void FrozenPopupAndChromeEventsAreDroppedWhilePending()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease());
        Assert.True(AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(
            live,
            ProtocolEventTypes.PopupLifecycle));
        Assert.True(AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(
            live,
            ProtocolEventTypes.LayoutUpdated));
        Assert.True(AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(
            live,
            ProtocolEventTypes.PaneLifecycle));
        Assert.True(AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(
            live,
            ProtocolEventTypes.OccupantLifecycle));
        Assert.True(AttachEndpointActivationPump.ShouldDropFrozenPresentationEvent(
            live,
            ProtocolEventTypes.PaneAgentStatusChanged));
    }

    [Fact]
    public void InstallCoherentSurfaceUsesCapturedCellsNotBlankGrid()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PaneId = "pane-a";
        var surface = Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" };
        var row = new List<AssembledCell>(80);
        row.Add(new AssembledCell("x", 1, false, AssembledStyle.Default));
        for (var i = 1; i < 80; i++)
            row.Add(AssembledCell.Blank);
        var rows = Enumerable.Repeat<IReadOnlyList<AssembledCell>>(row, 24).ToList();
        var captured = new AssembledSnapshot(
            "pane-a",
            80,
            24,
            "hypa",
            "main",
            rows,
            default,
            AssembledCursor.Default);
        StoreBoundCapture(live, TargetLease(), surface, captured);
        Assert.True(live.TryInstallCoherentSurface(surface, TargetLease(), out _));
        Assert.NotNull(live.CoherentPaneSurface);
        Assert.True(live.TryGetPaneFrame("pane-a", out var frame));
        Assert.NotNull(frame);
        Assert.Equal("x", frame!.Cells[0][0].Text);
    }

    [Fact]
    public void DestObserveDoesNotRequireDestLeases()
    {
        var outcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            DestInputLease = null,
            DestResizeLease = "resize-1",
        };
        Assert.False(AttachSession.HasRequiredDestSubscribe(outcome));
    }

    [Fact]
    public void RenderConnectionIdentityFollowsTransportNotPhase()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease());
        live.PendingActivation = activation;
        Assert.True(AttachSession.TryResolveRenderConnectionIdentity(
            live,
            sourceConnection: true,
            out var sourceEndpoint,
            out var sourceGeneration));
        Assert.Equal("local", sourceEndpoint);
        Assert.Equal(1UL, sourceGeneration);
        Assert.True(AttachSession.TryResolveRenderConnectionIdentity(
            live,
            sourceConnection: false,
            out var targetEndpoint,
            out var targetGeneration));
        Assert.Equal("remote", targetEndpoint);
        Assert.Equal(7UL, targetGeneration);
        Assert.True(AttachEndpointActivationPump.TryAdmitTerminalRender(
            live,
            "remote",
            7,
            Surface("remote-boot", 2),
            tty: null));
    }

    [Fact]
    public void SourceRestoreEffectsFenceUsesSourceLeaseId()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var localSurface = Surface("local-boot", 2);
        StoreBoundCapture(live, SourceLease(), localSurface, SampleCapturedFrame("pane-a"));
        Assert.True(live.TryInstallCoherentSurface(localSurface, SourceLease(), out _));
        var source = SourceLease() with { LeaseId = "src-input-lease" };
        var target = TargetLease() with { LeaseId = "dest-input-lease" };
        var restoreFence = AttachSession.BuildEffectsFenceRequest(
            live,
            source,
            target,
            "local",
            "local",
            "restore-token",
            "attach-client");
        Assert.Equal("src-input-lease", restoreFence.LeaseId);
        Assert.Equal("local-boot", restoreFence.BootId);
        var activateFence = AttachSession.BuildEffectsFenceRequest(
            live,
            source,
            target,
            "local",
            "remote",
            "activate-token",
            "attach-client");
        Assert.Equal("dest-input-lease", activateFence.LeaseId);
        Assert.Equal("remote-boot", activateFence.BootId);
    }

    [Fact]
    public void EmptySourceLeaseFailsClosedBeforeFenceSend()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var source = SourceLease() with { LeaseId = string.Empty };
        var target = TargetLease() with { LeaseId = "dest-input-lease" };
        Assert.False(AttachSession.TryBuildEffectsFenceRequest(
            live,
            source,
            target,
            "local",
            "local",
            "restore-token",
            "attach-client",
            out _,
            out var error));
        Assert.Contains("source lease", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceFenceNeverUsesDestLeaseWhenSourceLeaseMissing()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var target = TargetLease() with { LeaseId = "dest-input-lease" };
        Assert.False(AttachSession.TryBuildEffectsFenceRequest(
            live,
            sourceLease: null,
            target,
            "local",
            "local",
            "restore-token",
            "attach-client",
            out _,
            out var error));
        Assert.Contains("source lease", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingCaptureDoesNotReuseStalePaneFrame()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PaneId = "pane-a";
        live.SetPaneFrame(MouseTestGeom.Frame("stale", "pane-a", 80));
        var surface = Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" };
        Assert.False(live.TryInstallCoherentSurface(surface, TargetLease(), out _));
        Assert.True(live.TryGetPaneFrame("pane-a", out var frame));
        Assert.Equal("s", frame!.Cells[0][0].Text);
    }

    [Fact]
    public void StaleCaptureFromFailedActivationCannotInstallOnLaterSuccess()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PendingActivation = AtPhase(new ActivationPhase.ActivatingTarget(
            "on",
            2,
            null,
            null,
            true,
            new EndpointActivationEvidence()));
        var lease = TargetLease();
        var staleSurface = Surface("remote-boot", 1);
        var goodSurface = Surface("remote-boot", 2);
        StoreBoundCapture(live, lease, staleSurface, SampleCapturedFrame("pane-a"));
        Assert.False(live.TryInstallCoherentSurface(goodSurface, lease, out _));
        live.ClearActivationCapturedFrames();
        Assert.False(live.HasActivationCapturedFrame("remote"));
        StoreBoundCapture(live, lease, goodSurface, SampleCapturedFrame("pane-a"));
        Assert.True(live.TryInstallCoherentSurface(goodSurface, lease, out _));
    }

    [Fact]
    public void PendingInstallKeepsSourcePaneFramesBeforeDestCapture()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.SetPaneFrame(MouseTestGeom.Frame("source", "pane-a", 80));
        live.ObservedPaneIds.Add("pane-a");
        AttachSession.InstallPendingActivationForTests(
            live,
            AtPhase(new ActivationPhase.ReleasingSource("off")),
            "remote");
        Assert.True(live.TryGetPaneFrame("pane-a", out _));
        Assert.Contains("pane-a", live.ObservedPaneIds);
        Assert.True(live.PresentationFrozen);
        Assert.True(live.FrozenChromePaintArmed);
    }

    [Fact]
    public void UnavailablePresentationKeepsSourcePaneFrames()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.SetPaneFrame(MouseTestGeom.Frame("source", "pane-a", 80));
        live.ObservedPaneIds.Add("pane-a");
        AttachSession.PresentHandoffUnavailable(live, "Connect timed out. Restoring the previous Placement.");
        Assert.True(live.TryGetPaneFrame("pane-a", out _));
        Assert.Contains("pane-a", live.ObservedPaneIds);
        Assert.True(live.PresentationFrozen);
        Assert.True(live.FrozenChromePaintArmed);
        Assert.False(live.InputFrozen);
    }

    [Fact]
    public void RestoredSourceSelectsSourceCubeAndUnfreezes()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "plc_local",
                Name = "Local",
                Kind = SidebarCubeKind.Local,
                Reachability = SidebarCubeReachability.Local,
            },
            new SidebarCubeItem
            {
                Id = "plc_docker",
                Name = "Docker",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        live.SelectedCubeId = "plc_docker";
        live.ConnectedPlacementId = "plc_docker";
        live.PaneId = "pane-a";
        live.SetPaneFrame(MouseTestGeom.Frame("source", "pane-a", 80));
        AttachSession.RememberCubeSnapshot(live, "plc_local", LocalWorkspaceSnapshot());
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourcePort = MouseTestGeom.ApplyPort(),
            ConnectedPlacementId = "plc_local",
            PlacementKind = SidebarCubeKind.Local,
            PlacementDisplayName = "Local",
            PaneId = "pane-a",
            WorkspaceId = "ws-local",
            TabId = "t1",
        };
        var destClient = new ControlPlaneClient("/tmp/hypa-restore-source-cube.sock");
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        live.PendingConnectOutcome = MinimalConnectOutcome(destClient);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                SourceLease(),
                "9:1:local-boot",
                true,
                new ActivationCompletion.RestoredSource("endpoint handoff was rolled back", null)),
            SourceLease(),
            TargetLease());
        live.PresentationFrozen = true;
        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(successor);
        Assert.False(live.PresentationFrozen);
        Assert.Equal("plc_local", live.SelectedCubeId);
        Assert.True(live.TryGetPaneFrame("pane-a", out _));
        Assert.True(live.PendingVisibleObserve);
    }

    [Fact]
    public async Task LocalRestoreObservesWhenSourceLeaseClaimIsDenied()
    {
        var port = MouseTestGeom.ApplyPort();
        var inner = port.Handler!;
        port.Handler = (method, parameters) =>
        {
            if (method == ProtocolMethods.RuntimeLeaseClaim)
                return MouseTestGeom.Parse("""{"outcome":"denied"}""");
            return inner(method, parameters);
        };
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.PresentationFrozen = true;
        live.InputFrozen = true;
        live.ConnectedPlacementId = "plc_docker";
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "plc_local",
                Name = "Local",
                Kind = SidebarCubeKind.Local,
                Reachability = SidebarCubeReachability.Local,
            },
            new SidebarCubeItem
            {
                Id = "plc_docker",
                Name = "Docker",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourcePort = port,
            ConnectedPlacementId = "plc_local",
            PlacementKind = SidebarCubeKind.Local,
            PlacementDisplayName = "Local",
            PaneId = "p1",
            WorkspaceId = "w1",
            TabId = "t1",
            InputLease = "lease-in",
            ResizeLease = "lease-r",
        };
        var cube = live.Cubes[0];
        await AttachSession.RestoreLocalCubeProjectionAsync(
            live, cube, port, tty: null, CancellationToken.None);
        Assert.False(live.InputFrozen);
        Assert.Null(live.StatusError);
        Assert.Equal("", live.InputLease);
        Assert.Equal("", live.ResizeLease);
        Assert.DoesNotContain(port.Calls, call => call.Method == ProtocolMethods.RuntimeLeaseClaim);
    }

    [Fact]
    public async Task RefreshChromeOnSplitDoesNotClaimSiblingResizeLease()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        await AttachSession.RefreshChromeAsync(port, live, tty: null, CancellationToken.None);
        Assert.Null(live.StatusError);
        Assert.DoesNotContain(port.Calls, call => call.Method == ProtocolMethods.RuntimeLeaseClaim);
        Assert.Contains(
            port.Calls,
            call => call.Method == ProtocolMethods.PaneResize
                && call.Params?["pane_id"]?.GetValue<string>() == "p2"
                && call.Params?["lease_id"] is null);
    }

    [Fact]
    public void MatchingEvidenceCommitsWithoutCapturedCells()
    {
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2));
        evidence.RecordSurface(Surface("remote-boot", 2));
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease());
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        SeedTargetLiveConnect(live, "remote-boot", 2);
        var registry = new FakeRegistry();
        var result = activation.Complete(live, registry);
        Assert.True(result.IsOk);
        var sync = Assert.IsType<ActivationCompletion.AwaitingPresentationSync>(result.Value);
        Assert.Equal("local", sync.Previous);
        Assert.Equal("remote", sync.Endpoint);
        Assert.Contains("remote", registry.ActiveSets);
        Assert.True(registry.SurfaceActive("remote"));
        Assert.Equal(ClientEndpointStatus.Online, live.GetEndpointStatus("remote"));
        Assert.Equal("remote", live.ActiveProjectionEndpointId);
        Assert.False(live.HasActivationCapturedFrame("remote"));
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(activation.Phase);
    }

    [Fact]
    public void CaptureBeforeSurfaceEvidenceDoesNotBind()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease());
        using var doc = JsonDocument.Parse(
            """
            {
              "pane_id":"pane-a",
              "kind":"cells",
              "full":true,
              "reanchor":true,
              "grid_cols":80,
              "grid_rows":1,
              "generation":2,
              "rows":[{"i":0,"t":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}]
            }
            """);
        Assert.False(AttachSession.TryCaptureActivationTerminalRender(
            live,
            "remote",
            7,
            doc.RootElement,
            assembler: null));
        Assert.False(live.HasActivationCapturedFrame("remote"));
    }

    [Fact]
    public void ObserveRenderBindsAfterMatchingSurface()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease());
        using var doc = JsonDocument.Parse(
            """
            {
              "pane_id":"pane-a",
              "kind":"cells",
              "full":true,
              "reanchor":true,
              "grid_cols":80,
              "grid_rows":1,
              "generation":2,
              "rows":[{"i":0,"t":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}]
            }
            """);
        Assert.False(AttachSession.TryCaptureActivationTerminalRender(
            live,
            "remote",
            7,
            doc.RootElement,
            assembler: null));
        Assert.False(live.HasActivationCapturedFrame("remote"));

        AttachSession.OnEndpointSnapshot(
            live,
            "remote",
            7,
            Snapshot("remote-boot", 4) with { PaneId = "pane-a" },
            tty: null);
        Assert.False(live.HasActivationCapturedFrame("remote"));

        AttachSession.OnEndpointSurface(
            live,
            "remote",
            7,
            Surface("remote-boot", 4),
            tty: null);
        Assert.True(live.HasActivationCapturedFrame("remote"));
        Assert.True(live.HasMatchingActivationCapturedFrame(
            TargetLease(),
            Surface("remote-boot", 4)));
    }

    [Fact]
    public void ReadyEvidenceCommitsTheDestinationEndpoint()
    {
        using var peer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-ready-evidence-source.sock");
        var dest = peer.ConnectClient();
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2) with { PaneId = "pane-a" });
        evidence.RecordSurface(Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" });
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
            DestInputLease = "dest-input",
            DestResizeLease = "dest-resize",
        };
        StoreBoundCapture(
            live,
            SourceLease(),
            Surface("local-boot", 1),
            SampleCapturedFrame("pane-a"));
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease() with { LeaseId = "dest-input" });
        AttachSession.RetryActivationCompleteAfterCapture(live, tty: null);
        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation.Phase);
        Assert.False(live.PresentationFrozen);
        Assert.True(live.InputFrozen);
        Assert.True(live.GetEndpointSurfaceActive("remote"));
        Assert.Equal(ClientEndpointStatus.Online, live.GetEndpointStatus("remote"));
        Assert.Same(dest, live.ControlSlot?.Client);
        Assert.False(source.IsDisposed);
        Assert.False(dest.IsDisposed);
        Assert.False(live.HasActivationCapturedFrame("local"));
    }

    [Fact]
    public void ProjectionSnapshotKeepsDestinationWorkspaces()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PendingConnectCube = new SidebarCubeItem
        {
            Id = "remote",
            Name = "remote",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        live.PendingTargetSessionSnapshot = DestinationWorkspaceSnapshot();
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease());

        AttachSession.OnEndpointSnapshot(
            live,
            "remote",
            7,
            Snapshot("remote-boot", 4),
            tty: null);

        var stored = live.PendingTargetSessionSnapshot;
        Assert.NotNull(stored);
        Assert.Equal("ws-remote", FirstId(stored.Value, "workspaces", "workspace_id"));
        Assert.Equal("tab-main", FirstId(stored.Value, "tabs", "tab_id"));
        Assert.Equal("pane-a", FirstId(stored.Value, "panes", "pane_id"));
        Assert.Equal("remote-boot", stored.Value.GetProperty("boot_id").GetString());
        Assert.Equal(4UL, stored.Value.GetProperty("projection_revision").GetUInt64());
        Assert.True(EndpointActivationProjection.LiveEndpointSnapshotMatches(
            live,
            TargetLease(),
            Surface("remote-boot", 4)));
    }

    [Fact]
    public void CommittedDestinationSidebarListsDestinationWorkspaces()
    {
        using var peer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-sidebar-workspaces-source.sock");
        var dest = peer.ConnectClient();
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2) with { PaneId = "pane-a" });
        evidence.RecordSurface(Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" });
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingTargetSessionSnapshot = DestinationWorkspaceSnapshot();
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
            DestInputLease = "dest-input",
            DestResizeLease = "dest-resize",
        };
        StoreBoundCapture(
            live,
            SourceLease(),
            Surface("local-boot", 1),
            SampleCapturedFrame("pane-a"));
        var destLease = TargetLease() with { LeaseId = "dest-input" };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            destLease);

        AttachSession.OnEndpointSnapshot(
            live,
            "remote",
            7,
            Snapshot("remote-boot", 2) with { PaneId = "pane-a" },
            tty: null);
        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation.Phase);

        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                destLease,
                "9:7:remote-boot",
                true,
                new ActivationCompletion.Activated()),
            SourceLease(),
            destLease);
        Assert.Null(AttachSession.CompleteEndpointActivation(live, tty: null));
        Assert.Null(live.PendingActivation);

        Assert.Contains(live.SidebarInput!.Workspaces, workspace => workspace.Id == "ws-remote");
        Assert.True(live.CubeSnapshots.TryGetValue("remote", out var stored));
        Assert.Equal("ws-remote", FirstId(stored, "workspaces", "workspace_id"));
    }

    [Fact]
    public void CommitSetsDestinationLayoutRefresh()
    {
        using var peer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-commit-layout-refresh-source.sock");
        var dest = peer.ConnectClient();
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2) with { PaneId = "pane-a" });
        evidence.RecordSurface(Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" });
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
            DestInputLease = "dest-input",
            DestResizeLease = "dest-resize",
        };
        StoreBoundCapture(
            live,
            SourceLease(),
            Surface("local-boot", 1),
            SampleCapturedFrame("pane-a"));
        var destLease = TargetLease() with { LeaseId = "dest-input" };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            destLease);
        AttachSession.RetryActivationCompleteAfterCapture(live, tty: null);
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation!.Phase);

        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                destLease,
                "9:7:remote-boot",
                true,
                new ActivationCompletion.Activated()),
            SourceLease(),
            destLease);
        Assert.Null(AttachSession.CompleteEndpointActivation(live, tty: null));
        Assert.Null(live.PendingActivation);
        Assert.True(live.CommitChromeRefreshPending);
    }

    [Fact]
    public async Task CommitLayoutRefreshReadsDestinationLayoutOnce()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.RenderPort = port;
        live.PendingActivation = null;
        live.CommitChromeRefreshPending = true;

        Assert.True(await AttachSession.TryFlushCommitChromeRefreshAsync(
            live,
            tty: null,
            controlGate: null,
            CancellationToken.None));
        Assert.Contains(port.Calls, call => call.Method == ProtocolMethods.SessionSnapshot);
        Assert.Contains(port.Calls, call => call.Method == ProtocolMethods.LayoutExport);
        Assert.Contains(port.Calls, call => call.Method == ProtocolMethods.TabList);
        Assert.NotNull(live.ChromeSeed);

        var sent = port.Calls.Count;
        Assert.False(await AttachSession.TryFlushCommitChromeRefreshAsync(
            live,
            tty: null,
            controlGate: null,
            CancellationToken.None));
        Assert.Equal(sent, port.Calls.Count);
    }

    [Fact]
    public async Task CommitLayoutRefreshWaitsWhileActivationIsPending()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.RenderPort = port;
        live.CommitChromeRefreshPending = true;
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease());

        Assert.False(await AttachSession.TryFlushCommitChromeRefreshAsync(
            live,
            tty: null,
            controlGate: null,
            CancellationToken.None));
        Assert.Empty(port.Calls);
        Assert.True(live.CommitChromeRefreshPending);
    }

    [Fact]
    public void CommitEndsTheParkedSourceRead()
    {
        using var peer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-commit-reader-switch-source.sock");
        var dest = peer.ConnectClient();
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2) with { PaneId = "pane-a" });
        evidence.RecordSurface(Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" });
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
            DestInputLease = "dest-input",
            DestResizeLease = "dest-resize",
        };
        StoreBoundCapture(
            live,
            SourceLease(),
            Surface("local-boot", 1),
            SampleCapturedFrame("pane-a"));
        var destLease = TargetLease() with { LeaseId = "dest-input" };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            destLease);
        AttachSession.RetryActivationCompleteAfterCapture(live, tty: null);
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation!.Phase);

        var parked = live.ReaderSwitchToken;
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                destLease,
                "9:7:remote-boot",
                true,
                new ActivationCompletion.Activated()),
            SourceLease(),
            destLease);
        Assert.Null(AttachSession.CompleteEndpointActivation(live, tty: null));
        Assert.True(parked.IsCancellationRequested);
        Assert.Same(dest, live.ControlSlot?.Client);
        var next = live.ReaderSwitchToken;
        Assert.False(next.IsCancellationRequested);
        Assert.NotEqual(parked, next);
    }

    [Fact]
    public void SourceRestoreEndsTheParkedDestinationRead()
    {
        using var peer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-restore-reader-switch-source.sock");
        var dest = peer.ConnectClient();
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2) with { PaneId = "pane-a" });
        evidence.RecordSurface(Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" });
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
            DestInputLease = "dest-input",
            DestResizeLease = "dest-resize",
        };
        StoreBoundCapture(
            live,
            SourceLease(),
            Surface("local-boot", 1),
            SampleCapturedFrame("pane-a"));
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease() with { LeaseId = "dest-input" });
        AttachSession.RetryActivationCompleteAfterCapture(live, tty: null);
        Assert.Same(dest, live.ControlSlot?.Client);

        var parked = live.ReaderSwitchToken;
        AttachSession.ApplySourcePresentationSnapshot(
            live,
            new AttachRetargetPresentationBackup
            {
                SourceClient = source,
                SourcePort = MouseTestGeom.ApplyPort(),
            });
        Assert.True(parked.IsCancellationRequested);
        Assert.Same(source, live.ControlSlot?.Client);
    }

    [Fact]
    public async Task StallOnLiveDestinationKeepsTheDestinationClient()
    {
        using var peer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-stall-live-source.sock");
        var dest = peer.ConnectClient();
        var live = CommitReadyDestination(source, dest);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (live.HealthMonitor is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        var monitor = live.HealthMonitor;
        Assert.NotNull(monitor);

        var render = new ControlPlaneClient("/tmp/hypa-stall-live-render.sock");
        Assert.True(AttachSession.KeepCommittedEndpointAfterStall(
            live,
            source,
            render,
            "control ping failed",
            fault: null));
        Assert.Same(dest, live.ControlSlot?.Client);
        Assert.False(dest.IsDisposed);
        Assert.Empty(live.EndpointFailures);
        Assert.Same(monitor, live.HealthMonitor);
    }

    [Fact]
    public async Task StallOnClosedDestinationRecordsAnEndpointFailure()
    {
        using var peer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-stall-closed-source.sock");
        var dest = peer.ConnectClient();
        var live = CommitReadyDestination(source, dest);
        peer.Dispose();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!dest.IsTransportClosed && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(dest.IsTransportClosed);
        // The committed presentation sync faults when the peer closes.
        // That record is not this call. Wait until it has landed, then read the call.
        deadline = DateTime.UtcNow.AddSeconds(2);
        while ((live.ControlSlot is not null || live.EndpointFailures.Count == 0)
            && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.EndpointFailures.Clear();

        var render = new ControlPlaneClient("/tmp/hypa-stall-closed-render.sock");
        Assert.True(AttachSession.KeepCommittedEndpointAfterStall(
            live,
            source,
            render,
            "render closed",
            fault: null));
        var failure = Assert.Single(live.EndpointFailures);
        Assert.Equal("remote", failure.EndpointId);
        Assert.False(source.IsDisposed);
    }

    [Fact]
    public async Task CommittedEndpointIsNotLiveOnceThePeerCloses()
    {
        using var peer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-live-check-source.sock");
        var dest = peer.ConnectClient();
        var live = CommitReadyDestination(source, dest);
        Assert.True(AttachSession.CommittedEndpointIsLive(live, "remote"));

        peer.Dispose();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!dest.IsTransportClosed && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(dest.IsTransportClosed);
        Assert.False(AttachSession.CommittedEndpointIsLive(live, "remote"));
    }

    [Fact]
    public async Task DrainedPeerLossArmsTheLostConnectionNotice()
    {
        using var peer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-lost-notice-source.sock");
        var dest = peer.ConnectClient();
        var live = CommitReadyDestination(source, dest);
        peer.Dispose();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((live.ControlSlot is not null || live.EndpointFailures.Count == 0)
            && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        live.StatusError = null;
        live.FrozenChromePaintArmed = false;

        AttachSession.DrainQueuedEndpointFailures(live, tty: null);

        Assert.Equal("remote Connection closed", live.StatusError);
        Assert.True(live.FrozenChromePaintArmed);
        Assert.False(AttachSession.CommittedEndpointIsLive(live, "remote"));
    }

    [Fact]
    public void CommittedEndpointIsNotLiveWithoutAClient()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "remote";
        live.ControlSlot = null;

        Assert.False(AttachSession.CommittedEndpointIsLive(live, "remote"));
    }

    [Fact]
    public void StallWithoutCommittedEndpointUsesTheLocalPath()
    {
        var control = new ControlPlaneClient("/tmp/hypa-stall-uncommitted-control.sock");
        var render = new ControlPlaneClient("/tmp/hypa-stall-uncommitted-render.sock");
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ControlSlot = new AttachControlSlot { Client = control };

        Assert.False(AttachSession.KeepCommittedEndpointAfterStall(
            live,
            control,
            render,
            "control ping failed",
            fault: null));
        Assert.False(control.IsDisposed);
        Assert.False(render.IsDisposed);
        Assert.Same(control, live.ControlSlot?.Client);
    }

    [Fact]
    public async Task LocalLossUsesTheLocalPath()
    {
        using var destPeer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-stall-local-loss-source.sock");
        var dest = destPeer.ConnectClient();
        var live = CommitReadyDestination(source, dest);
        AcceptingPeer? controlPeer = AcceptingPeer.Listen();
        try
        {
            var control = controlPeer.ConnectClient();
            controlPeer.Dispose();
            controlPeer = null;
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!control.IsTransportClosed && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.True(control.IsTransportClosed);
            Assert.False(AttachSession.KeepCommittedEndpointAfterStall(
                live,
                control,
                source,
                "render closed",
                fault: null));
        }
        finally
        {
            controlPeer?.Dispose();
        }
    }

    private static AttachLiveState CommitReadyDestination(ControlPlaneClient source, ControlPlaneClient dest)
    {
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2) with { PaneId = "pane-a" });
        evidence.RecordSurface(Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" });
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
            DestInputLease = "dest-input",
            DestResizeLease = "dest-resize",
        };
        StoreBoundCapture(
            live,
            SourceLease(),
            Surface("local-boot", 1),
            SampleCapturedFrame("pane-a"));
        var destLease = TargetLease() with { LeaseId = "dest-input" };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            destLease);
        AttachSession.RetryActivationCompleteAfterCapture(live, tty: null);
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation!.Phase);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                destLease,
                "9:7:remote-boot",
                true,
                new ActivationCompletion.Activated()),
            SourceLease(),
            destLease);
        Assert.Null(AttachSession.CompleteEndpointActivation(live, tty: null));
        Assert.Same(dest, live.ControlSlot?.Client);
        return live;
    }

    [Fact]
    public void CaptureRetryDoesNotCompletePresentationSyncWithoutAck()
    {
        using var peer = AcceptingPeer.Listen();
        var dest = peer.ConnectClient();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
            DestInputLease = "dest-input",
            DestResizeLease = "dest-resize",
        };
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2) with { PaneId = "pane-a" });
        evidence.RecordSurface(Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" });
        var destLease = TargetLease() with { LeaseId = "dest-input" };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            destLease);
        StoreBoundCapture(
            live,
            destLease,
            Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" },
            SampleCapturedFrame("pane-a"));

        Assert.Null(AttachSession.CompleteEndpointActivation(live, tty: null));
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation!.Phase);

        AttachSession.RetryActivationCompleteAfterCapture(live, tty: null);

        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation.Phase);
        Assert.Same(dest, live.ControlSlot?.Client);
        Assert.Null(live.StatusError);
        Assert.False(dest.IsDisposed);
    }

    [Fact]
    public void CompleteWithoutSyncAckKeepsDestAndDoesNotRollback()
    {
        var dest = new ControlPlaneClient("/tmp/hypa-sync-complete-no-ack.sock");
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
        };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.SynchronizingPresentation(
                TargetLease(),
                "client-shell-surface:9:presentation-sync",
                null,
                new EndpointActivationEvidence(),
                new ActivationCompletion.Activated()),
            SourceLease(),
            TargetLease());

        Assert.Null(AttachSession.CompleteEndpointActivation(live, tty: null));

        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation.Phase);
        Assert.Same(dest, live.ControlSlot?.Client);
        Assert.NotNull(live.PendingConnectOutcome);
        Assert.False(dest.IsDisposed);
        Assert.Contains("surface acknowledgement", live.StatusError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaleCaptureDumpReportsMismatch()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2));
        evidence.RecordSurface(Surface("remote-boot", 2));
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease());
        StoreBoundCapture(
            live,
            TargetLease(),
            Surface("remote-boot", 1),
            SampleCapturedFrame("pane-a"));

        var dump = AttachSession.FormatActivationGateDump(live, "probe");
        Assert.Contains("capture=False", dump, StringComparison.Ordinal);
        Assert.Contains("bind=1:1", dump, StringComparison.Ordinal);
        Assert.Contains("ev=2:2", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffUnavailableAfterSetActiveKeepsControlClient()
    {
        var dest = new ControlPlaneClient("/tmp/hypa-handoff-keep-control.sock");
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
        };

        AttachSession.PresentHandoffUnavailable(live, "connect failed");

        Assert.Same(dest, live.ControlSlot?.Client);
        Assert.False(dest.IsDisposed);
        Assert.NotNull(live.PendingConnectOutcome);
        Assert.Equal("connect failed", live.StatusError);
    }

    [Fact]
    public void DisconnectAfterCommitLeavesActiveDestClient()
    {
        var dest = new ControlPlaneClient("/tmp/hypa-disconnect-keep-active.sock");
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ControlSlot = new AttachControlSlot { Client = dest };
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with { DestClient = dest };

        AttachSession.DisconnectPendingConnectTransport(live);

        Assert.Same(dest, live.ControlSlot?.Client);
        Assert.False(dest.IsDisposed);
        Assert.Null(live.PendingConnectOutcome);
    }

    [Fact]
    public void AdvancedSurfaceRenderCompletesTargetInOneAdmit()
    {
        using var peer = AcceptingPeer.Listen();
        var dest = peer.ConnectClient();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
            DestInputLease = "dest-input",
            DestResizeLease = "dest-resize",
        };
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2) with { PaneId = "pane-a" });
        evidence.RecordSurface(Surface("remote-boot", 1) with { FocusedPaneId = "pane-a" });
        var destLease = TargetLease() with { LeaseId = "dest-input" };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            destLease);

        using var doc = JsonDocument.Parse(
            """
            {
              "event":"terminal.render",
              "params":{
                "pane_id":"pane-a",
                "kind":"cells",
                "full":true,
                "reanchor":true,
                "grid_cols":80,
                "grid_rows":24,
                "generation":2,
                "boot_id":"remote-boot",
                "projection_revision":2,
                "surface_revision":2,
                "columns":80,
                "focused":true,
                "focused_pane_id":"pane-a",
                "rows":[{"i":0,"t":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}]
              }
            }
            """);
        AttachEndpointActivationPump.AdmitAttachEvent(
            live,
            "remote",
            7,
            doc.RootElement,
            tty: null);

        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation!.Phase);
        Assert.Same(dest, live.ControlSlot?.Client);
        Assert.False(dest.IsDisposed);
    }

    [Fact]
    public void CompleteFailAfterSyncOkWritesErrorOutcome()
    {
        var dest = new ControlPlaneClient("/tmp/hypa-complete-fail-outcome.sock");
        var sink = new CapturingProcessLogSink(ProcessLogLevel.Debug);
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ProcessLog = sink;
        live.CubesConnectOutcomeRecorded = true;
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
        };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.SynchronizingPresentation(
                TargetLease(),
                "client-shell-surface:9:presentation-sync",
                null,
                new EndpointActivationEvidence(),
                new ActivationCompletion.Activated()),
            SourceLease(),
            TargetLease());

        Assert.Null(AttachSession.CompleteEndpointActivation(live, tty: null));
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.CubesConnectOutcome
                && r.Outcome == ProcessLogEvents.OutcomeError);
    }

    [Fact]
    public void SnapshotSurfaceRevisionMismatchFailsClosed()
    {
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2));
        evidence.RecordSurface(Surface("remote-boot", 1));
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease());
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        StoreBoundCapture(
            live,
            TargetLease(),
            Surface("remote-boot", 2),
            SampleCapturedFrame("pane-a"));
        var result = activation.Complete(live, new FakeRegistry());
        Assert.False(result.IsOk);
        Assert.Contains("coherent snapshot/surface pair", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivationCaptureDoesNotMergeOntoSourcePaneFrame()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.SetPaneFrame(MouseTestGeom.Frame("source", "pane-a", 80));
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 3));
        evidence.RecordSurface(Surface("remote-boot", 3));
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                3,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease());
        using var doc = JsonDocument.Parse(
            """
            {
              "pane_id":"pane-a",
              "kind":"cells",
              "full":true,
              "reanchor":true,
              "grid_cols":80,
              "grid_rows":1,
              "generation":3,
              "rows":[{"i":0,"t":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}]
            }
            """);
        Assert.True(AttachSession.TryCaptureActivationTerminalRender(
            live,
            "remote",
            7,
            doc.RootElement,
            assembler: null));
        var surface = Surface("remote-boot", 3) with { FocusedPaneId = "pane-a", Rows = 1 };
        Assert.True(live.TryInstallCoherentSurface(surface, TargetLease(), out _));
        Assert.True(live.TryGetPaneFrame("pane-a", out var frame));
        Assert.Equal("d", frame!.Cells[0][0].Text);
    }

    [Fact]
    public void FenceSendFailureFailsClosedAtSendTime()
    {
        var registry = new FenceFailRegistry();
        var lease = TargetLease() with { LeaseId = "dest-input-lease" };
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2));
        evidence.RecordSurface(Surface("remote-boot", 2));
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.SynchronizingPresentation(
                lease,
                "client-shell-surface:9:presentation-sync",
                2,
                evidence,
                new ActivationCompletion.Activated()),
            SourceLease(),
            TargetLease());
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.SetEndpointStatus("remote", ClientEndpointStatus.Online);
        registry.ActiveId = "remote";
        var syncSurface = Surface("remote-boot", 2);
        StoreBoundCapture(live, lease, syncSurface, SampleCapturedFrame("pane-a"));
        var result = activation.Complete(live, registry);
        Assert.False(result.IsOk);
        Assert.Contains("fence could not be sent", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FenceRpcFailureInAwaitingEffectsIsRejected()
    {
        var token = "9:7:remote-boot";
        var activation = AtPhase(new ActivationPhase.AwaitingPresentationEffects(
            TargetLease() with { LeaseId = "dest-input-lease" },
            token,
            false,
            new ActivationCompletion.Activated()));
        var progress = activation.ReceiveResponseForBoot(
            "remote",
            7,
            "remote-boot",
            token,
            new EndpointSurfaceControlResult
            {
                Ok = false,
                HasErrorCode = true,
                ErrorMessage = "lease rejected",
                BootId = "remote-boot",
                RequestId = token,
            },
            new FakeRegistry());
        Assert.IsType<SurfaceActivationProgress.Rejected>(progress);
    }

    [Fact]
    public void DestSnapshotCaptureRequiresActivationAssembler()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 3));
        evidence.RecordSurface(Surface("remote-boot", 3));
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                3,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease());
        var payload = SnapshotRenderPayload("pane-a", generation: 3);
        Assert.False(AttachSession.TryCaptureActivationTerminalRender(
            live,
            "remote",
            7,
            payload,
            assembler: null));
        Assert.True(AttachSession.TryCaptureActivationTerminalRender(
            live,
            "remote",
            7,
            payload,
            live.ActivationSnapshotAssembler));
    }

    [Fact]
    public void DestDrainCapturesSnapshotThroughActivationAssembler()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 3));
        evidence.RecordSurface(Surface("remote-boot", 3));
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease());
        using var doc = JsonDocument.Parse(
            """
            {
              "event":"terminal.render",
              "params":{
                "pane_id":"pane-a",
                "kind":"snapshot",
                "row_start":0,
                "row_end":1,
                "complete":true,
                "grid_cols":80,
                "grid_rows":1,
                "generation":3,
                "provider":"basic",
                "active_screen":"main",
                "snapshot":{"provider":"basic","active_screen":"main"}
              }
            }
            """);
        AttachEndpointActivationPump.AdmitAttachEvent(
            live,
            "remote",
            7,
            doc.RootElement,
            tty: null);
        var drainSurface = Surface("remote-boot", 3) with { FocusedPaneId = "pane-a", Columns = 80, Rows = 1 };
        Assert.True(live.TryInstallCoherentSurface(drainSurface, TargetLease(), out _));
        Assert.True(live.TryGetPaneFrame("pane-a", out var frame));
        Assert.NotNull(frame);
        Assert.Equal(80, frame!.Cols);
    }

    [Fact]
    public void DestDrainUnwrapsRuntimeEventTerminalRender()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 3));
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease(),
            resize: new AttachGeometry
            {
                Columns = 80,
                Rows = 1,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            });
        using var doc = JsonDocument.Parse(
            """
            {
              "event":"runtime.event",
              "params":{
                "type":"terminal.render",
                "payload":{
                  "pane_id":"pane-a",
                  "kind":"snapshot",
                  "row_start":0,
                  "row_end":1,
                  "complete":true,
                  "grid_cols":80,
                  "grid_rows":1,
                  "generation":3,
                  "boot_id":"remote-boot",
                  "projection_revision":3,
                  "surface_revision":3,
                  "columns":80,
                  "rows":1,
                  "focused":true,
                  "focused_pane_id":"pane-a",
                  "provider":"basic",
                  "active_screen":"main",
                  "snapshot":{"provider":"basic","active_screen":"main"}
                }
              }
            }
            """);
        AttachEndpointActivationPump.AdmitAttachEvent(
            live,
            "remote",
            7,
            doc.RootElement,
            tty: null);
        var drainSurface = Surface("remote-boot", 3) with { FocusedPaneId = "pane-a", Columns = 80, Rows = 1 };
        Assert.True(live.TryInstallCoherentSurface(drainSurface, TargetLease(), out _));
        Assert.True(live.TryGetPaneFrame("pane-a", out var frame));
        Assert.NotNull(frame);
        Assert.Equal(80, frame!.Cols);
    }

    [Fact]
    public void DestDrainReadsGeometryFromGridWhenRowsIsCellArray()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 3));
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease(),
            resize: new AttachGeometry
            {
                Columns = 80,
                Rows = 1,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            });
        using var doc = JsonDocument.Parse(
            """
            {
              "event":"runtime.event",
              "params":{
                "type":"terminal.render",
                "payload":{
                  "pane_id":"pane-a",
                  "kind":"cells",
                  "full":true,
                  "grid_cols":80,
                  "grid_rows":1,
                  "generation":3,
                  "boot_id":"remote-boot",
                  "projection_revision":3,
                  "surface_revision":3,
                  "columns":80,
                  "rows":[{"i":0,"t":"hello                               "}],
                  "focused":true,
                  "focused_pane_id":"pane-a"
                }
              }
            }
            """);
        AttachEndpointActivationPump.AdmitAttachEvent(
            live,
            "remote",
            7,
            doc.RootElement,
            tty: null);
        var drainSurface = Surface("remote-boot", 3) with { FocusedPaneId = "pane-a", Columns = 80, Rows = 1 };
        Assert.True(live.TryInstallCoherentSurface(drainSurface, TargetLease(), out _));
        Assert.True(live.TryGetPaneFrame("pane-a", out var frame));
        Assert.NotNull(frame);
        Assert.Equal(80, frame!.Cols);
    }

    [Fact]
    public async Task FrozenControlDrainSkipsPopupLifecycleMutation()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease());
        live.PopupOpen = false;
        using var doc = JsonDocument.Parse(
            """
            {"event":"runtime.event","params":{"type":"popup.lifecycle","payload":{"state":"opened","cols":40,"rows":10},"seq":1}}
            """);
        await AttachSession.ApplyControlEventsAsync(
            [doc.RootElement],
            live,
            MouseTestGeom.ApplyPort(),
            tty: null,
            CancellationToken.None);
        Assert.False(live.PopupOpen);
    }

    [Fact]
    public void EffectsReadyAdmittedOnLoopCompletesAwaitingFence()
    {
        var token = "9:7:remote-boot";
        var activation = AtPhase(new ActivationPhase.AwaitingPresentationEffects(
            TargetLease(),
            token,
            false,
            new ActivationCompletion.Activated()));
        var progress = activation.ReceivePresentationEffectsReady("remote", 7, token);
        Assert.IsType<SurfaceActivationProgress.Ready>(progress);
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        var completion = activation.Complete(live, new FakeRegistry());
        Assert.True(completion.IsOk);
        Assert.IsType<ActivationCompletion.Activated>(completion.Value);
    }

    [Fact]
    public void PerEndpointSurfaceActiveDefaultsTrueUntilExplicitOff()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        Assert.True(live.GetEndpointSurfaceActive("local"));
        live.SetEndpointSurfaceActive("local", false);
        Assert.False(live.GetEndpointSurfaceActive("local"));
        live.SetEndpointSurfaceActive("remote", true);
        Assert.True(live.GetEndpointSurfaceActive("remote"));
    }

    [Fact]
    public async Task CaptureWriteBlocksUntilInstallReadReleasesGate()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var surface = Surface("remote-boot", 2);
        StoreBoundCapture(live, TargetLease(), surface, SampleCapturedFrame("pane-a"));
        using var installEntered = new ManualResetEventSlim(false);
        using var captureStarted = new ManualResetEventSlim(false);
        using var captureDone = new ManualResetEventSlim(false);
        var captureFinishedWhileGateHeld = true;

        var installTask = Task.Run(() =>
        {
            lock (live.ActivationGate)
            {
                installEntered.Set();
                // Hold the gate until the capture thread has started, then a
                // little longer, and record whether the capture got through.
                captureStarted.Wait(TimeSpan.FromSeconds(5));
                Thread.Sleep(100);
                captureFinishedWhileGateHeld = captureDone.IsSet;
            }
        });

        installEntered.Wait(TimeSpan.FromSeconds(5));
        var captureTask = Task.Run(() =>
        {
            captureStarted.Set();
            live.StoreActivationCapturedFrame(new ActivationCapturedFrame(
                new ActivationCaptureBinding("remote", 7, "remote-boot", 2, 2),
                SampleCapturedFrame("pane-a")));
            captureDone.Set();
        });

        await Task.WhenAll(installTask, captureTask);
        Assert.False(captureFinishedWhileGateHeld, "the capture write must wait for the gate");
    }

    [Fact]
    public void SnapshotIdentityMismatchFailsClosedBeforeInstall()
    {
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2));
        evidence.RecordSurface(Surface("remote-boot", 2));
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            TargetLease() with { ConnectionGeneration = 9 });
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        StoreBoundCapture(
            live,
            TargetLease() with { ConnectionGeneration = 9 },
            Surface("remote-boot", 2),
            SampleCapturedFrame("pane-a"));
        var result = activation.Complete(live, new FakeRegistry());
        Assert.False(result.IsOk);
        Assert.Contains("coherent snapshot/surface pair", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BelowMinimumProjectionRevisionSurfaceIsStale()
    {
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease() with { MinimumProjectionRevision = 5 });
        var progress = activation.ReceiveSurface("remote", 7, Surface("remote-boot", 3));
        Assert.IsType<SurfaceActivationProgress.Stale>(progress);
    }

    [Fact]
    public void UnavailableOwnerFreezeClearsActivationCaptures()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        StoreBoundCapture(
            live,
            TargetLease(),
            Surface("remote-boot", 2),
            SampleCapturedFrame("pane-a"));
        Assert.True(live.HasActivationCapturedFrame("remote"));
        AttachSession.ApplyUnavailableOwnerFreeze(live);
        Assert.False(live.HasActivationCapturedFrame("remote"));
    }

    [Fact]
    public void FreshDestTargetLeaseStartsAtProjectionRevisionZero()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "boot_id":"target-boot",
              "revision":4,
              "projection_revision":4
            }
            """);
        var outcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            DestSnapshot = doc.RootElement.Clone(),
            TargetBootId = "target-boot",
            TargetConnectionGeneration = 9,
            DestInputLease = "dest-input",
        };
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.AttachClientId = "conn_2";
        var lease = AttachSession.BuildTargetLeaseForTests(
            live,
            new SidebarCubeItem
            {
                Id = "remote",
                Name = "remote",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
            outcome);
        Assert.Equal(0UL, lease.MinimumProjectionRevision);
        Assert.Equal("target-boot", lease.BootId);
        Assert.Equal(9UL, lease.ConnectionGeneration);
        // The peer's mux connection id is not unique on the target mux.
        Assert.Equal(live.EndpointClientId, lease.ClientId);
    }

    [Fact]
    public void DestSurfaceOnAckRaisesFloorAndAdmitsRevisionOne()
    {
        var registry = new FakeRegistry();
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "client-shell-surface:9:on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease());
        Assert.Equal(0UL, activation.Target.MinimumProjectionRevision);

        var ack = activation.ReceiveResponseForBoot(
            "remote",
            7,
            "remote-boot",
            "client-shell-surface:9:on",
            new EndpointSurfaceControlResult
            {
                Ok = true,
                ProjectionRevision = 1,
                BootId = "remote-boot",
                RequestId = "client-shell-surface:9:on",
                ConnectionGeneration = 7,
            },
            registry);
        Assert.IsType<SurfaceActivationProgress.Pending>(ack);
        Assert.Equal(1UL, activation.Target.MinimumProjectionRevision);

        Assert.IsType<SurfaceActivationProgress.Pending>(
            activation.ReceiveSnapshot("remote", 7, Snapshot("remote-boot", 1)));
        Assert.IsType<SurfaceActivationProgress.Ready>(
            activation.ReceiveSurface("remote", 7, Surface("remote-boot", 1)));
    }

    [Fact]
    public void SameBootDestReconnectKeepsConnectionGenerationIdentity()
    {
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "client-shell-surface:9:on",
                1,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease() with { ConnectionGeneration = 2, BootId = "remote-boot" });
        var stale = activation.ReceiveSnapshot("remote", 1, Snapshot("remote-boot", 1));
        Assert.IsType<SurfaceActivationProgress.Stale>(stale);
        var matching = activation.ReceiveSnapshot("remote", 2, Snapshot("remote-boot", 1));
        Assert.IsType<SurfaceActivationProgress.Pending>(matching);
    }

    [Fact]
    public void SourceLeaseFloorUsesLastSnapshotWhenCoherentPaneSurfaceNull()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "boot_id":"local-boot",
              "revision":6,
              "projection_revision":6
            }
            """);
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.LastSnapshot = doc.RootElement.Clone();
        live.LastSnapshotConnectionGeneration = 3;
        var outcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            SourceBootId = "local-boot",
            SourceConnectionGeneration = 3,
            TargetConnectionGeneration = 9,
            DestInputLease = "dest-input",
        };
        var request = new CubesConnectRequest
        {
            Destination = new SidebarCubeItem
            {
                Id = "remote",
                Name = "remote",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
            SourceSurfaceActive = true,
            SourceEndpointId = "local",
            SourceInputLease = "src-input-lease",
            SourceClient = new ControlPlaneClient("/tmp/hypa-source-floor-test.sock"),
            SourceControl = MouseTestGeom.ApplyPort(),
        };
        var lease = AttachSession.BuildSourceLeaseForTests(live, request, outcome);
        Assert.Equal(6UL, lease?.MinimumProjectionRevision);
    }

    [Fact]
    public void SourceLeaseAdoptsLiveSessionSnapshotOnFirstSourceHello()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "focused_workspace_id":"ws-local",
              "workspaces":[{"workspace_id":"ws-local","label":"mac"}]
            }
            """);
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.LastSnapshot = doc.RootElement.Clone();
        live.LastSnapshotConnectionGeneration = 0;
        var outcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            SourceBootId = "local-boot",
            SourceConnectionGeneration = 1,
            TargetConnectionGeneration = 9,
            DestInputLease = "dest-input",
        };
        var request = new CubesConnectRequest
        {
            Destination = new SidebarCubeItem
            {
                Id = "remote",
                Name = "remote",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
            SourceSurfaceActive = true,
            SourceEndpointId = "local",
            SourceInputLease = "src-input-lease",
            SourceClient = new ControlPlaneClient("/tmp/hypa-source-adopt.sock"),
            SourceControl = MouseTestGeom.ApplyPort(),
        };

        var lease = AttachSession.BuildSourceLeaseForTests(live, request, outcome);
        Assert.NotNull(lease);
        Assert.Equal(1UL, live.LastSnapshotConnectionGeneration);
        Assert.Equal("local-boot", live.SourceEndpointBootId);
        Assert.Equal(1UL, lease!.ConnectionGeneration);
    }

    [Fact]
    public void SourceLeaseRefusesSnapshotBoundToAnotherGeneration()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "boot_id":"local-boot",
              "revision":2,
              "projection_revision":2
            }
            """);
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.LastSnapshot = doc.RootElement.Clone();
        live.LastSnapshotConnectionGeneration = 99;
        var outcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            SourceBootId = "local-boot",
            SourceConnectionGeneration = 1,
            TargetConnectionGeneration = 9,
            DestInputLease = "dest-input",
        };
        var request = new CubesConnectRequest
        {
            Destination = new SidebarCubeItem
            {
                Id = "remote",
                Name = "remote",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
            SourceSurfaceActive = true,
            SourceEndpointId = "local",
            SourceInputLease = "src-input-lease",
            SourceClient = new ControlPlaneClient("/tmp/hypa-source-stale-gen.sock"),
            SourceControl = MouseTestGeom.ApplyPort(),
        };

        Assert.False(AttachSession.TryBuildSourceLeaseForTests(
            live, request, outcome, out var lease, out var error));
        Assert.Null(lease);
        Assert.Contains("not ready", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(99UL, live.LastSnapshotConnectionGeneration);
    }

    [Fact]
    public void SourceLeaseRefusedWhenLastSnapshotUnready()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.LastSnapshot = null;
        var outcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            SourceConnectionGeneration = 3,
            TargetConnectionGeneration = 9,
            DestInputLease = "dest-input",
        };
        var request = new CubesConnectRequest
        {
            Destination = new SidebarCubeItem
            {
                Id = "remote",
                Name = "remote",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
            SourceSurfaceActive = true,
            SourceEndpointId = "local",
            SourceInputLease = "src-input-lease",
            SourceClient = new ControlPlaneClient("/tmp/hypa-source-unready.sock"),
            SourceControl = MouseTestGeom.ApplyPort(),
        };
        Assert.False(AttachSession.TryBuildSourceLeaseForTests(
            live,
            request,
            outcome,
            out var lease,
            out var error));
        Assert.Null(lease);
        Assert.Contains("not ready", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaleSnapshotGenerationFailsClosedBeforeSourceRestoreInstall()
    {
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("local", 1, Snapshot("local-boot", 2));
        evidence.RecordSurface(Surface("local-boot", 2));
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.RestoringSource(
                "on",
                2,
                evidence),
            SourceLease(),
            TargetLease());
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        SeedSourceLiveSnapshot(live, "local-boot", 2);
        live.LastSnapshotConnectionGeneration = 99;
        StoreBoundCapture(
            live,
            SourceLease(),
            Surface("local-boot", 2),
            SampleCapturedFrame("pane-a"));
        var result = activation.Complete(live, new FakeRegistry());
        Assert.False(result.IsOk);
        Assert.Contains("coherent snapshot/surface pair", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PresentHandoffUnavailableKeepsPendingDestTransport()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var destClient = new ControlPlaneClient("/tmp/hypa-release-unavailable.sock");
        var control = MouseTestGeom.ApplyPort();
        live.PendingConnectControl = control;
        live.PendingConnectOutcome = MinimalConnectOutcome(destClient);
        AttachSession.PresentHandoffUnavailable(live, "endpoint unavailable");
        Assert.NotNull(live.PendingConnectOutcome);
        Assert.Same(control, live.PendingConnectControl);
        Assert.False(destClient.IsDisposed);
        Assert.True(live.PresentationFrozen);
        Assert.False(live.InputFrozen);
        Assert.False(live.PlacementOwnerUnavailable);
        Assert.False(AttachSession.BlocksPresentationInput(live));
        Assert.True(AttachSession.BlocksPaneKeys(live));
        Assert.Equal("endpoint unavailable", live.StatusError);
    }

    [Fact]
    public void PresentConnectFailureKeepsLiveSourceAndCubeRetry()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.SetPaneFrame(MouseTestGeom.Frame("source", "p1", 80));
        live.ObservedPaneIds.Add("p1");
        live.ControlSlot = new AttachControlSlot
        {
            Client = new ControlPlaneClient("/tmp/hypa-connect-failure-source.sock"),
        };
        live.InputSender = new AttachInputSender(
            new ControlPlaneClient("/tmp/hypa-connect-failure-keys.sock"),
            () => (live.PaneId, live.InputLease),
            blocksForward: () => AttachSession.BlocksPaneKeys(live));
        // Delivery runs on the sender loop. Hold notify so an unconnected
        // client does not fault the sender before the second enqueue.
        live.InputSender.BlockNotifyForTests = true;
        try
        {
            Assert.True(live.InputSender.TryEnqueue([0x61]));
            // Wait until the sender loop is parked in delivery. While it
            // prepares a batch it holds the retarget admission, and a
            // second enqueue in that window is rejected.
            Assert.True(live.InputSender.NotifyEnteredForTests.Wait(TimeSpan.FromSeconds(5)));

            AttachSession.PresentConnectFailure(live, AttachEndpointUserCopy.DestLeaseDenied);

            Assert.False(live.PresentationFrozen);
            Assert.False(live.FrozenChromePaintArmed);
            Assert.False(live.InputFrozen);
            Assert.False(live.PlacementOwnerUnavailable);
            Assert.False(AttachSession.BlocksPresentationInput(live));
            Assert.False(AttachSession.BlocksPaneKeys(live));
            Assert.Equal(AttachEndpointUserCopy.DestLeaseDenied, live.StatusError);
            Assert.Equal(CubesConnectActions.Noop, live.CubesConnectAction);
            Assert.True(live.TryGetPaneFrame("p1", out _));
            Assert.Contains("p1", live.ObservedPaneIds);
            Assert.True(live.InputSender.TryEnqueue([0x62]));
        }
        finally
        {
            live.InputSender.NotifyHoldForTests.Release();
        }
    }

    [Fact]
    public async Task PresentConnectFailureStillForwardsPaneKeys()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.SetPaneFrame(MouseTestGeom.Frame("source", "p1", 80));
        AttachSession.PresentConnectFailure(live, AttachEndpointUserCopy.DestLeaseDenied);

        var forwarded = 0;
        using var gate = new SemaphoreSlim(1, 1);
        using var linked = new CancellationTokenSource();
        await AttachSession.SendPaneKeysUnderGateAsync(
            _ =>
            {
                forwarded++;
                return Task.CompletedTask;
            },
            gate,
            live,
            tty: null,
            linked,
            CancellationToken.None);
        Assert.Equal(1, forwarded);
        Assert.False(live.PresentationFrozen);
        Assert.False(AttachSession.BlocksPaneKeys(live));
    }

    [Fact]
    public void RestoredSourceCompletionReleasesPendingDestTransport()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var destClient = new ControlPlaneClient("/tmp/hypa-release-restored.sock");
        var control = MouseTestGeom.ApplyPort();
        live.PendingConnectControl = control;
        live.PendingConnectOutcome = MinimalConnectOutcome(destClient);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                SourceLease(),
                "9:1:local-boot",
                true,
                new ActivationCompletion.RestoredSource("endpoint handoff was rolled back", null)),
            SourceLease(),
            TargetLease());
        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(successor);
        Assert.Null(live.PendingActivation);
        Assert.Null(live.PendingConnectOutcome);
        Assert.Same(control, live.PendingConnectControl);
        Assert.True(SpinWaitUntilDisposed(destClient));
    }

    [Fact]
    public void RestoredSourceWithSuccessorPreservesConnectSessionForRelaunch()
    {
        var successorIntent = new EndpointActivationIntent { EndpointId = "other-remote" };
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var destClient = new ControlPlaneClient("/tmp/hypa-restored-successor.sock");
        var control = MouseTestGeom.ApplyPort();
        using var successorCt = new CancellationTokenSource();
        live.PendingConnectControl = control;
        live.PendingConnectCt = successorCt.Token;
        live.PendingConnectOutcome = MinimalConnectOutcome(destClient);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                SourceLease(),
                "9:1:local-boot",
                true,
                new ActivationCompletion.RestoredSource(
                    "endpoint handoff was rolled back",
                    successorIntent)),
            SourceLease(),
            TargetLease());
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "other-remote",
                Name = "other-remote",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.NotNull(successor);
        Assert.Equal("other-remote", successor.EndpointId);
        Assert.Null(live.PendingActivation);
        Assert.Null(live.PendingConnectOutcome);
        Assert.Same(control, live.PendingConnectControl);
        Assert.False(live.PendingConnectCt.IsCancellationRequested);
        Assert.True(SpinWaitUntilDisposed(destClient));
    }

    [Fact]
    public void TargetRegistryFailDisconnectsDestTransportWithoutClearingConnectSession()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var destClient = new ControlPlaneClient("/tmp/hypa-fail-disconnect.sock");
        var control = MouseTestGeom.ApplyPort();
        live.PendingConnectControl = control;
        live.PendingConnectCt = CancellationToken.None;
        live.PendingConnectOutcome = MinimalConnectOutcome(destClient);
        var activation = AtPhase(new ActivationPhase.ReleasingTargetForRollback("rollback-target-off"));
        var port = new AttachEndpointRegistryPort(
            live,
            live.ControlSlot?.Client is { } sourceClient
                ? new AttachEndpointRpcClient(sourceClient)
                : null,
            new AttachEndpointRpcClient(destClient),
            activation.Source,
            activation.Target,
            _ => { },
            (_, _, _) => { });
        port.Fail("remote", "endpoint did not acknowledge surface revocation");
        Assert.Null(live.PendingConnectOutcome);
        Assert.Same(control, live.PendingConnectControl);
        Assert.True(SpinWaitUntilDisposed(destClient));
    }

    [Fact]
    public void PostConnectSupersedeRetainsPendingActivation()
    {
        using var existingPeer = AcceptingPeer.Listen();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var existing = AtPhase(new ActivationPhase.ActivatingTarget(
            "on",
            null,
            null,
            null,
            true,
            new EndpointActivationEvidence()));
        live.PendingActivation = existing;
        live.PendingConnectOutcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            TargetConnectionGeneration = 7,
            DestInputLease = "dest-input",
            DestClient = existingPeer.ConnectClient(),
        };
        using var newPeer = AcceptingPeer.Listen();
        var destClient = newPeer.ConnectClient();
        var outcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            DestClient = destClient,
            TargetConnectionGeneration = 7,
            DestInputLease = "dest-input",
        };
        var intent = new EndpointActivationIntent { EndpointId = "other-remote" };
        var result = AttachSession.CommitActivationAfterConnectForTests(
            live,
            new SidebarCubeItem
            {
                Id = "other-remote",
                Name = "other-remote",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
            intent,
            MouseTestGeom.ApplyPort(),
            tty: null,
            CancellationToken.None,
            new CubesConnectRequest
            {
                Destination = new SidebarCubeItem
                {
                    Id = "other-remote",
                    Name = "other-remote",
                    Kind = SidebarCubeKind.Peer,
                    Reachability = SidebarCubeReachability.Reachable,
                },
                SourceSurfaceActive = false,
            },
            outcome,
            Geometry());
        Assert.Equal(AttachSession.PostConnectActivationResult.Superseded, result);
        Assert.Same(existing, live.PendingActivation);
        Assert.Equal("other-remote", existing.Successor?.EndpointId);
    }

    [Fact]
    public void PostCompleteCaptureIsRejected()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var destClient = new ControlPlaneClient("/tmp/hypa-post-complete-capture.sock");
        live.ControlSlot = new AttachControlSlot { Client = destClient };
        live.PendingConnectOutcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            DestClient = destClient,
            TargetConnectionGeneration = 7,
            DestInputLease = "dest-input",
        };
        live.PendingActivation = AtPhase(new ActivationPhase.AwaitingPresentationEffects(
            TargetLease(),
            "1:7:remote-boot",
            true,
            new ActivationCompletion.Activated()));
        AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(live.PendingActivation);
        using var doc = JsonDocument.Parse(
            """
            {
              "pane_id":"pane-a",
              "kind":"cells",
              "full":true,
              "reanchor":true,
              "grid_cols":80,
              "grid_rows":1,
              "generation":2,
              "rows":[{"i":0,"t":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}]
            }
            """);
        Assert.False(AttachSession.TryCaptureActivationTerminalRender(
            live,
            "remote",
            7,
            doc.RootElement,
            assembler: null));
    }

    [Fact]
    public void LiveAttachSnapshotDriftFailsClosedBeforeInstall()
    {
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("local", 1, Snapshot("local-boot", 2));
        evidence.RecordSurface(Surface("local-boot", 2));
        var activation = PendingEndpointActivation.ForTests(
            new ActivationPhase.RestoringSource(
                "on",
                2,
                evidence),
            SourceLease(),
            TargetLease());
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        SeedSourceLiveSnapshot(live, "local-boot", 3);
        StoreBoundCapture(
            live,
            SourceLease(),
            Surface("local-boot", 2),
            SampleCapturedFrame("pane-a"));
        var result = activation.Complete(live, new FakeRegistry());
        Assert.False(result.IsOk);
        Assert.Contains("coherent snapshot/surface pair", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentCompletionEnqueueIsNotLostOnPumpDrain()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PendingActivation = AtPhase(new ActivationPhase.ActivatingTarget(
            "on",
            null,
            null,
            null,
            true,
            new EndpointActivationEvidence()));
        const int count = 24;
        var tasks = Enumerable.Range(0, count).Select(index => Task.Run(() =>
            AttachSession.EnqueueActivationCompletion(
                live,
                new EndpointActivationRpcCompletion.EffectsReady(
                    "remote",
                    7,
                    $"token-{index}",
                    new AttachPresentationSync
                    {
                        RequestId = $"token-{index}",
                        LeaseId = "dest-input",
                        BootId = "remote-boot",
                        ProjectionRevision = 1,
                        SurfaceRevision = 1,
                    })))).ToArray();
        await Task.WhenAll(tasks);
        int queued;
        lock (live.ActivationGate)
            queued = live.ActivationCompletions.Count;
        Assert.Equal(count, queued);
        AttachSession.ProcessActivationCompletionsForPump(live, tty: null);
        lock (live.ActivationGate)
            Assert.Empty(live.ActivationCompletions);
    }

    [Fact]
    public void SourceLeaseUsesPreflightBootAndGeneration()
    {
        var outcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            SourceBootId = "source-boot",
            SourceConnectionGeneration = 3,
            TargetBootId = "target-boot",
            TargetConnectionGeneration = 9,
            DestInputLease = "dest-input",
        };
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var sourceClient = new ControlPlaneClient("/tmp/hypa-source-lease-test.sock");
        live.ControlSlot = new AttachControlSlot { Client = sourceClient };
        using (var doc = JsonDocument.Parse(
            """
            {
              "boot_id":"source-boot",
              "revision":2,
              "projection_revision":2
            }
            """))
        {
            live.LastSnapshot = doc.RootElement.Clone();
            live.LastSnapshotConnectionGeneration = 3;
        }

        var request = new CubesConnectRequest
        {
            Destination = new SidebarCubeItem
            {
                Id = "remote",
                Name = "remote",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
            SourceSurfaceActive = true,
            SourceEndpointId = "local",
            SourceInputLease = "src-input-lease",
            SourceClient = sourceClient,
            SourceControl = MouseTestGeom.ApplyPort(),
        };
        var sourceLease = AttachSession.BuildSourceLeaseForTests(live, request, outcome);
        Assert.Equal("source-boot", sourceLease?.BootId);
        Assert.Equal(3UL, sourceLease?.ConnectionGeneration);
        Assert.Equal("src-input-lease", sourceLease?.LeaseId);
        var targetLease = AttachSession.BuildTargetLeaseForTests(live, request.Destination, outcome);
        Assert.Equal("target-boot", targetLease.BootId);
        Assert.Equal(9UL, targetLease.ConnectionGeneration);
        Assert.Equal("dest-input", targetLease.LeaseId);
    }

    [Fact]
    public void PendingActivationAdmitsSurfaceWithoutPaintPath()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "client-shell-surface:9:on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease());
        live.PendingConnectOutcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            TargetConnectionGeneration = 7,
        };
        var admitted = AttachEndpointActivationPump.TryAdmitTerminalRender(
            live,
            "remote",
            7,
            Surface("remote-boot", 1),
            tty: null);
        Assert.True(admitted);
    }

    [Fact]
    public void RpcRejectSurfacesAsRejectedProgressNotDisconnect()
    {
        var registry = new FakeRegistry();
        var activation = AtPhase(new ActivationPhase.ActivatingTarget(
            "client-shell-surface:3:on",
            null,
            null,
            null,
            true,
            new EndpointActivationEvidence()));
        var progress = activation.ReceiveResponseForBoot(
            "remote",
            7,
            "remote-boot",
            "client-shell-surface:3:on",
            new EndpointSurfaceControlResult
            {
                Ok = false,
                HasErrorCode = true,
                ErrorMessage = "surface rejected",
                BootId = "remote-boot",
                RequestId = "client-shell-surface:3:on",
            },
            registry);
        Assert.IsType<SurfaceActivationProgress.Rejected>(progress);
        Assert.Empty(registry.Failures);
    }

    [Fact]
    public void WriteAcceptSendToReturnsBeforeDispatchCompletes()
    {
        var registry = new DeferredFakeRegistry();
        var outcome = registry.SendTo(
            "remote",
            new EndpointActivationMessage.SurfaceInterest("client-shell-surface:9:on", true));
        Assert.Equal(EndpointSendOutcome.Sent, outcome);
        Assert.Equal(0, registry.CompletedAsyncDispatches);
    }

    [Fact]
    public async Task Dest_sync_keeps_input_frozen_until_activated()
    {
        await using var peer = AcceptingPeer.Listen();
        var dest = peer.ConnectClient();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
            DestPaneId = "pane-a",
            DestInputLease = "dest-input",
            DestResizeLease = "dest-resize",
        };
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2) with { PaneId = "pane-a" });
        evidence.RecordSurface(Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" });
        var destLease = TargetLease() with { LeaseId = "dest-input" };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                evidence),
            SourceLease(),
            destLease);
        StoreBoundCapture(
            live,
            destLease,
            Surface("remote-boot", 2) with { FocusedPaneId = "pane-a" },
            SampleCapturedFrame("pane-a"));

        var syncSuccessor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(syncSuccessor);
        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation.Phase);
        Assert.False(live.PresentationFrozen);
        Assert.True(live.InputFrozen);
        Assert.True(AttachSession.BlocksPresentationInput(live));

        var forwarded = 0;
        using var gate = new SemaphoreSlim(1, 1);
        using var linked = new CancellationTokenSource();
        await AttachSession.SendPaneKeysUnderGateAsync(
            _ =>
            {
                forwarded++;
                return Task.CompletedTask;
            },
            gate,
            live,
            tty: null,
            linked,
            CancellationToken.None);
        Assert.Equal(0, forwarded);

        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                destLease,
                "9:7:remote-boot",
                true,
                new ActivationCompletion.Activated()),
            SourceLease(),
            destLease);
        var activated = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(activated);
        Assert.Null(live.PendingActivation);
        Assert.False(live.InputFrozen);
        Assert.False(live.PresentationFrozen);
        Assert.False(AttachSession.BlocksPresentationInput(live));

        await AttachSession.SendPaneKeysUnderGateAsync(
            _ =>
            {
                forwarded++;
                return Task.CompletedTask;
            },
            gate,
            live,
            tty: null,
            linked,
            CancellationToken.None);
        Assert.Equal(1, forwarded);
    }

    [Fact]
    public void CompleteErrorKeepsSourceClientAndPendingActivation()
    {
        var source = new ControlPlaneClient("/tmp/hypa-complete-error-source.sock");
        var dest = new ControlPlaneClient("/tmp/hypa-complete-error-dest.sock");
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        live.PendingConnectOutcome = MinimalConnectOutcome(dest);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease());
        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(successor);
        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.ActivatingTarget>(live.PendingActivation.Phase);
        Assert.False(source.IsDisposed);
        Assert.False(dest.IsDisposed);
        Assert.NotNull(live.PendingConnectOutcome);
        Assert.NotNull(live.StatusError);
    }

    [Fact]
    public async Task PresentationSyncEventIsAdmittedOnce()
    {
        var client = new ControlPlaneClient("/tmp/hypa-sync-once-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "remote";
        live.SourceTransportEnvelope.StampServerGeneration(7);
        live.ControlSlot = new AttachControlSlot { Client = client };
        var requestId = "client-shell-surface:9:presentation-sync";
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.SynchronizingPresentation(
                TargetLease(),
                requestId,
                null,
                new EndpointActivationEvidence(),
                new ActivationCompletion.Activated()),
            SourceLease(),
            TargetLease());
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "event":"{{AttachEndpointProtocol.PresentationSyncEvent}}",
              "params":{
                "request_id":"{{requestId}}",
                "lease_id":"lease",
                "boot_id":"remote-boot",
                "projection_revision":4,
                "surface_revision":4
              }
            }
            """);
        var raw = System.Text.Encoding.UTF8.GetByteCount(doc.RootElement.GetRawText());
        Assert.True(client.AdmitEvent(doc.RootElement, raw));
        var started = DateTime.UtcNow;
        await AttachEndpointActivationPump.RunTick(live, tty: null);
        Assert.Empty(client.DrainPendingAllEvents());
        Assert.True(live.PendingActivation.Phase is ActivationPhase.SynchronizingPresentation acked
            && acked.AcknowledgedRevision == 4);
        await AttachEndpointActivationPump.RunTick(live, tty: null);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        Assert.True(live.PendingActivation.Phase is ActivationPhase.SynchronizingPresentation again
            && again.AcknowledgedRevision == 4);
        Assert.NotNull(live.PendingActivation);
    }

    [Fact]
    public async Task HeartbeatDrainAdmitsDestinationPresentationSync()
    {
        var dest = new ControlPlaneClient("/tmp/hypa-heartbeat-sync-dest-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        var live = SyncLive(dest);
        var requestId = "client-shell-surface:9:presentation-sync";
        using var doc = SyncDocument(requestId, projectionRevision: 4);
        Assert.True(dest.AdmitEvent(doc.RootElement, SyncBytes(doc)));

        await AttachSession.DrainOverlayEventsAsync(dest, live, tty: null, CancellationToken.None);

        Assert.True(live.PendingActivation!.Phase is ActivationPhase.SynchronizingPresentation acked
            && acked.AcknowledgedRevision == 4);
        Assert.Empty(dest.DrainPendingEvents());
        await AttachSession.DrainOverlayEventsAsync(dest, live, tty: null, CancellationToken.None);
        Assert.True(live.PendingActivation.Phase is ActivationPhase.SynchronizingPresentation again
            && again.AcknowledgedRevision == 4);

        live.SetPaneSurface(Surface("remote-boot", 4), TargetLease());
        Assert.Null(AttachSession.CompleteEndpointActivation(live, tty: null));
        Assert.Null(live.PendingActivation);
        Assert.Null(live.StatusError);
        Assert.False(live.InputFrozen);
        Assert.False(dest.IsDisposed);
    }

    [Fact]
    public async Task HeartbeatDrainOnSourceDoesNotApplyDestinationSync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dest = new ControlPlaneClient("/tmp/hypa-heartbeat-sync-slot-" + suffix + ".sock");
        var source = new ControlPlaneClient("/tmp/hypa-heartbeat-sync-source-" + suffix + ".sock");
        var live = SyncLive(dest);
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourceClient = source,
            SourcePort = MouseTestGeom.ApplyPort(),
            ConnectedPlacementId = "local",
        };
        var requestId = "client-shell-surface:9:presentation-sync";
        using var doc = SyncDocument(requestId, projectionRevision: 4);
        Assert.True(source.AdmitEvent(doc.RootElement, SyncBytes(doc)));

        await AttachSession.DrainOverlayEventsAsync(source, live, tty: null, CancellationToken.None);

        Assert.True(live.PendingActivation!.Phase is ActivationPhase.SynchronizingPresentation sync
            && sync.AcknowledgedRevision is null);
        Assert.Empty(source.DrainPendingEvents());
        Assert.Null(live.StatusError);
        Assert.False(source.IsDisposed);
        Assert.False(dest.IsDisposed);
    }

    private static AttachLiveState SyncLive(ControlPlaneClient dest)
    {
        var requestId = "client-shell-surface:9:presentation-sync";
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "remote";
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.TransportEnvelope.StampServerGeneration(7);
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.SynchronizingPresentation(
                TargetLease(),
                requestId,
                null,
                new EndpointActivationEvidence(),
                new ActivationCompletion.Activated()),
            SourceLease(),
            TargetLease());
        return live;
    }

    private static JsonDocument SyncDocument(string requestId, ulong projectionRevision) =>
        JsonDocument.Parse(
            $$"""
            {
              "event":"{{AttachEndpointProtocol.PresentationSyncEvent}}",
              "params":{
                "request_id":"{{requestId}}",
                "lease_id":"lease",
                "boot_id":"remote-boot",
                "projection_revision":{{projectionRevision}},
                "surface_revision":{{projectionRevision}}
              }
            }
            """);

    private static int SyncBytes(JsonDocument doc) =>
        System.Text.Encoding.UTF8.GetByteCount(doc.RootElement.GetRawText());

    [Fact]
    public async Task RenderLoopAndPumpDeliverPresentationSyncOnce()
    {
        using var peer = AcceptingPeer.Listen();
        var dest = peer.ConnectClient();
        var source = new ControlPlaneClient("/tmp/hypa-render-sync-source.sock");
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 4));
        evidence.RecordSurface(Surface("remote-boot", 4));
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ConnectedPlacementId = "remote";
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.TransportEnvelope.StampServerGeneration(7);
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 4);
        live.SetEndpointStatus("remote", ClientEndpointStatus.Online);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
        };
        var lease = TargetLease() with { LeaseId = "dest-input" };
        var requestId = "client-shell-surface:9:presentation-sync";
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.SynchronizingPresentation(
                lease,
                requestId,
                null,
                evidence,
                new ActivationCompletion.Activated()),
            SourceLease(),
            lease);

        using var stop = new CancellationTokenSource();
        using var controlGate = new SemaphoreSlim(1, 1);
        using var tty = new UnixRawTerminal(new MemoryStream());
        var loop = new AttachSession().ReadRenderAsync(
            dest,
            dest,
            controlGate,
            new SnapshotAssembler(),
            tty,
            live,
            stop,
            stop.Token);
        await Task.Delay(100);
        Assert.True(await live.ActivationPumpGate.WaitAsync(TimeSpan.FromSeconds(2)));
        var pump = AttachEndpointActivationPump.RunTick(live, tty: null);
        var started = DateTime.UtcNow;
        try
        {
            using var doc = JsonDocument.Parse(
                $$"""
                {
                  "event":"{{AttachEndpointProtocol.PresentationSyncEvent}}",
                  "params":{
                    "request_id":"{{requestId}}",
                    "lease_id":"dest-input",
                    "boot_id":"remote-boot",
                    "projection_revision":4,
                    "surface_revision":4
                  }
                }
                """);
            var raw = System.Text.Encoding.UTF8.GetByteCount(doc.RootElement.GetRawText());
            Assert.True(dest.AdmitEvent(doc.RootElement, raw));

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (live.RenderLoopReads < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            while (live.PendingActivation?.Phase is not ActivationPhase.AwaitingPresentationEffects
                && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.NotEqual(
                live.SourceTransportEnvelope.Generation,
                live.PendingActivation!.Target.ConnectionGeneration);
            Assert.Equal(7UL, live.TransportEnvelope.Generation);
            Assert.Equal(1, live.RenderLoopReads);
            Assert.NotNull(live.PendingActivation);
            Assert.IsType<ActivationPhase.AwaitingPresentationEffects>(live.PendingActivation.Phase);
            Assert.Null(live.StatusError);
            Assert.True(live.InputFrozen);
            Assert.True(live.PresentationFrozen);
            Assert.False(source.IsDisposed);
            Assert.False(dest.IsDisposed);
            Assert.Empty(dest.DrainPendingAllEvents());
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            live.ActivationPumpGate.Release();
            await pump.WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.AwaitingPresentationEffects>(live.PendingActivation.Phase);
        Assert.False(source.IsDisposed);
    }

    [Fact]
    public async Task DestinationRenderDuringPendingActivationPaintsPaneText()
    {
        using var peer = AcceptingPeer.Listen();
        var dest = peer.ConnectClient();
        var surface = Surface("remote-boot", 4) with { FocusedPaneId = "pane-a" };
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSurface(surface);
        var lease = TargetLease();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = false;
        live.ConnectedPlacementId = "remote";
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.TransportEnvelope.StampServerGeneration(7);
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.SynchronizingPresentation(
                lease,
                "client-shell-surface:9:presentation-sync",
                null,
                evidence,
                new ActivationCompletion.Activated()),
            SourceLease(),
            lease);
        live.SetPaneSurface(surface, lease);

        await DriveCellsAdmit(live, dest, "hypa-remote");

        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation.Phase);
        Assert.True(live.TryGetPaneFrame("pane-a", out var frame));
        Assert.Contains("hypa-remote", RowText(frame!), StringComparison.Ordinal);
        Assert.True(live.TryGetActivationCaptureBinding("remote", out var binding));
        Assert.Equal("remote", binding!.EndpointId);
        Assert.Equal(7UL, binding.Generation);
        Assert.Equal("remote-boot", binding.BootId);
        Assert.False(live.HasActivationCapturedFrame("local"));
    }

    [Fact]
    public async Task SourceRenderDuringPendingActivationBindsSourceLease()
    {
        using var peer = AcceptingPeer.Listen();
        var source = peer.ConnectClient();
        var surface = Surface("local-boot", 4) with { FocusedPaneId = "pane-a" };
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSurface(surface);
        var lease = SourceLease();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = false;
        live.ConnectedPlacementId = "local";
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.TransportEnvelope.StampServerGeneration(7);
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.RestoringSource(
                "client-shell-surface:9:rollback-source-on",
                null,
                evidence),
            lease,
            TargetLease());
        live.SetPaneSurface(surface, lease);

        await DriveCellsAdmit(live, source, "source-shell");

        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.RestoringSource>(live.PendingActivation.Phase);
        Assert.True(live.TryGetPaneFrame("pane-a", out var frame));
        Assert.Contains("source-shell", RowText(frame!), StringComparison.Ordinal);
        Assert.True(live.TryGetActivationCaptureBinding("local", out var binding));
        Assert.Equal("local", binding!.EndpointId);
        Assert.Equal(1UL, binding.Generation);
        Assert.Equal("local-boot", binding.BootId);
        Assert.False(live.HasActivationCapturedFrame("remote"));
    }

    private static async Task DriveCellsAdmit(AttachLiveState live, ControlPlaneClient reader, string marker)
    {
        using var stop = new CancellationTokenSource();
        using var controlGate = new SemaphoreSlim(1, 1);
        using var tty = new UnixRawTerminal(new MemoryStream());
        var loop = new AttachSession().ReadRenderAsync(
            reader,
            reader,
            controlGate,
            new SnapshotAssembler(),
            tty,
            live,
            stop,
            stop.Token);
        await Task.Delay(100);
        Assert.True(await live.ActivationPumpGate.WaitAsync(TimeSpan.FromSeconds(2)));
        var pump = AttachEndpointActivationPump.RunTick(live, tty: null);
        try
        {
            using var doc = JsonDocument.Parse(
                $$"""
                {
                  "params": {
                    "type": "terminal.render",
                    "payload": {
                      "pane_id": "pane-a",
                      "kind": "cells",
                      "full": true,
                      "reanchor": true,
                      "grid_cols": 80,
                      "grid_rows": 1,
                      "generation": 3,
                      "rows": [{"i": 0, "t": "{{marker}}"}]
                    }
                  }
                }
                """);
            var raw = System.Text.Encoding.UTF8.GetByteCount(doc.RootElement.GetRawText());
            Assert.True(reader.AdmitEvent(doc.RootElement, raw));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (live.RenderLoopReads >= 1
                    && live.TryGetPaneFrame("pane-a", out var painted)
                    && painted is not null
                    && RowText(painted).Contains(marker, StringComparison.Ordinal))
                    return;
                await Task.Delay(10);
            }

            Assert.True(live.RenderLoopReads >= 1, "render loop did not read the cells event");
            Assert.True(live.TryGetPaneFrame("pane-a", out var frame));
            Assert.Contains(marker, RowText(frame!));
        }
        finally
        {
            stop.Cancel();
            live.ActivationPumpGate.Release();
            await pump.WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static string RowText(AssembledSnapshot frame) =>
        string.Concat(frame.Cells[0].Select(cell => cell.Text));

    [Fact]
    public void EffectsFenceLeavesPendingActivationInPlace()
    {
        using var peer = AcceptingPeer.Listen();
        var source = new ControlPlaneClient("/tmp/hypa-effects-pending-source.sock");
        var dest = peer.ConnectClient();
        var evidence = new EndpointActivationEvidence();
        evidence.RecordSnapshot("remote", 7, Snapshot("remote-boot", 2));
        evidence.RecordSurface(Surface("remote-boot", 2));
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputFrozen = true;
        live.PresentationFrozen = true;
        live.ConnectedPlacementId = "remote";
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        SeedTargetLiveConnect(live, "remote-boot", 2);
        live.SetEndpointStatus("remote", ClientEndpointStatus.Online);
        live.PendingConnectOutcome = live.PendingConnectOutcome! with
        {
            DestClient = dest,
            DestSubscribeId = "sub_dest",
        };
        var lease = TargetLease() with { LeaseId = "dest-input" };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.SynchronizingPresentation(
                lease,
                "client-shell-surface:9:presentation-sync",
                2,
                evidence,
                new ActivationCompletion.Activated()),
            SourceLease(),
            lease);
        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(successor);
        Assert.Null(live.StatusError);
        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.AwaitingPresentationEffects>(live.PendingActivation.Phase);
        Assert.True(live.InputFrozen);
        Assert.True(live.PresentationFrozen);
        Assert.False(source.IsDisposed);
        Assert.False(dest.IsDisposed);
    }

    [Fact]
    public void RestoredSourceSuccessorIsOneFollowUp()
    {
        var successorIntent = new EndpointActivationIntent { EndpointId = "other-remote" };
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var destClient = new ControlPlaneClient("/tmp/hypa-one-follow-up.sock");
        live.PendingConnectControl = MouseTestGeom.ApplyPort();
        live.PendingConnectOutcome = MinimalConnectOutcome(destClient);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                SourceLease(),
                "9:1:local-boot",
                true,
                new ActivationCompletion.RestoredSource(
                    "endpoint handoff was rolled back",
                    successorIntent)),
            SourceLease(),
            TargetLease());
        var first = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.NotNull(first);
        Assert.Equal("other-remote", first.EndpointId);
        Assert.Null(live.PendingActivation);
        Assert.Null(AttachSession.CompleteEndpointActivation(live, tty: null));
        Assert.Null(live.PendingActivation);
    }

    private static PendingEndpointActivation AtPhase(ActivationPhase phase) =>
        PendingEndpointActivation.ForTests(phase, SourceLease(), TargetLease());

    private static EndpointActivationBeginRequest SampleRequest(
        bool sourceAvailable,
        ulong epoch = 9) =>
        new()
        {
            ClientId = "attach-client",
            Geometry = Geometry(),
            Target = new EndpointActivationIntent { EndpointId = "remote" },
            Source = sourceAvailable ? SourceLease() : null,
            SourceAvailable = sourceAvailable,
            HostFocused = true,
            Epoch = epoch,
            TargetLease = TargetLease(),
        };

    private static EndpointActivationLease SourceLease() =>
        new()
        {
            EndpointId = "local",
            ConnectionGeneration = 1,
            BootId = "local-boot",
            MinimumProjectionRevision = 0,
            ClientId = "attach-client",
            LeaseId = "src-input-lease",
        };

    private static EndpointActivationLease TargetLease() =>
        new()
        {
            EndpointId = "remote",
            ConnectionGeneration = 7,
            BootId = "remote-boot",
            MinimumProjectionRevision = 0,
            ClientId = "attach-client",
            LeaseId = string.Empty,
        };

    private static AttachGeometry Geometry() =>
        new()
        {
            Columns = 80,
            Rows = 24,
            CellWidthPx = 8,
            CellHeightPx = 16,
            GeometryRevision = 1,
        };

    private static AttachSnapshotEvidence Snapshot(string bootId, ulong revision) =>
        new() { BootId = bootId, Revision = revision };

    private static JsonElement DestinationWorkspaceSnapshot()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "boot_id":"stale-boot",
              "revision":1,
              "projection_revision":1,
              "focused_workspace_id":"ws-remote",
              "workspaces":[{"workspace_id":"ws-remote","label":"remote","cwd":"/work"}],
              "tabs":[{"tab_id":"tab-main","workspace_id":"ws-remote","label":"main","focused_pane_id":"pane-a"}],
              "panes":[{"pane_id":"pane-a"}]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static string? FirstId(JsonElement snapshot, string arrayName, string idName)
    {
        if (!snapshot.TryGetProperty(arrayName, out var items) || items.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty(idName, out var id) && id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }

        return null;
    }

    private static JsonElement LocalWorkspaceSnapshot()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "focused_workspace_id":"ws-local",
              "workspaces":[
                {"workspace_id":"ws-local","label":"mac","cwd":"/tmp"}
              ]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static AssembledSnapshot SampleCapturedFrame(string paneId) =>
        MouseTestGeom.Frame("x", paneId, 80);

    private static void StoreBoundCapture(
        AttachLiveState live,
        EndpointActivationLease lease,
        AttachSurfaceEvidence surface,
        AssembledSnapshot frame) =>
        live.StoreActivationCapturedFrame(new ActivationCapturedFrame(
            new ActivationCaptureBinding(
                lease.EndpointId,
                lease.ConnectionGeneration,
                lease.BootId,
                surface.ProjectionRevision,
                surface.SurfaceRevision),
            frame));

    private static CubesConnectRetargetOutcome MinimalConnectOutcome(ControlPlaneClient destClient) =>
        new()
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            DestClient = destClient,
            TargetConnectionGeneration = 7,
            DestInputLease = "dest-input",
        };

    private static bool SpinWaitUntilDisposed(ControlPlaneClient client)
    {
        var deadline = Environment.TickCount64 + 2_000;
        while (Environment.TickCount64 < deadline)
        {
            if (client.IsDisposed)
                return true;
            Thread.Sleep(10);
        }

        return client.IsDisposed;
    }

    private static void SeedTargetLiveConnect(
        AttachLiveState live,
        string bootId,
        ulong revision,
        ulong generation = 7)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "boot_id":"{{bootId}}",
              "revision":{{revision}},
              "projection_revision":{{revision}}
            }
            """);
        live.PendingConnectCube = new SidebarCubeItem
        {
            Id = "remote",
            Name = "remote",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        live.PendingConnectOutcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            TargetConnectionGeneration = generation,
            DestInputLease = "dest-input",
        };
        live.PendingTargetSessionSnapshot = doc.RootElement.Clone();
        live.PendingTargetSessionSnapshotGeneration = generation;
    }

    private static void SeedSourceLiveSnapshot(AttachLiveState live, string bootId, ulong revision)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "boot_id":"{{bootId}}",
              "revision":{{revision}},
              "projection_revision":{{revision}}
            }
            """);
        live.ConnectedPlacementId = "local";
        live.LastSnapshot = doc.RootElement.Clone();
        live.LastSnapshotConnectionGeneration = 1;
        live.SourceTransportEnvelope.StampServerGeneration(1);
    }

    private static JsonElement SnapshotRenderPayload(string paneId, long generation)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "pane_id": "{{paneId}}",
              "kind": "snapshot",
              "row_start": 0,
              "row_end": 1,
              "complete": true,
              "grid_cols": 80,
              "grid_rows": 1,
              "generation": {{generation}},
              "provider": "basic",
              "active_screen": "main",
              "snapshot": {
                "provider": "basic",
                "active_screen": "main"
              }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static AttachSurfaceEvidence Surface(string bootId, ulong revision) =>
        new()
        {
            BootId = bootId,
            ProjectionRevision = revision,
            SurfaceRevision = revision,
            Columns = 80,
            Rows = 24,
        };

    private static EndpointSurfaceControlResult SurfaceOff(string requestId) =>
        new()
        {
            Ok = true,
            RequestId = requestId,
            BootId = "local-boot",
            ConnectionGeneration = 1,
        };

    private sealed class AcceptingPeer : IDisposable, IAsyncDisposable
    {
        private readonly Socket _listener;
        private NetworkStream? _stream;
        private Task? _reader;

        private AcceptingPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        internal string Path { get; }

        internal static AcceptingPeer Listen()
        {
            var path = "/tmp/hypa-accept-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            if (File.Exists(path))
                File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(1);
                return new AcceptingPeer(listener, path);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        internal ControlPlaneClient ConnectClient()
        {
            var client = new ControlPlaneClient(Path, connectTimeout: TimeSpan.FromSeconds(2));
            var accept = AcceptAsync();
            client.ConnectAsync().GetAwaiter().GetResult();
            accept.GetAwaiter().GetResult();
            return client;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _listener.Dispose();
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task AcceptAsync()
        {
            var accepted = await _listener.AcceptAsync();
            _stream = new NetworkStream(accepted, ownsSocket: true);
            _reader = ReadAsync();
        }

        private async Task ReadAsync()
        {
            var stream = _stream;
            if (stream is null)
                return;
            var buffer = new byte[4096];
            try
            {
                while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
                {
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private class FakeRegistry : IEndpointRegistryPort
    {
        public bool FailAfterWrite { get; init; }
        public List<(string Endpoint, EndpointActivationMessage Message)> Sent { get; } = [];
        public List<(string Endpoint, string Error)> Failures { get; } = [];
        public Dictionary<string, bool> SurfaceState { get; } = new(StringComparer.Ordinal)
        {
            ["local"] = true,
            ["remote"] = false,
        };

        public IEnumerable<(string Endpoint, EndpointActivationMessage Message)> TargetSent =>
            Sent.Where(s => s.Endpoint == "remote");

        public string ActiveId { get; set; } = "local";

        public bool SurfaceActive(string endpointId) =>
            SurfaceState.TryGetValue(endpointId, out var active) && active;

        public bool SupportsSurfaceInterest(string endpointId) => true;

        public virtual EndpointSendOutcome SendTo(string endpointId, EndpointActivationMessage message)
        {
            Sent.Add((endpointId, message));
            if (message is EndpointActivationMessage.SurfaceInterest(_, var active))
                SurfaceState[endpointId] = active;
            return FailAfterWrite ? EndpointSendOutcome.NotSent : EndpointSendOutcome.Sent;
        }

        public void Fail(string endpointId, string error) => Failures.Add((endpointId, error));

        public void SetSurfaceActive(string endpointId, bool active) => SurfaceState[endpointId] = active;

        public List<string> ActiveSets { get; } = [];

        public bool SetActive(string endpointId)
        {
            ActiveSets.Add(endpointId);
            ActiveId = endpointId;
            return true;
        }

        public void FreezeInput() { }

        public void UnfreezeInput() { }
    }

    private sealed class FenceFailRegistry : FakeRegistry
    {
        public override EndpointSendOutcome SendTo(string endpointId, EndpointActivationMessage message) =>
            message is EndpointActivationMessage.PresentationEffectsFence
                ? EndpointSendOutcome.NotSent
                : base.SendTo(endpointId, message);
    }

    private sealed class DeferredFakeRegistry : FakeRegistry
    {
        public int CompletedAsyncDispatches { get; private set; }

        public new EndpointSendOutcome SendTo(string endpointId, EndpointActivationMessage message)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(50).ConfigureAwait(false);
                CompletedAsyncDispatches++;
            });
            return EndpointSendOutcome.Sent;
        }
    }
}
