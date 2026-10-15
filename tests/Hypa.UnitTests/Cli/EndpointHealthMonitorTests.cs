using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class EndpointHealthMonitorTests
{
    [Fact]
    public async Task SuccessfulHealthProbeLeavesDestinationOpen()
    {
        var (live, source, dest, backup) = Committed();
        var clock = new ManualClock();
        await using var monitor = AttachSession.CreateEndpointHealthMonitor(
            live,
            dest,
            "docker",
            (_, _) => Task.FromResult(new AttachHealthResult
            {
                RequestId = "health-ok",
                BootId = "remote-boot",
            }),
            clock);
        live.HealthMonitor = monitor;

        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        await monitor.ProbeTask;

        Assert.False(dest.IsDisposed);
        Assert.False(source.IsDisposed);
        Assert.False(live.PresentationFrozen);
        Assert.False(live.TransportEnvelope.IsInvalidated);
        Assert.Empty(live.EndpointFailures);
        Assert.Same(backup, live.RollbackSource);
        Assert.Same(source, live.RollbackSource!.SourceClient);

        AttachSession.DrainQueuedEndpointFailures(live, tty: null);
        Assert.False(live.PresentationFrozen);
        Assert.False(dest.IsDisposed);
    }

    [Fact]
    public async Task HealthProbeCompletingAfterFiveSecondsStaysOpen()
    {
        var (live, source, dest, backup) = Committed();
        var clock = new ManualClock();
        var gate = new TaskCompletionSource<AttachHealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = AttachSession.CreateEndpointHealthMonitor(
            live,
            dest,
            "docker",
            (_, _) => gate.Task,
            clock);
        live.HealthMonitor = monitor;

        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();

        Assert.False(dest.IsDisposed);
        Assert.False(live.PresentationFrozen);
        Assert.Empty(live.EndpointFailures);

        gate.SetResult(new AttachHealthResult { RequestId = "health-slow", BootId = "remote-boot" });
        await monitor.ProbeTask;
        monitor.Poll();

        Assert.False(dest.IsDisposed);
        Assert.False(source.IsDisposed);
        Assert.False(live.PresentationFrozen);
        Assert.False(live.TransportEnvelope.IsInvalidated);
        Assert.Empty(live.EndpointFailures);
        Assert.Same(backup, live.RollbackSource);
        AttachSession.DrainQueuedEndpointFailures(live, tty: null);
        Assert.False(live.PresentationFrozen);
        Assert.Null(live.StatusError);
    }

    [Fact]
    public async Task HealthExceptionBeforeExpiryLeavesDestinationOpen()
    {
        var (live, source, dest, _) = Committed();
        var clock = new ManualClock();
        await using var monitor = AttachSession.CreateEndpointHealthMonitor(
            live,
            dest,
            "docker",
            (_, _) => Task.FromException<AttachHealthResult>(new InvalidOperationException("health failed")),
            clock);
        live.HealthMonitor = monitor;

        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        await monitor.ProbeTask;
        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();

        Assert.False(dest.IsDisposed);
        Assert.False(source.IsDisposed);
        Assert.False(live.PresentationFrozen);
        Assert.False(live.TransportEnvelope.IsInvalidated);
        Assert.Empty(live.EndpointFailures);
        Assert.NotNull(live.RollbackSource);
        Assert.Same(source, live.RollbackSource.SourceClient);
    }

    [Fact]
    public async Task UnansweredHealthProbeExpiresAfterTenSeconds()
    {
        var (live, source, dest, backup) = Committed();
        var clock = new ManualClock();
        var hang = new TaskCompletionSource<AttachHealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = AttachSession.CreateEndpointHealthMonitor(
            live,
            dest,
            "docker",
            (_, _) => hang.Task,
            clock);
        live.HealthMonitor = monitor;

        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        Assert.False(monitor.ProbeTask.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        Assert.False(dest.IsDisposed);
        Assert.Empty(live.EndpointFailures);

        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();

        Assert.True(dest.IsDisposed);
        Assert.False(source.IsDisposed);
        Assert.True(live.TransportEnvelope.IsInvalidated);
        Assert.Same(backup, live.RollbackSource);
        Assert.Same(source, backup.SourceClient);
        Assert.False(source.IsDisposed);
        string error;
        lock (live.ActivationGate)
            error = live.EndpointFailures.Peek().Error;
        Assert.Equal("health timed out", error);

        AttachSession.DrainQueuedEndpointFailures(live, tty: null);
        Assert.True(live.PresentationFrozen);
        // The loss is silent. The quiet status waits for the 2s window.
        Assert.Null(live.StatusError);
        Assert.Equal("docker", live.Connections.Armed?.EndpointId);
        Assert.Empty(live.EndpointFailures);
        Assert.Same(backup, live.RollbackSource);
        Assert.Same(source, live.RollbackSource!.SourceClient);
        Assert.False(source.IsDisposed);

        monitor.Poll();
        Assert.Empty(live.EndpointFailures);
        Assert.False(source.IsDisposed);
    }

    [Fact]
    public async Task LineArrivingWhileHealthProbeHangsKeepsDestinationOpen()
    {
        var (live, _, dest, _) = Committed();
        var clock = new ManualClock();
        var hang = new TaskCompletionSource<AttachHealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidated = 0;
        await using var monitor = AttachSession.CreateEndpointHealthMonitor(
            live,
            dest,
            "docker",
            (_, _) => hang.Task,
            clock);
        monitor.ConnectionGenerationInvalidated += () => invalidated++;

        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        clock.Advance(TimeSpan.FromSeconds(4));
        monitor.Received();
        clock.Advance(TimeSpan.FromSeconds(6));
        monitor.Poll();

        Assert.Equal(0, invalidated);
        Assert.False(live.TransportEnvelope.IsInvalidated);
        Assert.False(dest.IsDisposed);
    }

    [Fact]
    public async Task SleepWithAnOutstandingProbeSendsANewProbeBeforeItExpires()
    {
        var (live, _, dest, _) = Committed();
        var clock = new ManualClock();
        var hang = new TaskCompletionSource<AttachHealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var invalidated = 0;
        await using var monitor = AttachSession.CreateEndpointHealthMonitor(
            live,
            dest,
            "docker",
            (_, _) =>
            {
                probes++;
                return hang.Task;
            },
            clock);
        monitor.ConnectionGenerationInvalidated += () => invalidated++;

        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        Assert.Equal(1, probes);

        // The laptop sleeps for one hour with that probe outstanding.
        // The monotonic clock runs during this sleep.
        clock.Advance(TimeSpan.FromHours(1));
        monitor.Poll();

        Assert.Equal(0, invalidated);
        Assert.Equal(2, probes);
        Assert.False(dest.IsDisposed);

        // The wake probe uses the short wake timeout, not the 10s health timeout.
        clock.Advance(TimeSpan.FromSeconds(2));
        monitor.Poll();
        Assert.Equal(0, invalidated);

        clock.Advance(TimeSpan.FromSeconds(1));
        monitor.Poll();
        Assert.Equal(1, invalidated);
        Assert.True(dest.IsDisposed);
    }

    [Fact]
    public async Task UnansweredProbeAfterWakeExpiresAfterThreeSeconds()
    {
        var (live, _, dest, _) = Committed();
        var clock = new ManualClock();
        var hang = new TaskCompletionSource<AttachHealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidated = 0;
        await using var monitor = AttachSession.CreateEndpointHealthMonitor(
            live,
            dest,
            "docker",
            (_, _) => hang.Task,
            clock);
        monitor.ConnectionGenerationInvalidated += () => invalidated++;

        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        clock.Suspend(TimeSpan.FromHours(1));
        monitor.Poll();

        clock.Advance(TimeSpan.FromSeconds(3));
        monitor.Poll();

        Assert.Equal(1, invalidated);
        Assert.True(dest.IsDisposed);
    }

    [Fact]
    public async Task ProbeAnsweredAfterWakeKeepsTheDestinationOpen()
    {
        var (live, _, dest, _) = Committed();
        var clock = new ManualClock();
        var first = new TaskCompletionSource<AttachHealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        await using var monitor = AttachSession.CreateEndpointHealthMonitor(
            live,
            dest,
            "docker",
            (_, _) => ++probes == 1
                ? first.Task
                : Task.FromResult(new AttachHealthResult { RequestId = "health-2", BootId = "remote-boot" }),
            clock);

        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        clock.Suspend(TimeSpan.FromMinutes(20));
        monitor.Poll();
        await monitor.ProbeTask;
        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        await monitor.ProbeTask;
        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();

        Assert.False(dest.IsDisposed);
        Assert.False(live.TransportEnvelope.IsInvalidated);
        Assert.Empty(live.EndpointFailures);
    }

    [Fact]
    public async Task WallClockStepBackDoesNotStopProbesOrExpiry()
    {
        var (live, _, dest, _) = Committed();
        var clock = new ManualClock();
        var hang = new TaskCompletionSource<AttachHealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var invalidated = 0;
        await using var monitor = AttachSession.CreateEndpointHealthMonitor(
            live,
            dest,
            "docker",
            (_, _) =>
            {
                probes++;
                return hang.Task;
            },
            clock);
        monitor.ConnectionGenerationInvalidated += () => invalidated++;

        // The wall clock steps back 1 h while 5 s pass.
        clock.StepWallBack(TimeSpan.FromHours(1), TimeSpan.FromSeconds(5));
        monitor.Poll();
        Assert.Equal(1, probes);

        clock.StepWallBack(TimeSpan.FromHours(1), TimeSpan.FromSeconds(5));
        monitor.Poll();
        Assert.Equal(0, invalidated);

        clock.StepWallBack(TimeSpan.FromHours(1), TimeSpan.FromSeconds(5));
        monitor.Poll();

        Assert.Equal(1, invalidated);
        Assert.True(dest.IsDisposed);
    }

    [Fact]
    public async Task MonitorDoesNotReportAClientThatIsAlreadyDisposed()
    {
        var (live, _, dest, _) = Committed();
        var clock = new ManualClock();
        var hang = new TaskCompletionSource<AttachHealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidated = 0;
        await using var monitor = AttachSession.CreateEndpointHealthMonitor(
            live,
            dest,
            "docker",
            (_, _) => hang.Task,
            clock);
        monitor.ConnectionGenerationInvalidated += () => invalidated++;

        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        // The transport loss path disposed this client first.
        await dest.DisposeAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();
        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Poll();

        Assert.Equal(0, invalidated);
        Assert.False(live.TransportEnvelope.IsInvalidated);
        Assert.Empty(live.EndpointFailures);
    }

    private static (AttachLiveState Live, ControlPlaneClient Source, ControlPlaneClient Dest, AttachRetargetPresentationBackup Backup) Committed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = new ControlPlaneClient("/tmp/hypa-health-source-" + suffix + ".sock");
        var dest = new ControlPlaneClient("/tmp/hypa-health-dest-" + suffix + ".sock");
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.ConnectedPlacementId = "docker";
        live.TransportEnvelope.StampServerGeneration(7);
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.PresentationFrozen = false;
        var backup = new AttachRetargetPresentationBackup
        {
            SourceClient = source,
            SourcePort = port,
            ConnectedPlacementId = "local",
        };
        live.RollbackSource = backup;
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "docker",
                Name = "docker",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        return (live, source, dest, backup);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } =
            new(2026, 9, 23, 5, 26, 54, TimeSpan.Zero);

        private long _stamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public override long GetTimestamp() => _stamp;

        /// <summary>Both clocks move forward.</summary>
        public void Advance(TimeSpan delta)
        {
            UtcNow += delta;
            _stamp += delta.Ticks;
        }

        /// <summary>The wall clock moves. The monotonic clock stays, as in a suspend.</summary>
        public void Suspend(TimeSpan delta) => UtcNow += delta;

        /// <summary>The wall clock steps back. The monotonic clock runs forward.</summary>
        public void StepWallBack(TimeSpan back, TimeSpan elapsed)
        {
            UtcNow += elapsed - back;
            _stamp += elapsed.Ticks;
        }
    }
}
