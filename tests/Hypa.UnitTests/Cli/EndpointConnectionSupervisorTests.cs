using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Connections;
using Hypa.Cli.Attach.Mouse;
using Hypa.AgentRuntime.Application.Attach.Status;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;
using Hypa.AgentRuntime.Domain.Cubes.Connections;
using Hypa.AgentRuntime.Application.Cubes.Connections;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The endpoint reconnect. The supervisor redials a lost cube through
/// <see cref="IEndpointRedialPort"/>. The step takes no mouse port, so a
/// redial cannot reach the cube click path.
/// </summary>
public sealed class EndpointConnectionSupervisorTests
{
    [Fact]
    public void LosingTheActiveCubeFreezesTheViewWithoutAnError()
    {
        var (live, dest, _) = Committed(new FakeClock());
        live.InputFrozen = true;

        Lose(live, dest);

        Assert.Equal("docker", live.Connections.Armed?.EndpointId);
        Assert.Equal("docker", live.ConnectedPlacementId);
        Assert.Null(live.StatusError);
        Assert.Null(live.StatusNotice);
        Assert.True(live.PresentationFrozen);
        Assert.False(live.InputFrozen);
        Assert.False(AttachSession.BlocksPresentationInput(live));
        Assert.True(AttachSession.BlocksPaneKeys(live));
    }

    [Fact]
    public void LossProjectsReconnectingAndACommitProjectsOnline()
    {
        var (live, dest, _) = Committed(new FakeClock());
        Lose(live, dest);

        Assert.Equal(ClientEndpointStatus.Reconnecting, live.GetEndpointStatus("docker"));
        Assert.False(live.EndpointProjectionAvailable("docker"));

        AttachSession.MarkEndpointLive(live, new ConnectionId("docker", 8), "boot-2");

        Assert.Equal(ClientEndpointStatus.Online, live.GetEndpointStatus("docker"));
        Assert.Null(live.Connections.Armed);
    }

    [Fact]
    public async Task RedialRunsAtOnceThenBacksOffBeforeTheNextAttempt()
    {
        var clock = new FakeClock();
        var (live, dest, port) = Committed(clock);
        var redial = new FakeRedialPort();
        Lose(live, dest);

        Assert.True(await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None));
        Assert.Equal(1, live.Connections.Armed!.Attempts);
        Assert.Equal(["docker"], redial.Calls);
        Assert.Empty(port.Calls);

        clock.Advance(TimeSpan.FromMilliseconds(400));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal(1, live.Connections.Armed!.Attempts);
        Assert.Single(redial.Calls);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal(2, live.Connections.Armed!.Attempts);
        Assert.Equal(["docker", "docker"], redial.Calls);
        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task RedialDialsTheSameCubeWithoutSelectingLocal()
    {
        var (live, dest, port) = Committed(new FakeClock());
        var redial = new FakeRedialPort();
        live.Cubes = [Local(), .. live.Cubes];
        Lose(live, dest);

        Assert.True(await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None));

        Assert.Equal("docker", live.ConnectedPlacementId);
        Assert.Equal("docker", live.Connections.Armed?.EndpointId);
        Assert.Equal(1, live.Connections.Armed!.Attempts);
        Assert.Equal(["docker"], redial.Calls);
        Assert.Null(live.StatusError);
        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task RedialTransitionRunsUnderTheActivationGate()
    {
        var clock = new GateProbeClock();
        var (live, dest, _) = Committed(clock);
        var redial = new FakeRedialPort();
        Lose(live, dest);
        clock.Probe(live.ActivationGate, () => live.Connections.Armed?.Attempts == 1);

        Assert.True(await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None));

        Assert.Equal(1, live.Connections.Armed!.Attempts);
        Assert.True(clock.HeldGateWhenProbed);
    }

    [Fact]
    public void ReconnectStatusRepeatKeepsALaterNoticeOnTop()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        Lose(live, dest);
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.True(AttachSession.ApplyReconnectStatus(live, live.Connections.Armed!));
        live.Status.Set(StatusOwner.Transport, StatusSeverity.Notice, "later notice");

        Assert.False(AttachSession.ApplyReconnectStatus(live, live.Connections.Armed!));

        Assert.Equal("later notice", live.StatusNotice);
    }

    [Fact]
    public async Task StatusIsEmptyBeforeTwoSecondsThenShowsANeutralNotice()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        var redial = new FakeRedialPort();
        Lose(live, dest);

        clock.Advance(TimeSpan.FromMilliseconds(1900));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Null(live.StatusError);
        Assert.Null(live.StatusNotice);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal("Reconnecting to docker…", live.StatusNotice);
        Assert.Null(live.StatusError);
    }

    [Fact]
    public async Task StatusNamesTheCubeUnreachableAfterThirtySeconds()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        var redial = new FakeRedialPort();
        Lose(live, dest);

        clock.Advance(TimeSpan.FromSeconds(29));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal("Reconnecting to docker…", live.StatusNotice);
        Assert.Null(live.StatusError);

        clock.Advance(TimeSpan.FromSeconds(1));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal("docker is unreachable. Retrying…", live.StatusError);
        Assert.Null(live.StatusNotice);
    }

    [Fact]
    public void FailedRedialInsideTheGraceWindowWritesNoError()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        Lose(live, dest);

        AttachSession.PresentReconnectAwareFailure(live, "destination connect timed out", tty: null);
        Assert.Null(live.StatusError);
        Assert.Null(live.StatusNotice);
        Assert.NotNull(live.Connections.Armed);

        clock.Advance(TimeSpan.FromSeconds(3));
        AttachSession.PresentReconnectAwareFailure(live, "destination connect timed out", tty: null);
        Assert.Null(live.StatusError);
        Assert.Equal("Reconnecting to docker…", live.StatusNotice);

        clock.Advance(TimeSpan.FromSeconds(27));
        AttachSession.PresentReconnectAwareFailure(live, "destination connect timed out", tty: null);
        Assert.Equal("docker is unreachable. Retrying…", live.StatusError);
        Assert.Null(live.StatusNotice);
    }

    [Fact]
    public void RecoveredReconnectClearsTheNoticeAndTheUnreachableCopy()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        Lose(live, dest);
        clock.Advance(TimeSpan.FromSeconds(31));
        AttachSession.ApplyReconnectStatus(live, live.Connections.Armed!);
        Assert.Equal("docker is unreachable. Retrying…", live.StatusError);

        AttachSession.MarkEndpointLive(live, new ConnectionId("docker", 8), "boot-2");

        Assert.Null(live.Connections.Armed);
        Assert.Null(live.StatusError);
        Assert.Null(live.StatusNotice);
    }

    [Fact]
    public async Task InFlightRedialKeepsTheTimedStatusMovingWithoutAnotherAttempt()
    {
        var clock = new FakeClock();
        var (live, dest, port) = Committed(clock);
        var redial = new FakeRedialPort();
        Lose(live, dest);
        live.DestConnectAttempt = new AttachDestConnectAttempt
        {
            Generation = 0,
            EndpointId = "docker",
            PrepTask = new TaskCompletionSource().Task,
            Cancel = new CancellationTokenSource(),
        };

        clock.Advance(TimeSpan.FromSeconds(3));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal("Reconnecting to docker…", live.StatusNotice);
        Assert.Null(live.StatusError);
        Assert.Equal(0, live.Connections.Armed!.Attempts);

        clock.Advance(TimeSpan.FromSeconds(28));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal("docker is unreachable. Retrying…", live.StatusError);
        Assert.Null(live.StatusNotice);
        Assert.Equal(0, live.Connections.Armed!.Attempts);
        Assert.Empty(redial.Calls);
        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task SelectingAnotherCubeClearsTheNotice()
    {
        var clock = new FakeClock();
        var (live, dest, port) = Committed(clock);
        var redial = new FakeRedialPort();
        Lose(live, dest);
        clock.Advance(TimeSpan.FromSeconds(3));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal("Reconnecting to docker…", live.StatusNotice);

        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "lab"),
            live,
            port,
            null,
            CancellationToken.None);

        Assert.Null(live.Connections.Armed);
        Assert.Null(live.StatusNotice);
    }

    [Fact]
    public void RedialKeepsTheLocalBackupAndDoesNotUseTheDeadClientAsSource()
    {
        var (live, _, port) = Committed(new FakeClock());
        var local = new ControlPlaneClient("/tmp/hypa-reconnect-local-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        var backup = new AttachRetargetPresentationBackup
        {
            SourceClient = local,
            SourcePort = port,
            ConnectedPlacementId = "local",
        };
        live.LocalRoot = backup;
        var dead = live.ControlSlot!.Client!;

        var request = AttachSession.BuildConnectRequestForTests(live, live.Cubes[0], port);
        Assert.True(request.Redial);
        Assert.False(request.SourceSurfaceActive);
        Assert.Null(request.SourceClient);
        Assert.NotSame(dead, request.SourceClient);

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
            TargetConnectionGeneration = 9,
            DestInputLease = "dest-input",
        };
        Assert.True(AttachSession.TryBuildSourceLeaseForTests(live, request, outcome, out var lease, out _));
        Assert.Null(lease);
        Assert.Same(backup, live.LocalRoot);
        Assert.Same(local, live.LocalRoot!.SourceClient);
    }

    [Fact]
    public async Task SelectingAnotherCubeCancelsTheReconnect()
    {
        var (live, dest, port) = Committed(new FakeClock());
        Lose(live, dest);
        Assert.NotNull(live.Connections.Armed);

        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "lab"),
            live,
            port,
            null,
            CancellationToken.None);

        Assert.Null(live.Connections.Armed);
        Assert.Equal(EndpointConnectionState.Failed, live.Connections.Entity("docker").State);
    }

    [Fact]
    public async Task SelectingLocalCancelsTheReconnect()
    {
        var (live, dest, port) = Committed(new FakeClock());
        live.Cubes = [Local(), .. live.Cubes];
        Lose(live, dest);

        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "local"),
            live,
            port,
            null,
            CancellationToken.None);

        Assert.Null(live.Connections.Armed);
    }

    [Fact]
    public async Task ClickOnTheLostCubeRedialsNowAndStaysArmed()
    {
        var (live, dest, port) = Committed(new FakeClock());
        Lose(live, dest);

        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "docker"),
            live,
            port,
            null,
            CancellationToken.None);

        var armed = live.Connections.Armed;
        Assert.NotNull(armed);
        Assert.Equal(EndpointConnectionState.Reconnecting, armed!.State);
        Assert.Equal(0, armed.Attempts);
    }

    [Fact]
    public async Task AFailedActivationAfterCommitKeepsTheRedialArmedOnItsSchedule()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        var redial = new FakeRedialPort();
        Lose(live, dest);
        var lostAt = live.Connections.Armed!.LostAt;
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Single(redial.Calls);

        // The commit point is reached. The activation still runs. The reconnect stays armed.
        live.PendingActivation = PendingForDocker();
        AttachSession.BeginEndpointCommit(live, new ConnectionId("docker", 8), "docker-boot");
        Assert.NotNull(live.Connections.Armed);
        Assert.Equal(EndpointConnectionState.Reconnecting, live.Connections.Armed!.State);
        Assert.Equal(ClientEndpointStatus.Online, live.GetEndpointStatus("docker"));

        // The activation fails before it completes.
        live.PendingActivation = null;
        AttachSession.PresentActivationUnavailable(live, "endpoint activation was rolled back");

        var armed = live.Connections.Armed!;
        Assert.Equal("activation_failed", armed.LastFailureCode);
        Assert.Equal(lostAt, armed.LostAt);
        Assert.Equal(ClientEndpointStatus.Reconnecting, live.GetEndpointStatus("docker"));

        // The next redial waits for the schedule: 0.5 s after the first attempt.
        clock.Advance(TimeSpan.FromMilliseconds(400));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Single(redial.Calls);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal(2, redial.Calls.Count);
        Assert.Equal(2, armed.Attempts);
        Assert.Equal(lostAt, armed.LostAt);

        // The grace window still counts from the original loss.
        clock.Advance(TimeSpan.FromSeconds(1.6));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal("Reconnecting to docker…", live.StatusNotice);
        Assert.Null(live.StatusError);
    }

    [Fact]
    public async Task ACompletedActivationRestoresTheConnectionOnce()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        var redial = new FakeRedialPort();
        Lose(live, dest);
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(3));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);
        Assert.Equal("Reconnecting to docker…", live.StatusNotice);

        // The commit point: the restore is not yet done, so the reconnect stays armed.
        live.PendingActivation = PendingForDocker();
        AttachSession.BeginEndpointCommit(live, new ConnectionId("docker", 8), "docker-boot");
        Assert.NotNull(live.Connections.Armed);
        Assert.Equal("Reconnecting to docker…", live.StatusNotice);

        // The activation completes.
        live.PendingActivation = null;
        Assert.True(AttachSession.CompleteEndpointCommit(live, "docker"));

        Assert.Null(live.Connections.Armed);
        Assert.Null(live.StatusNotice);
        Assert.Null(live.StatusError);
        Assert.Equal(ClientEndpointStatus.Online, live.GetEndpointStatus("docker"));

        // The restore is reported once.
        Assert.False(AttachSession.CompleteEndpointCommit(live, "docker"));
    }

    [Fact]
    public void AConnectFailureKeepsItsTypedReasonAsTheLastFailureCode()
    {
        var (live, dest, _) = Committed(new FakeClock());
        Lose(live, dest);

        AttachSession.PresentReconnectAwareFailure(
            live,
            "source snapshot not ready",
            tty: null,
            CubesConnectReasons.SourceSnapshotUnready);

        Assert.Equal("source_snapshot_unready", live.Connections.Armed!.LastFailureCode);
        Assert.Null(live.StatusError);
    }

    [Fact]
    public async Task ReconnectStopsWhenTheCubeLeavesTheCatalog()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        var redial = new FakeRedialPort();
        Lose(live, dest);
        live.Cubes = [];
        live.FrozenChromePaintArmed = false;

        clock.Advance(TimeSpan.FromSeconds(1));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);

        Assert.Null(live.Connections.Armed);
        Assert.Empty(redial.Calls);
        // The cleared reconnect status is painted at once, not at some later paint.
        Assert.True(live.FrozenChromePaintArmed);
    }

    private static void Lose(AttachLiveState live, ControlPlaneClient dest)
    {
        AttachSession.RecordEndpointTransportFailure(
            live,
            "docker",
            live.TransportEnvelope.Generation,
            "connection was lost",
            dest);
        AttachSession.DrainQueuedEndpointFailures(live, tty: null);
    }

    [Fact]
    public async Task ReconnectNoticeDoesNotEraseAMoveWorkError()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        var redial = new FakeRedialPort();
        live.Status.Set(StatusOwner.MoveWork, StatusSeverity.Error, "placement is not in the Cubes list");
        Lose(live, dest);

        clock.Advance(TimeSpan.FromMilliseconds(2200));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);

        Assert.Equal("Reconnecting to docker…", live.StatusNotice);
        Assert.Equal("placement is not in the Cubes list", live.StatusError);
    }

    [Fact]
    public async Task MoveWorkClearDoesNotEraseTheUnreachableError()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        var redial = new FakeRedialPort();
        Lose(live, dest);
        clock.Advance(TimeSpan.FromSeconds(31));
        await EndpointConnectionSupervisor.StepAsync(live, redial, null, CancellationToken.None);

        live.Status.Clear(StatusOwner.MoveWork);

        Assert.Equal("docker is unreachable. Retrying…", live.StatusError);
    }

    [Fact]
    public void CancelledReconnectClearsOnlyTheReconnectEntry()
    {
        var clock = new FakeClock();
        var (live, dest, _) = Committed(clock);
        Lose(live, dest);
        live.Status.Set(StatusOwner.Command, StatusSeverity.Error, "bad key");

        AttachSession.CancelReconnect(live, live.Connections.Armed!);

        Assert.Null(live.StatusNotice);
        Assert.Equal("bad key", live.StatusError);
    }

    private static (AttachLiveState Live, ControlPlaneClient Dest, MouseRecordingPort Port) Committed(TimeProvider clock)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dest = new ControlPlaneClient("/tmp/hypa-reconnect-dest-" + suffix + ".sock");
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Time = clock;
        live.ConnectedPlacementId = "docker";
        live.TransportEnvelope.StampServerGeneration(7);
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PresentationFrozen = false;
        live.Cubes =
        [
            Peer("docker"),
            Peer("lab"),
        ];
        return (live, dest, port);
    }

    private static PendingEndpointActivation PendingForDocker() =>
        PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            Lease("docker", 7),
            Lease("docker", 8),
            sourceAvailable: true);

    private static EndpointActivationLease Lease(string endpointId, ulong generation) =>
        new()
        {
            EndpointId = endpointId,
            ConnectionGeneration = generation,
            BootId = endpointId + "-boot",
            MinimumProjectionRevision = 0,
            ClientId = "attach-client",
            LeaseId = "lease-" + endpointId,
        };

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

    /// <summary>Records each redial start. It does not dial.</summary>
    private sealed class FakeRedialPort : IEndpointRedialPort
    {
        public List<string> Calls { get; } = [];

        public Task RedialAsync(string endpointId, CancellationToken ct)
        {
            Calls.Add(endpointId);
            return Task.CompletedTask;
        }
    }

    private sealed class GateProbeClock : TimeProvider
    {
        private object? _gate;
        private Func<bool>? _when;
        private bool _probed;

        public bool HeldGateWhenProbed { get; private set; }

        public void Probe(object gate, Func<bool> when)
        {
            _gate = gate;
            _when = when;
        }

        public override long GetTimestamp()
        {
            // The first read after the attempt count rises is the schedule
            // update inside the redial transition.
            if (!_probed && _gate is not null && _when!())
            {
                _probed = true;
                HeldGateWhenProbed = Monitor.IsEntered(_gate);
            }

            return 0;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 7, 4, 0, 0, TimeSpan.Zero);
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => _utc;

        public override long GetTimestamp() => _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta)
        {
            _utc += delta;
            _ticks += delta.Ticks;
        }
    }
}
