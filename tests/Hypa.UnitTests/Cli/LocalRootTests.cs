using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.UnitTests.Cli;

public sealed class LocalRootTests
{
    [Fact]
    public async Task SelectingLocalAfterTwoCubeHandoffsRestoresLocal()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote"), Peer("second")];
        await using var local = NewClient("local");
        await using var remote = NewClient("remote");
        await using var second = NewClient("second");
        StartOnLocal(live, local);

        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");
        Handoff(live, port, Peer("second"), second, targetGeneration: 8, sourceGeneration: 4, sourceBoot: "remote-boot");

        await AttachSession.RestoreLocalCubeProjectionAsync(live, Local(), port, null, CancellationToken.None);

        Assert.Same(local, live.ControlSlot!.Client);
        Assert.Equal("local", live.ConnectedPlacementId);
        Assert.Equal("sub-local", live.ControlSub);
        Assert.Equal("render-local", live.RenderSub);
        Assert.Equal("pane-local", live.PaneId);
        Assert.NotEqual("input-second", live.InputLease);
        Assert.False(local.IsDisposed);
    }

    [Fact]
    public async Task CommittedHandoffDisposesThePreviousCubeAndKeepsLocal()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote"), Peer("second")];
        await using var local = NewClient("commit-local");
        var remote = NewClient("commit-remote");
        await using var second = NewClient("commit-second");
        StartOnLocal(live, local);
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");

        Handoff(live, port, Peer("second"), second, targetGeneration: 8, sourceGeneration: 4, sourceBoot: "remote-boot");
        live.PendingActivation = Pending("second", 8, "second-boot");
        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);

        Assert.Null(successor);
        Assert.True(remote.IsDisposed);
        Assert.False(live.Connections.TryGet(remote, out _));
        Assert.False(local.IsDisposed);
        Assert.Same(second, live.ControlSlot!.Client);
    }

    [Fact]
    public async Task RollbackSourceIsClearedAfterCommitAndAfterRestoredSource()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote"), Peer("second")];
        await using var local = NewClient("clear-local");
        await using var remote = NewClient("clear-remote");
        await using var second = NewClient("clear-second");
        StartOnLocal(live, local);
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");
        Handoff(live, port, Peer("second"), second, targetGeneration: 8, sourceGeneration: 4, sourceBoot: "remote-boot");

        live.PendingActivation = Pending("second", 8, "second-boot");
        _ = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(live.RollbackSource);

        var restoredLocal = NewClient("clear-restored-local");
        await using var restoredRemote = NewClient("clear-restored-remote");
        StartOnLocal(live, restoredLocal);
        Handoff(live, port, Peer("remote"), restoredRemote, targetGeneration: 5, sourceGeneration: 1, sourceBoot: "local-boot");
        live.PendingActivation = ForRestore("remote", 5, "remote-boot");
        _ = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(live.RollbackSource);
    }

    [Fact]
    public async Task FailedHandoffRestoresTheRollbackSourceAndKeepsLocalRoot()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote"), Peer("second")];
        await using var local = NewClient("rollback-local");
        await using var remote = NewClient("rollback-remote");
        await using var second = NewClient("rollback-second");
        StartOnLocal(live, local);
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");
        var localRoot = live.LocalRoot;
        Assert.NotNull(localRoot);

        Handoff(live, port, Peer("second"), second, targetGeneration: 8, sourceGeneration: 4, sourceBoot: "remote-boot");
        AttachSession.ApplyEndpointActivationSetActive(live, "remote", "remote", "second");

        Assert.Same(remote, live.ControlSlot!.Client);
        Assert.Equal("remote", live.ConnectedPlacementId);
        Assert.Same(localRoot, live.LocalRoot);
    }

    [Fact]
    public async Task RedialWithoutSourceLeavesLocalRootUnchanged()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote")];
        await using var local = NewClient("redial-local");
        await using var remote = NewClient("redial-remote");
        StartOnLocal(live, local);
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");
        var localRoot = live.LocalRoot;
        live.Connections.Unbind(remote);
        await remote.DisposeAsync();

        var request = AttachSession.BuildConnectRequestForTests(live, Peer("remote"), port);
        Assert.True(request.Redial);
        Assert.True(AttachSession.TryBuildSourceLeaseForTests(live, request, Outcome(remote, 9, 0, null, "remote-boot"), out var lease, out _));
        Assert.Null(lease);
        Assert.Same(localRoot, live.LocalRoot);
    }

    [Fact]
    public async Task CubeSourceLeaseUsesTheBootAndGenerationOfTheCube()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote"), Peer("second")];
        await using var local = NewClient("lease-local");
        await using var remote = NewClient("lease-remote");
        await using var second = NewClient("lease-second");
        StartOnLocal(live, local);
        live.Connections.Bind(local, new ConnectionId("local", 1), "local-boot");
        live.SourceEndpointBootId = "local-boot";
        live.SourceTransportEnvelope.StampServerGeneration(1);
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");

        var request = AttachSession.BuildConnectRequestForTests(live, Peer("second"), port);

        Assert.Same(remote, request.SourceClient);
        Assert.Equal("remote-boot", request.ExistingSourceBootId);
        Assert.Equal(4UL, request.ExistingSourceConnectionGeneration);

        var outcome = Outcome(second, 8, request.ExistingSourceConnectionGeneration, request.ExistingSourceBootId, "second-boot");
        Assert.True(AttachSession.TryBuildSourceLeaseForTests(live, request, outcome, out var lease, out var error), error);
        Assert.NotNull(lease);
        Assert.Equal("remote", lease!.EndpointId);
        Assert.Equal(4UL, lease.ConnectionGeneration);
        Assert.Equal("remote-boot", lease.BootId);
    }

    [Fact]
    public async Task ResponseFromTheCubeSourceWithItsBootIsAcceptedDuringRelease()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote"), Peer("second")];
        await using var local = NewClient("accept-local");
        await using var remote = NewClient("accept-remote");
        await using var second = NewClient("accept-second");
        StartOnLocal(live, local);
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");

        var request = AttachSession.BuildConnectRequestForTests(live, Peer("second"), port);
        Assert.True(AttachSession.TryBuildSourceLeaseForTests(
            live,
            request,
            Outcome(second, 8, request.ExistingSourceConnectionGeneration, request.ExistingSourceBootId, "second-boot"),
            out var lease,
            out _));

        var pending = PendingEndpointActivation.ForTests(
            new ActivationPhase.ReleasingSource("client-shell-surface:9:off"),
            lease!,
            Lease("second", 8, "second-boot"));
        Assert.True(pending.AcceptsResponse("remote", 4, "remote-boot", "client-shell-surface:9:off"));
    }

    [Fact]
    public async Task SourceLeaseIdentityComesFromTheRegistryNotFromLocal()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote"), Peer("second")];
        await using var local = NewClient("registry-local");
        await using var remote = NewClient("registry-remote");
        StartOnLocal(live, local);
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");
        live.SourceEndpointBootId = "local-boot";

        var request = AttachSession.BuildConnectRequestForTests(live, Peer("second"), port);

        Assert.Equal("remote-boot", request.ExistingSourceBootId);
        Assert.Equal(4UL, request.ExistingSourceConnectionGeneration);
    }

    [Fact]
    public void FailedRestoreRepaintsTheWholeHostFrame()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        using var capture = new MemoryStream();
        using var tty = new UnixRawTerminal(capture, 80, 24);
        live.ConnectedPlacementId = "remote";
        live.PresentationFrozen = true;
        live.InputFrozen = true;
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.RestoringSource("client-shell-surface:9:restore", 5, new EndpointActivationEvidence()),
            Lease("remote", 4, "remote-boot"),
            Lease("second", 8, "second-boot"));
        var before = capture.Length;

        var successor = AttachSession.CompleteEndpointActivation(live, tty);

        Assert.Null(successor);
        Assert.Equal("endpoint activation lost its coherent snapshot/surface pair", live.StatusError);
        var painted = Encoding.ASCII.GetString(capture.ToArray(), (int)before, (int)(capture.Length - before));
        Assert.Contains("\u001b[?2026h", painted, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticActionDirectoryStaysOnTheLocalSessionAfterACubeConnect()
    {
        var live = MouseTestGeom.ApplyLive(
            MouseTestGeom.ApplyPort(),
            MouseTestGeom.Split(),
            homeSession: "default");
        live.SessionName = "second";

        Assert.Equal(
            MuxSessionCatalog.AttachStateDirectory("default"),
            AttachSession.SemanticActionStateDirectory(live));
    }

    [Fact]
    public async Task FailedUnavailableHandoffClearsTheRollbackSource()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote"), Peer("second")];
        await using var local = NewClient("unavailable-local");
        await using var remote = NewClient("unavailable-remote");
        StartOnLocal(live, local);
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");
        Assert.NotNull(live.RollbackSource);

        live.PlacementOwnerUnavailable = true;
        live.PendingActivation = Pending("remote", 4, "remote-boot");
        _ = AttachSession.CompleteEndpointActivation(live, tty: null);

        Assert.Null(live.PendingActivation);
        Assert.Null(live.RollbackSource);
        Assert.False(local.IsDisposed);
    }

    [Fact]
    public async Task ANewLocalConnectionUpdatesLocalRootAndLeavesTheActiveCubeAlone()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote")];
        await using var localA = NewClient("identity-local-a");
        await using var localB = NewClient("identity-local-b");
        await using var remote = NewClient("identity-remote");
        StartOnLocal(live, localA);
        live.ControlSub = "sub_local_1";
        live.RenderSub = "sub_local_1";
        live.AttachClientId = "conn_local_1";
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");
        live.AttachClientId = "conn_remote";
        Assert.Equal("conn_local_1", live.LocalRoot!.AttachClientId);

        AttachSession.ApplyLocalConnectionIdentity(
            live,
            localB,
            new LocalConnectionIdentity
            {
                ControlSub = "sub_local_2",
                RenderSub = "sub_local_2",
                AttachClientId = "conn_local_2",
                PaneId = "pane-local-2",
            });

        Assert.Same(localB, live.LocalRoot!.SourceClient);
        Assert.Equal("sub_local_2", live.LocalRoot.ControlSub);
        Assert.Equal("sub_local_2", live.LocalRoot.RenderSub);
        Assert.Equal("conn_local_2", live.LocalRoot.AttachClientId);
        Assert.Equal("pane-local-2", live.LocalRoot.PaneId);
        Assert.Same(remote, live.ControlSlot!.Client);
        Assert.Equal("sub-remote", live.ControlSub);
        Assert.Equal("render-remote", live.RenderSub);
        Assert.Equal("conn_remote", live.AttachClientId);
        Assert.Equal("pane-remote", live.PaneId);
        Assert.Equal("input-remote", live.InputLease);

        await AttachSession.RestoreLocalCubeProjectionAsync(live, Local(), port, null, CancellationToken.None);

        Assert.Same(localB, live.ControlSlot!.Client);
        Assert.Equal("sub_local_2", live.ControlSub);
        Assert.Equal("sub_local_2", live.RenderSub);
        Assert.Equal("conn_local_2", live.AttachClientId);
    }

    [Fact]
    public async Task ANewLocalConnectionWhileLocalIsActiveUpdatesTheLiveFieldsToo()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote")];
        await using var localA = NewClient("active-local-a");
        await using var localB = NewClient("active-local-b");
        await using var remote = NewClient("active-remote");
        StartOnLocal(live, localA);
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");
        await AttachSession.RestoreLocalCubeProjectionAsync(live, Local(), port, null, CancellationToken.None);
        Assert.True(AttachSession.IsLocalEndpointActive(live));

        AttachSession.ApplyLocalConnectionIdentity(
            live,
            localB,
            new LocalConnectionIdentity
            {
                ControlSub = "sub_local_3",
                RenderSub = "sub_local_3",
                AttachClientId = "conn_local_3",
                PaneId = "pane-local-3",
            });

        Assert.Equal("sub_local_3", live.ControlSub);
        Assert.Equal("conn_local_3", live.AttachClientId);
        Assert.Equal("pane-local-3", live.PaneId);
        Assert.Equal("sub_local_3", live.LocalRoot!.ControlSub);
        Assert.Same(localB, live.LocalRoot.SourceClient);
    }

    [Fact]
    public async Task BindingTheLocalClientWhileACubeIsActiveUpdatesTheWholeLocalRoot()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote")];
        await using var localA = NewClient("bind-local-a");
        await using var localB = NewClient("bind-local-b");
        await using var remote = NewClient("bind-remote");
        StartOnLocal(live, localA);
        live.AttachClientId = "conn_local_1";
        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");

        AttachSession.BindLocalAttachConnection(
            live,
            localB,
            generation: 2,
            bootId: "local-boot-2",
            new LocalConnectionIdentity
            {
                ControlSub = "sub_local_2",
                RenderSub = "sub_local_2",
                AttachClientId = "conn_local_2",
            });

        Assert.Same(localB, live.LocalRoot!.SourceClient);
        Assert.Equal("sub_local_2", live.LocalRoot.ControlSub);
        Assert.Equal("conn_local_2", live.LocalRoot.AttachClientId);
        Assert.True(live.Connections.TryGet(localB, out var bound));
        Assert.Equal("local", bound.EndpointId);
        Assert.Same(remote, live.ControlSlot!.Client);
        Assert.Equal("sub-remote", live.ControlSub);
        Assert.Equal("remote", live.ConnectedPlacementId);
    }

    [Fact]
    public async Task TheHeartbeatPingsTheLocalClientWhileACubeIsActive()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes = [Local(), Peer("remote")];
        await using var local = NewClient("beat-local");
        await using var remote = NewClient("beat-remote");
        StartOnLocal(live, local);
        Assert.Same(local, AttachSession.HeartbeatPingTarget(live, local));

        Handoff(live, port, Peer("remote"), remote, targetGeneration: 4, sourceGeneration: 1, sourceBoot: "local-boot");

        Assert.Same(remote, live.ControlSlot!.Client);
        Assert.Same(local, AttachSession.HeartbeatPingTarget(live, local));
    }

    private static void StartOnLocal(AttachLiveState live, ControlPlaneClient local)
    {
        live.ConnectedPlacementId = "local";
        live.ControlSlot = new AttachControlSlot { Client = local };
        live.Connections.Bind(local, new ConnectionId("local", 1), "local-boot");
        live.SourceEndpointBootId = "local-boot";
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.LastSnapshotConnectionGeneration = 1;
        live.LastSnapshot = Snapshot("local-boot");
        ApplyEndpointState(live, "local");
    }

    /// <summary>
    /// One handoff from the current control client to <paramref name="target"/>.
    /// The source lease is built as the attach builds it. The commit is then
    /// applied to the live state.
    /// </summary>
    private static void Handoff(
        AttachLiveState live,
        MouseRecordingPort port,
        SidebarCubeItem target,
        ControlPlaneClient dest,
        ulong targetGeneration,
        ulong sourceGeneration,
        string sourceBoot)
    {
        var request = AttachSession.BuildConnectRequestForTests(live, target, port);
        live.LastSnapshot = Snapshot(sourceBoot);
        live.LastSnapshotConnectionGeneration = sourceGeneration;
        var outcome = Outcome(dest, targetGeneration, sourceGeneration, sourceBoot, target.Id + "-boot");
        Assert.True(AttachSession.TryBuildSourceLeaseForTests(live, request, outcome, out _, out var error), error);

        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.Connections.Bind(dest, new ConnectionId(target.Id, targetGeneration), target.Id + "-boot");
        live.ConnectedPlacementId = target.Id;
        live.LastSnapshot = Snapshot(target.Id + "-boot");
        live.LastSnapshotConnectionGeneration = targetGeneration;
        ApplyEndpointState(live, target.Id);
    }

    private static void ApplyEndpointState(AttachLiveState live, string id)
    {
        live.PaneId = "pane-" + id;
        live.WorkspaceId = "ws-" + id;
        live.TabId = "tab-" + id;
        live.ControlSub = "sub-" + id;
        live.RenderSub = "render-" + id;
        live.InputLease = "input-" + id;
        live.ResizeLease = "resize-" + id;
    }

    private static PendingEndpointActivation Pending(
        string id,
        ulong generation,
        string bootId) =>
        PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                Lease(id, generation, bootId),
                "9:" + generation + ":" + bootId,
                true,
                new ActivationCompletion.Activated()),
            Lease("source", 1, "source-boot"),
            Lease(id, generation, bootId));

    private static PendingEndpointActivation ForRestore(
        string id,
        ulong generation,
        string bootId) =>
        PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                Lease(id, generation, bootId),
                "9:" + generation + ":" + bootId,
                true,
                new ActivationCompletion.RestoredSource("restore failed", null)),
            Lease("source", 1, "source-boot"),
            Lease(id, generation, bootId));

    private static CubesConnectRetargetOutcome Outcome(
        ControlPlaneClient dest,
        ulong targetGeneration,
        ulong sourceGeneration,
        string? sourceBoot,
        string targetBoot) =>
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
            DestClient = dest,
            DestSubscribeId = "sub-dest",
            DestPaneId = "pane-dest",
            DestWorkspaceId = "ws-dest",
            DestTabId = "tab-dest",
            DestInputLease = "input-dest",
            DestResizeLease = "resize-dest",
            TargetConnectionGeneration = targetGeneration,
            TargetBootId = targetBoot,
            SourceConnectionGeneration = sourceGeneration,
            SourceBootId = sourceBoot,
        };

    private static JsonElement Snapshot(string bootId)
    {
        using var doc = JsonDocument.Parse(
            $$"""{"boot_id":"{{bootId}}","projection_revision":1,"revision":1}""");
        return doc.RootElement.Clone();
    }

    private static EndpointActivationLease Lease(string endpointId, ulong generation, string bootId) =>
        new()
        {
            EndpointId = endpointId,
            ConnectionGeneration = generation,
            BootId = bootId,
            MinimumProjectionRevision = 0,
            ClientId = "attach-client",
            LeaseId = "lease-" + endpointId,
        };

    private static ControlPlaneClient NewClient(string name) =>
        new("/tmp/hypa-local-root-" + name + "-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

    private static SidebarCubeItem Local() => new()
    {
        Id = "local",
        Name = "local",
        Kind = SidebarCubeKind.Local,
        Reachability = SidebarCubeReachability.Reachable,
    };

    private static SidebarCubeItem Peer(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = SidebarCubeKind.Peer,
        Reachability = SidebarCubeReachability.Reachable,
    };
}
