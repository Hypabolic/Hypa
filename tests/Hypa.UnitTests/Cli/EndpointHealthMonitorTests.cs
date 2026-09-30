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
        Assert.Same(backup, live.SourceBackup);
        Assert.Same(source, live.SourceBackup!.SourceClient);

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
        Assert.Same(backup, live.SourceBackup);
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
        Assert.NotNull(live.SourceBackup);
        Assert.Same(source, live.SourceBackup.SourceClient);
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
        Assert.Same(backup, live.SourceBackup);
        Assert.Same(source, backup.SourceClient);
        Assert.False(source.IsDisposed);
        string error;
        lock (live.ActivationGate)
            error = live.EndpointFailures.Peek().Error;
        Assert.Equal("health timed out", error);

        AttachSession.DrainQueuedEndpointFailures(live, tty: null);
        Assert.True(live.PresentationFrozen);
        Assert.Equal("docker health timed out", live.StatusError);
        Assert.Empty(live.EndpointFailures);
        Assert.Same(backup, live.SourceBackup);
        Assert.Same(source, live.SourceBackup!.SourceClient);
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
        live.SourceBackup = backup;
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

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public void Advance(TimeSpan delta) => UtcNow += delta;
    }
}
