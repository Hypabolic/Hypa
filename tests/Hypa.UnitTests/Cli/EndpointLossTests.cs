using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class EndpointLossTests
{
    private const string SourceId = "plc_local";
    private const string DestId = "plc_peer";

    [SkippableFact]
    public async Task SourceLossDuringReleaseKeepsDestinationAndStartsTarget()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var sourcePeer = RecordingPeer.Listen();
        await using var destPeer = RecordingPeer.Listen();
        await using var source = Client(sourcePeer);
        await using var dest = Client(destPeer);
        await ConnectAsync(sourcePeer, source, destPeer, dest);

        var live = Live(source, dest, new ActivationPhase.ReleasingSource("client-shell-surface:9:off"));
        var sourceWait = ArmLane(live, SourceId, source, "source-lane");
        var destWait = ArmLane(live, DestId, dest, "dest-lane");
        var sourceClosed = source.WatchDisconnectAsync();
        using var destStay = new CancellationTokenSource();
        var destWatch = dest.WatchDisconnectAsync(destStay.Token);

        AttachSession.RecordEndpointTransportFailure(live, SourceId, 1, "source transport fault");

        Assert.True(source.IsDisposed);
        Assert.False(dest.IsDisposed);
        Assert.IsType<ActivationPhase.ReleasingSource>(live.PendingActivation!.Phase);

        AttachSession.DrainQueuedEndpointFailures(live, tty: null);

        Assert.False(dest.IsDisposed);
        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.ActivatingTarget>(live.PendingActivation.Phase);
        Assert.Same(dest, live.PendingConnectOutcome!.DestClient);
        Assert.Equal(DestId, live.PendingConnectCube!.Id);
        Assert.NotNull(live.PendingConnectControl);
        Assert.True(sourceWait.Task.IsCompleted);
        var sourceResult = await sourceWait.Task;
        var sourceError = Assert.IsType<ControlPlaneException>(sourceResult.Error);
        Assert.Equal(AttachEndpointCommands.CancelledCode, sourceError.ErrorCode);
        Assert.False(destWait.Task.IsCompleted);

        var closed = await Assert.ThrowsAsync<IOException>(() => sourceClosed.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("Connection closed", closed.Message);
        Assert.True(
            SpinWait.SpinUntil(() => destPeer.Count >= 3, TimeSpan.FromSeconds(3)),
            string.Join(",", destPeer.Methods()));
        Assert.Equal(
            [AttachEndpointProtocol.Resize, AttachEndpointProtocol.SurfaceInterest, AttachEndpointProtocol.Focus],
            destPeer.Methods().Take(3).ToArray());
        Assert.Contains("\"active\":true", destPeer.Line(1), StringComparison.Ordinal);
        Assert.Contains(":on", destPeer.Line(1), StringComparison.Ordinal);
        Assert.Contains(":baseline", destPeer.Line(2), StringComparison.Ordinal);
        Assert.Equal(0, sourcePeer.Count);
        Assert.Equal(0, sourcePeer.ByteCount);
        Assert.False(dest.IsDisposed);
        Assert.False(destWatch.IsCompleted);

        destStay.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => destWatch);
    }

    [SkippableFact]
    public async Task SourceLossDuringActivationKeepsDestinationOpen()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var sourcePeer = RecordingPeer.Listen();
        await using var destPeer = RecordingPeer.Listen();
        await using var source = Client(sourcePeer);
        await using var dest = Client(destPeer);
        await ConnectAsync(sourcePeer, source, destPeer, dest);

        var live = Live(
            source,
            dest,
            new ActivationPhase.ActivatingTarget(
                "client-shell-surface:9:on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()));
        var sourceClosed = source.WatchDisconnectAsync();
        using var destStay = new CancellationTokenSource();
        var destWatch = dest.WatchDisconnectAsync(destStay.Token);

        AttachSession.RecordEndpointTransportFailure(live, SourceId, 1, "source transport fault");
        Assert.True(source.IsDisposed);
        Assert.False(dest.IsDisposed);
        AttachSession.DrainQueuedEndpointFailures(live, tty: null);

        Assert.NotNull(live.PendingActivation);
        Assert.IsType<ActivationPhase.ActivatingTarget>(live.PendingActivation.Phase);
        Assert.False(dest.IsDisposed);
        Assert.True(source.IsDisposed);
        Assert.Same(dest, live.PendingConnectOutcome!.DestClient);
        await Assert.ThrowsAsync<IOException>(() => sourceClosed.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(400);
        Assert.Equal(0, destPeer.Count);
        Assert.Equal(0, sourcePeer.ByteCount);
        Assert.False(destWatch.IsCompleted);
        Assert.False(dest.IsDisposed);

        destStay.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => destWatch);
    }

    [SkippableFact]
    public async Task SendToMissingDestinationIsNotConnected()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var sourcePeer = RecordingPeer.Listen();
        await using var source = Client(sourcePeer);
        var accept = sourcePeer.AcceptAsync();
        await source.ConnectAsync();
        await accept;

        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.ConnectedPlacementId = SourceId;
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.SourceBackup = Backup(source, port);
        var registry = new AttachEndpointRegistryPort(
            live,
            new AttachEndpointRpcClient(source),
            null,
            Lease(SourceId, 1),
            Lease(DestId, 2),
            _ => { },
            (_, _, _) => { });

        var sent = registry.SendTo(
            DestId,
            new EndpointActivationMessage.Resize(Geometry(), "client-shell-resize:9:1"));
        Assert.Equal(EndpointSendOutcome.NotConnected, sent);
        await Task.Delay(400);
        Assert.Equal(0, sourcePeer.Count);
        Assert.Equal(0, sourcePeer.ByteCount);

        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ReleasingSource("client-shell-surface:9:off"),
            Lease(SourceId, 1),
            Lease(DestId, 2));
        AttachSession.HandleEndpointDisconnect(live, SourceId, 1, "source transport fault", tty: null);
        Assert.False(source.IsDisposed);
        AttachSession.RecordEndpointTransportFailure(live, SourceId, 1, "source transport fault");
        AttachSession.DrainQueuedEndpointFailures(live, tty: null);
        await Task.Delay(400);
        Assert.Null(live.PendingActivation);
        Assert.Equal(0, sourcePeer.Count);
        Assert.Equal(0, sourcePeer.ByteCount);
        Assert.True(source.IsDisposed);
    }

    [SkippableFact]
    public async Task DestinationFaultKeepsSourceClient()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var sourcePeer = RecordingPeer.Listen();
        await using var destPeer = RecordingPeer.Listen();
        await using var source = Client(sourcePeer);
        await using var dest = Client(destPeer);
        await ConnectAsync(sourcePeer, source, destPeer, dest);

        var live = Live(
            source,
            dest,
            new ActivationPhase.ActivatingTarget(
                "client-shell-surface:9:on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()));
        using var sourceStay = new CancellationTokenSource();
        var sourceWatch = source.WatchDisconnectAsync(sourceStay.Token);
        var destClosed = dest.WatchDisconnectAsync();

        AttachSession.HandleEndpointDisconnect(live, DestId, 2, "destination transport fault", tty: null);
        Assert.False(dest.IsDisposed);
        Assert.False(source.IsDisposed);

        AttachSession.RecordEndpointTransportFailure(live, DestId, 2, "destination transport fault");
        Assert.True(dest.IsDisposed);
        Assert.False(source.IsDisposed);
        AttachSession.DrainQueuedEndpointFailures(live, tty: null);

        Assert.True(dest.IsDisposed);
        Assert.False(source.IsDisposed);
        Assert.Same(source, live.SourceBackup!.SourceClient);
        Assert.Same(source, live.ControlSlot!.Client);
        var closed = await Assert.ThrowsAsync<IOException>(() => destClosed.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("Connection closed", closed.Message);
        Assert.True(
            SpinWait.SpinUntil(() => sourcePeer.Count >= 3, TimeSpan.FromSeconds(3)),
            string.Join(",", sourcePeer.Methods()));
        Assert.False(source.IsDisposed);
        Assert.False(sourceWatch.IsCompleted);

        sourceStay.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sourceWatch);
    }

    [SkippableFact]
    public async Task DestinationLossRestoresTheOpenSource()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var sourcePeer = RecordingPeer.Listen();
        await using var destPeer = RecordingPeer.Listen();
        await using var source = Client(sourcePeer);
        await using var dest = Client(destPeer);
        await ConnectAsync(sourcePeer, source, destPeer, dest);

        var live = PostCommitLive(source, dest, sourceAvailable: false);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ReleasingSource("client-shell-surface:9:off"),
            Lease("local", 1),
            Lease(DestId, 2),
            sourceAvailable: false);
        var registry = new AttachEndpointRegistryPort(
            live,
            new AttachEndpointRpcClient(source),
            new AttachEndpointRpcClient(dest),
            Lease("local", 1),
            Lease(DestId, 2),
            _ => { },
            (_, _, _) => { });
        Assert.True(live.PendingActivation.StartTarget(registry).IsOk);
        Assert.False(live.PendingActivation.SourceAvailable);
        Assert.Same(dest, live.ControlSlot!.Client);
        Assert.Same(source, live.SourceBackup!.SourceClient);
        Assert.True(
            SpinWait.SpinUntil(() => destPeer.Count >= 3, TimeSpan.FromSeconds(3)),
            string.Join(",", destPeer.Methods()));
        Assert.Equal(
            [AttachEndpointProtocol.Resize, AttachEndpointProtocol.SurfaceInterest, AttachEndpointProtocol.Focus],
            destPeer.Methods().Take(3).ToArray());
        Assert.Contains("client-shell-surface:9:on", destPeer.Line(1), StringComparison.Ordinal);
        Assert.Contains("\"active\":true", destPeer.Line(1), StringComparison.Ordinal);
        Assert.Contains(":baseline", destPeer.Line(2), StringComparison.Ordinal);
        var destFrames = destPeer.Count;

        await using var keys = new AttachInputSender(
            dest,
            () => (live.PaneId, live.InputLease),
            blocksForward: () => AttachSession.BlocksPaneKeys(live));
        live.InputSender = keys;
        using var sourceStay = new CancellationTokenSource();
        var sourceWatch = source.WatchDisconnectAsync(sourceStay.Token);

        AttachSession.HandleEndpointDisconnect(live, DestId, 2, "destination transport fault", tty: null);

        Assert.False(source.IsDisposed);
        Assert.False(dest.IsDisposed);
        Assert.NotNull(live.PendingActivation);
        var restoring = Assert.IsType<ActivationPhase.RestoringSource>(live.PendingActivation.Phase);
        Assert.Equal("client-shell-surface:9:rollback-source-on", restoring.RequestId);
        Assert.False(live.PendingActivation.SourceAvailable);
        Assert.Same(dest, live.ControlSlot.Client);
        Assert.Same(source, live.SourceBackup.SourceClient);
        Assert.DoesNotContain(
            "the previous endpoint is no longer connected",
            live.StatusError ?? string.Empty,
            StringComparison.Ordinal);
        Assert.True(
            SpinWait.SpinUntil(() => sourcePeer.Count >= 3, TimeSpan.FromSeconds(3)),
            string.Join(",", sourcePeer.Methods()));
        Assert.Equal(
            [AttachEndpointProtocol.Resize, AttachEndpointProtocol.SurfaceInterest, AttachEndpointProtocol.Focus],
            sourcePeer.Methods().Take(3).ToArray());
        Assert.Contains("rollback-source-on", sourcePeer.Line(1), StringComparison.Ordinal);
        Assert.Contains("\"active\":true", sourcePeer.Line(1), StringComparison.Ordinal);
        Assert.Contains(":baseline", sourcePeer.Line(2), StringComparison.Ordinal);
        Assert.Equal(destFrames, destPeer.Count);
        Assert.Equal(3, destFrames);

        AttachSession.RecordEndpointTransportFailure(live, DestId, 2, "destination transport fault");
        Assert.True(dest.IsDisposed);
        Assert.False(source.IsDisposed);
        Assert.False(sourceWatch.IsCompleted);
        Assert.Same(source, live.SourceBackup!.SourceClient);

        RecordCoherent(restoring.Evidence);
        using (var snap = JsonDocument.Parse(
            """{"boot_id":"local-boot","revision":4,"projection_revision":4}"""))
        {
            live.LastSnapshot = snap.RootElement.Clone();
        }

        live.LastSnapshotConnectionGeneration = 1;
        AttachSession.OnEndpointResponse(
            live,
            "local",
            1,
            "local-boot",
            restoring.RequestId,
            new EndpointSurfaceControlResult
            {
                Ok = true,
                ProjectionRevision = 4,
                BootId = "local-boot",
            },
            tty: null);

        var syncing = Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation!.Phase);
        Assert.IsType<ActivationCompletion.RestoredSource>(syncing.Completion);
        RecordCoherent(syncing.Evidence);
        AttachSession.OnEndpointResponse(
            live,
            "local",
            1,
            "local-boot",
            syncing.RequestId,
            new EndpointSurfaceControlResult
            {
                Ok = true,
                ProjectionRevision = 4,
                BootId = "local-boot",
            },
            tty: null);

        if (live.PendingActivation?.Phase is ActivationPhase.AwaitingPresentationEffects effects)
        {
            AttachSession.OnPresentationEffectsReady(live, "local", 1, effects.Token, tty: null);
        }

        Assert.Null(live.PendingActivation);
        Assert.False(live.InputFrozen);
        Assert.Same(source, live.ControlSlot!.Client);
        Assert.DoesNotContain(
            "the previous endpoint is no longer connected",
            live.StatusError ?? string.Empty,
            StringComparison.Ordinal);
        Assert.True(keys.TryEnqueue([0x61]));
        Assert.True(
            SpinWait.SpinUntil(
                () => sourcePeer.Methods().Contains(ProtocolMethods.PaneSendKeys),
                TimeSpan.FromSeconds(3)),
            string.Join(",", sourcePeer.Methods()));
        Assert.DoesNotContain(ProtocolMethods.PaneSendKeys, destPeer.Methods());
        Assert.Equal(destFrames, destPeer.Count);
        Assert.False(source.IsDisposed);
        Assert.False(sourceWatch.IsCompleted);

        sourceStay.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sourceWatch);
    }

    [SkippableFact]
    public async Task DisposedSourceRestoreReportsUnsafeRestore()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var sourcePeer = RecordingPeer.Listen();
        await using var destPeer = RecordingPeer.Listen();
        await using var source = Client(sourcePeer);
        await using var dest = Client(destPeer);
        await ConnectAsync(sourcePeer, source, destPeer, dest);
        await source.DisposeAsync();

        var live = PostCommitLive(source, dest, sourceAvailable: false);
        AttachSession.HandleEndpointDisconnect(live, DestId, 2, "destination transport fault", tty: null);

        Assert.Null(live.PendingActivation);
        Assert.NotNull(live.SourceBackup);
        Assert.Same(source, live.SourceBackup.SourceClient);
        Assert.True(source.IsDisposed);
        Assert.False(dest.IsDisposed);
        Assert.Equal(
            "endpoint connection was lost while activating destination transport fault; source endpoint could not be restored safely: endpoint resize could not be sent",
            live.StatusError);
        await Task.Delay(200);
        Assert.Equal(0, sourcePeer.Count);
        Assert.Equal(0, sourcePeer.ByteCount);
        Assert.Equal(0, destPeer.Count);
        Assert.Equal(0, destPeer.ByteCount);
    }

    [SkippableFact]
    public async Task StaleGenerationDoesNotDisposeSurvivingClient()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var sourcePeer = RecordingPeer.Listen();
        await using var destPeer = RecordingPeer.Listen();
        await using var source = Client(sourcePeer);
        await using var dest = Client(destPeer);
        await ConnectAsync(sourcePeer, source, destPeer, dest);

        var live = Live(source, dest, new ActivationPhase.ReleasingSource("client-shell-surface:9:off"));
        live.PendingActivation = null;
        live.SourceTransportEnvelope.StampServerGeneration(5);
        var sourceWait = ArmLane(live, SourceId, source, "source-lane");
        using var stay = new CancellationTokenSource();
        var sourceWatch = source.WatchDisconnectAsync(stay.Token);
        var destWatch = dest.WatchDisconnectAsync(stay.Token);

        AttachSession.RecordEndpointTransportFailure(live, SourceId, 0, "unspecified generation");
        AttachSession.RecordEndpointTransportFailure(live, SourceId, 4, "stale source fault");
        AttachSession.DrainQueuedEndpointFailures(live, tty: null);

        await Task.Delay(200);
        Assert.Equal(0, live.EndpointDisconnectCount);
        Assert.Empty(live.EndpointFailures);
        Assert.False(source.IsDisposed);
        Assert.False(dest.IsDisposed);
        Assert.False(sourceWait.Task.IsCompleted);
        Assert.False(sourceWatch.IsCompleted);
        Assert.False(destWatch.IsCompleted);

        stay.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sourceWatch);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => destWatch);

        var sourceClosed = source.WatchDisconnectAsync();
        AttachSession.RecordEndpointTransportFailure(live, SourceId, 5, "current source fault");
        Assert.True(source.IsDisposed);
        Assert.False(dest.IsDisposed);
        AttachSession.DrainQueuedEndpointFailures(live, tty: null);

        Assert.Equal(1, live.EndpointDisconnectCount);
        Assert.True(source.IsDisposed);
        Assert.False(dest.IsDisposed);
        Assert.True(sourceWait.Task.IsCompleted);
        var closed = await Assert.ThrowsAsync<IOException>(() => sourceClosed.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("Connection closed", closed.Message);
        Assert.False(dest.IsDisposed);
    }

    [Fact]
    public void RetireOneEndpointKeepsTheOtherCapturedFrame()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.ConnectedPlacementId = SourceId;
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = SourceId,
                Name = "Local",
                Kind = SidebarCubeKind.Local,
                Reachability = SidebarCubeReachability.Local,
            },
            new SidebarCubeItem
            {
                Id = DestId,
                Name = "Peer",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        var sourceFrame = MouseTestGeom.Frame("source", "p1", 80);
        var destFrame = MouseTestGeom.Frame("dest", "p2", 80);
        live.StoreActivationCapturedFrame(new ActivationCapturedFrame(
            new ActivationCaptureBinding(SourceId, 1, "src-boot", 1, 1),
            sourceFrame));
        live.StoreActivationCapturedFrame(new ActivationCapturedFrame(
            new ActivationCaptureBinding(DestId, 2, "dest-boot", 1, 1),
            destFrame));
        live.HoldUnboundActivationRender(SourceId, 1, sourceFrame);
        live.HoldUnboundActivationRender(DestId, 2, destFrame);

        AttachSession.HandleEndpointDisconnect(live, SourceId, 1, "source transport fault", tty: null);

        Assert.False(live.HasActivationCapturedFrame(SourceId));
        Assert.True(live.HasActivationCapturedFrame(DestId));
        Assert.False(live.TryPeekUnboundActivationRender(SourceId, 1, out _));
        Assert.True(live.TryPeekUnboundActivationRender(DestId, 2, out var kept));
        Assert.Same(destFrame, kept);
    }

    private static ControlPlaneClient Client(RecordingPeer peer) =>
        new(peer.Path, connectTimeout: TimeSpan.FromSeconds(2), callTimeout: TimeSpan.FromSeconds(5));

    private static async Task ConnectAsync(
        RecordingPeer sourcePeer,
        ControlPlaneClient source,
        RecordingPeer destPeer,
        ControlPlaneClient dest)
    {
        var sourceAccept = sourcePeer.AcceptAsync();
        var destAccept = destPeer.AcceptAsync();
        await source.ConnectAsync();
        await dest.ConnectAsync();
        await sourceAccept;
        await destAccept;
    }

    private static AttachLiveState Live(ControlPlaneClient source, ControlPlaneClient dest, ActivationPhase phase)
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.ConnectedPlacementId = SourceId;
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.SourceBackup = Backup(source, port);
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = SourceId,
                Name = "Local",
                Kind = SidebarCubeKind.Local,
                Reachability = SidebarCubeReachability.Local,
            },
            new SidebarCubeItem
            {
                Id = DestId,
                Name = "Peer",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        live.PendingConnectCube = live.Cubes[1];
        live.PendingConnectControl = port;
        live.PendingConnectOutcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Noop,
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
            DestClient = dest,
            TargetBootId = "dest-boot",
            TargetConnectionGeneration = 2,
        };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            phase,
            Lease(SourceId, 1),
            Lease(DestId, 2));
        return live;
    }

    private static AttachLiveState PostCommitLive(
        ControlPlaneClient source,
        ControlPlaneClient dest,
        bool sourceAvailable)
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.ConnectedPlacementId = DestId;
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PaneId = "p1";
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourceClient = source,
            SourcePort = port,
            ConnectedPlacementId = "local",
            PaneId = "p1",
        };
        var local = new SidebarCubeItem
        {
            Id = "local",
            Name = "Local",
            Kind = SidebarCubeKind.Local,
            Reachability = SidebarCubeReachability.Local,
        };
        var peer = new SidebarCubeItem
        {
            Id = DestId,
            Name = "Peer",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        live.Cubes = [local, peer];
        live.PendingConnectCube = peer;
        live.PendingConnectControl = port;
        live.PendingConnectOutcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Noop,
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
            DestClient = dest,
            TargetBootId = "dest-boot",
            TargetConnectionGeneration = 2,
        };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "client-shell-surface:9:on",
                null,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            Lease("local", 1),
            Lease(DestId, 2),
            sourceAvailable);
        return live;
    }

    private static void RecordCoherent(EndpointActivationEvidence evidence)
    {
        evidence.RecordSnapshot(
            "local",
            1,
            new AttachSnapshotEvidence { BootId = "local-boot", Revision = 4 });
        evidence.RecordSurface(new AttachSurfaceEvidence
        {
            BootId = "local-boot",
            ProjectionRevision = 4,
            SurfaceRevision = 4,
            Columns = 80,
            Rows = 24,
            Focused = true,
        });
    }

    private static AttachRetargetPresentationBackup Backup(ControlPlaneClient source, IAttachCommandPort port) =>
        new()
        {
            SourceClient = source,
            SourcePort = port,
            ConnectedPlacementId = SourceId,
        };

    private static EndpointActivationLease Lease(string endpointId, ulong generation) =>
        new()
        {
            EndpointId = endpointId,
            ConnectionGeneration = generation,
            BootId = endpointId + "-boot",
            MinimumProjectionRevision = 0,
            ClientId = "client",
            LeaseId = endpointId + "-lease",
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

    private static TaskCompletionSource<ControlPlaneCallResult> ArmLane(
        AttachLiveState live,
        string endpointId,
        ControlPlaneClient client,
        string requestId)
    {
        var wait = new TaskCompletionSource<ControlPlaneCallResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        live.EndpointCommands.BeginWait(endpointId, 1, endpointId + "-boot", requestId, "lane.probe", client, wait);
        live.EndpointCommands.Enqueue(new QueuedShellCommand(
            endpointId,
            1,
            endpointId + "-boot",
            requestId,
            "lane.probe",
            null,
            client));
        return wait;
    }

    private sealed class RecordingPeer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly object _gate = new();
        private readonly List<string> _lines = [];
        private NetworkStream? _stream;
        private Task? _reader;
        private int _bytes;

        private RecordingPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        internal string Path { get; }

        internal int Count
        {
            get
            {
                lock (_gate)
                    return _lines.Count;
            }
        }

        internal int ByteCount => Volatile.Read(ref _bytes);

        internal static RecordingPeer Listen()
        {
            var path = "/tmp/hypa-loss-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            if (File.Exists(path))
                File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(1);
                return new RecordingPeer(listener, path);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        internal async Task AcceptAsync()
        {
            var accepted = await _listener.AcceptAsync();
            _stream = new NetworkStream(accepted, ownsSocket: true);
            _reader = ReadAsync();
        }

        internal List<string> Methods()
        {
            lock (_gate)
                return _lines.Select(MethodOf).ToList();
        }

        internal string Line(int index)
        {
            lock (_gate)
                return _lines[index];
        }

        public async ValueTask DisposeAsync()
        {
            _stream?.Dispose();
            _listener.Dispose();
            if (_reader is not null)
            {
                try
                {
                    await _reader.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (Exception)
                {
                }
            }

            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }

        private async Task ReadAsync()
        {
            var stream = _stream ?? throw new InvalidOperationException("peer is not connected");
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                        return;
                    if (line.Length == 0)
                        continue;
                    Interlocked.Add(ref _bytes, Encoding.UTF8.GetByteCount(line) + 1);
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var id = root.TryGetProperty("id", out var idEl) ? IdJson(idEl) : null;
                    lock (_gate)
                        _lines.Add(line);
                    if (id is null)
                        continue;
                    var reply = "{\"id\":" + id + ",\"result\":{\"request_id\":\"ok\",\"boot_id\":\"dest-boot\",\"projection_revision\":1,\"geometry_revision\":1,\"active\":true,\"lease_id\":\"lease\",\"minimum_projection_revision\":0,\"connection_generation\":1}}";
                    var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static string MethodOf(string line)
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("method", out var method)
                ? method.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string? IdJson(JsonElement id) =>
            id.ValueKind switch
            {
                JsonValueKind.String => JsonSerializer.Serialize(id.GetString()),
                JsonValueKind.Number => id.GetRawText(),
                _ => null,
            };
    }
}
