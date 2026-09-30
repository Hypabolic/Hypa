using System.IO;
using System.Text;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class CubesConnectDestPrepTests
{
    [Fact]
    public async Task DestConnectPrepReturnsBeforeHelloAndKeepsSourceLive()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var hang = new HangRetargeter();
        live.CubesConnect = hang;

        try
        {
            var apply = AttachSession.ApplyCubesConnectAsync(
                DestClick(),
                live,
                port,
                tty,
                CancellationToken.None);
            await apply.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(live.DestConnectAttempt);
            Assert.Equal("plc_peer", live.DestConnectAttempt!.EndpointId);
            Assert.Null(live.PendingActivation);
            Assert.False(live.PresentationFrozen);
            Assert.False(live.InputFrozen);
            Assert.False(AttachSession.BlocksPaneKeys(live));
            Assert.True(live.TryGetPaneFrame("p1", out var frame));
            Assert.Equal("s", frame!.Cells[0][0].Text);

            AttachSession.PaintChrome(tty, live, requestRepaint: true);
            Assert.Contains("source", HostText(live.Host), StringComparison.Ordinal);

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
                tty,
                linked,
                CancellationToken.None);
            Assert.Equal(1, forwarded);
        }
        finally
        {
            hang.Gate.TrySetCanceled();
            try
            {
                await AttachSession.FlushDestConnectPrepForTests(live, tty);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task IdenticalDestClickSharesInFlightPrep()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        var hang = new HangRetargeter();
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1UL, live.DestConnectGeneration);
            hang.Gate.TrySetResult(DeniedOutcome());
            await AttachSession.FlushDestConnectPrepForTests(live);
            Assert.Equal(1, hang.Calls);

            hang.Gate = new TaskCompletionSource<CubesConnectRetargetOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            hang.Gate.TrySetResult(DeniedOutcome());
            await AttachSession.FlushDestConnectPrepForTests(live);
            Assert.Equal(2, hang.Calls);
            Assert.False(live.PresentationFrozen);
            Assert.Equal(AttachEndpointUserCopy.DestLeaseDenied, live.StatusError);
        }
        finally
        {
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task StaleDestCompletionDisposesAndDoesNotInstall()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-stale-dest-prep.sock");
        live.CubesConnect = new ImmediateRetargeter(OkOutcome(dest));

        await AttachSession.ApplyCubesConnectAsync(
                DestClick(), live, port, tty: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Task? prep;
        lock (live.ActivationGate)
            prep = live.DestConnectAttempt?.PrepTask;
        Assert.NotNull(prep);
        await prep!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEmpty(live.DestConnectCompletions);

        AttachSession.RetireDestConnectAttempt(live);
        await AttachSession.DrainDestConnectCompletions(live, tty: null);

        Assert.Null(live.PendingActivation);
        Assert.False(live.PresentationFrozen);
        Assert.True(SpinWait.SpinUntil(() => dest.IsDisposed, TimeSpan.FromSeconds(2)));
        Assert.True(live.TryGetPaneFrame("p1", out _));
    }

    [Fact]
    public async Task LocalClickDuringDestPrepRetiresAttempt()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        await using var dest = new ControlPlaneClient("/tmp/hypa-local-retire-dest.sock");
        var hang = new HangRetargeter();
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);
            var prep = live.DestConnectAttempt!.PrepTask;

            await AttachSession.ApplyCubesConnectAsync(
                    new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "plc_local"),
                    live,
                    port,
                    tty,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Null(live.DestConnectAttempt);
            Assert.Equal("plc_local", live.ConnectedPlacementId);
            Assert.False(live.PresentationFrozen);

            hang.Gate.TrySetResult(OkOutcome(dest));
            await prep.WaitAsync(TimeSpan.FromSeconds(2));
            await AttachSession.DrainDestConnectCompletions(live, tty);
            Assert.Null(live.PendingActivation);
            Assert.False(live.PresentationFrozen);
        }
        finally
        {
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task QueuedDestSuccessThenLocalDoesNotInstall()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-queued-dest-then-local.sock");
        live.CubesConnect = new ImmediateRetargeter(OkOutcome(dest));
        var sourceLease = live.InputLease;
        var sourceResize = live.ResizeLease;
        var sourceSlot = live.ControlSlot;

        await AttachSession.ApplyCubesConnectAsync(
                DestClick(), live, port, tty: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Task? prep;
        lock (live.ActivationGate)
            prep = live.DestConnectAttempt?.PrepTask;
        Assert.NotNull(prep);
        await prep!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEmpty(live.DestConnectCompletions);
        Assert.NotNull(live.DestConnectAttempt);

        await AttachSession.ApplyCubesConnectAsync(
                new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "plc_local"),
                live,
                port,
                tty: null,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(live.DestConnectAttempt);
        Assert.NotEmpty(live.DestConnectCompletions);
        Assert.Same(dest, live.DestConnectCompletions.Peek().Outcome?.DestClient);

        await AttachSession.DrainDestConnectCompletions(live, tty: null);

        Assert.Null(live.PendingActivation);
        Assert.False(live.PresentationFrozen);
        Assert.Equal("plc_local", live.ConnectedPlacementId);
        Assert.Equal(sourceLease, live.InputLease);
        Assert.Equal(sourceResize, live.ResizeLease);
        Assert.Same(sourceSlot, live.ControlSlot);
        Assert.True(
            dest.IsDisposed,
            "stale dest success after Local must dispose dest before drain returns");
    }

    [Fact]
    public async Task RetireAfterAttemptClearedStillBlocksQueuedDest()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-retire-cleared-attempt-dest.sock");
        live.CubesConnect = new ImmediateRetargeter(OkOutcome(dest));

        await AttachSession.ApplyCubesConnectAsync(
                DestClick(), live, port, tty: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Task? prep;
        lock (live.ActivationGate)
            prep = live.DestConnectAttempt?.PrepTask;
        Assert.NotNull(prep);
        await prep!.WaitAsync(TimeSpan.FromSeconds(2));
        var generation = live.DestConnectGeneration;
        lock (live.ActivationGate)
            live.DestConnectAttempt = null;

        AttachSession.RetireDestConnectAttempt(live);
        Assert.True(live.DestConnectGeneration > generation);

        await AttachSession.DrainDestConnectCompletions(live, tty: null);

        Assert.Null(live.PendingActivation);
        Assert.False(live.PresentationFrozen);
        Assert.Equal("lease-in", live.InputLease);
        Assert.True(dest.IsDisposed);
    }

    [Fact]
    public async Task NewerDestAfterQueuedSuccessDoesNotInstallPriorDest()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        live.Cubes =
        [
            live.Cubes[0],
            live.Cubes[1],
            new SidebarCubeItem
            {
                Id = "plc_other",
                Name = "Other",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        await using var dest = new ControlPlaneClient("/tmp/hypa-newer-after-queued-dest.sock");
        var hang = new HangRetargeter();
        hang.Gate.TrySetResult(OkOutcome(dest));
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Task? prep;
            lock (live.ActivationGate)
                prep = live.DestConnectAttempt?.PrepTask;
            Assert.NotNull(prep);
            await prep!.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotEmpty(live.DestConnectCompletions);

            hang.Gate = new TaskCompletionSource<CubesConnectRetargetOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await AttachSession.ApplyCubesConnectAsync(
                    new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "plc_other"),
                    live,
                    port,
                    tty: null,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            await AttachSession.DrainDestConnectCompletions(live, tty: null);

            Assert.Null(live.PendingActivation);
            Assert.False(live.PresentationFrozen);
            Assert.Equal("plc_other", live.DestConnectAttempt!.EndpointId);
            Assert.True(dest.IsDisposed);
            Assert.Equal("lease-in", live.InputLease);
        }
        finally
        {
            hang.Gate.TrySetCanceled();
            try
            {
                await AttachSession.FlushDestConnectPrepForTests(live);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task CanceledAfterDestHelloDoesNotInstall()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-cancel-after-hello-dest.sock");
        var hang = new HangRetargeter { IgnoreCancellation = true };
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);
            var prep = live.DestConnectAttempt!.PrepTask;

            AttachSession.RetireDestConnectAttempt(live);
            hang.Gate.TrySetResult(OkOutcome(dest));
            await prep.WaitAsync(TimeSpan.FromSeconds(2));
            await AttachSession.DrainDestConnectCompletions(live, tty: null);

            Assert.Null(live.PendingActivation);
            Assert.False(live.PresentationFrozen);
            Assert.True(dest.IsDisposed);
            Assert.Equal("lease-in", live.InputLease);
        }
        finally
        {
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task SessionTeardownDisposesQueuedDestSuccess()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-teardown-queued-dest.sock");
        live.CubesConnect = new ImmediateRetargeter(OkOutcome(dest));

        await AttachSession.ApplyCubesConnectAsync(
                DestClick(), live, port, tty: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Task? prep;
        lock (live.ActivationGate)
            prep = live.DestConnectAttempt?.PrepTask;
        Assert.NotNull(prep);
        await prep!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEmpty(live.DestConnectCompletions);

        await AttachSession.ShutdownDestConnectPrepAsync(live)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(live.DestConnectCompletions);
        Assert.Null(live.DestConnectAttempt);
        Assert.Null(live.PendingActivation);
        Assert.False(live.PresentationFrozen);
        Assert.True(dest.IsDisposed);
        Assert.Equal("lease-in", live.InputLease);
        Assert.True(live.TryGetPaneFrame("p1", out _));
    }

    [Fact]
    public async Task SessionTeardownDisposesDestThatCompletesAfterRetire()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-teardown-after-retire-dest.sock");
        var hang = new HangRetargeter { IgnoreCancellation = true };
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);

            var shutdown = AttachSession.ShutdownDestConnectPrepAsync(live);
            hang.Gate.TrySetResult(OkOutcome(dest));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Empty(live.DestConnectCompletions);
            Assert.Null(live.DestConnectAttempt);
            Assert.Null(live.PendingActivation);
            Assert.False(live.PresentationFrozen);
            Assert.True(dest.IsDisposed);
            Assert.Equal("lease-in", live.InputLease);
        }
        finally
        {
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task StaleOpenDestDisposeDoesNotWaitCallTimeout()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var hang = new HangStream();
        await using var dest = ControlPlaneClient.FromConnectedStream(
            hang,
            callTimeout: TimeSpan.FromSeconds(30));
        await dest.ConnectAsync();
        Assert.True(dest.HasOpenConnection);
        live.CubesConnect = new ImmediateRetargeter(OkOutcome(dest));

        await AttachSession.ApplyCubesConnectAsync(
                DestClick(), live, port, tty: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Task? prep;
        lock (live.ActivationGate)
            prep = live.DestConnectAttempt?.PrepTask;
        Assert.NotNull(prep);
        await prep!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEmpty(live.DestConnectCompletions);

        AttachSession.RetireDestConnectAttempt(live);
        var drain = AttachEndpointActivationPump.RunTick(live, tty: null);
        await drain.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(live.PendingActivation);
        Assert.False(live.PresentationFrozen);
        Assert.True(dest.IsDisposed);
        Assert.True(live.TryGetPaneFrame("p1", out var frame));
        Assert.Equal("s", frame!.Cells[0][0].Text);
    }

    [Fact]
    public async Task DestClickDoesNotRefreshSourceChromeWhilePrepInFlight()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        var hang = new HangRetargeter();
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyChromeHitAsync(
                    new ChromeHit(ChromeHitKind.SidebarCube, PlacementId: "plc_peer"),
                    live,
                    port,
                    tty: null,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(live.DestConnectAttempt);
            Assert.Null(live.PendingActivation);
            Assert.DoesNotContain(
                port.Calls,
                call => call.Method == ProtocolMethods.SessionSnapshot
                    || call.Method == ProtocolMethods.LayoutExport
                    || call.Method == ProtocolMethods.TabList);
        }
        finally
        {
            hang.Gate.TrySetCanceled();
            try
            {
                await AttachSession.FlushDestConnectPrepForTests(live);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task PreInstallDestDenyKeepsSourceAndPaintsCopy()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        using var capture = new MemoryStream();
        using var tty = new UnixRawTerminal(capture, 80, 24);
        live.CubesConnect = new ImmediateRetargeter(DeniedOutcome());

        await AttachSession.ApplyCubesConnectAsync(
                DestClick(), live, port, tty, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await AttachSession.FlushDestConnectPrepForTests(live, tty);

        Assert.False(live.PresentationFrozen);
        Assert.False(live.InputFrozen);
        Assert.False(live.FrozenChromePaintArmed);
        Assert.False(AttachSession.BlocksPaneKeys(live));
        Assert.Equal(AttachEndpointUserCopy.DestLeaseDenied, live.StatusError);
        Assert.True(live.TryGetPaneFrame("p1", out _));
        AttachSession.PaintChrome(tty, live, requestRepaint: true);
        var sink = new HostFrameCellSink(live.Host);
        live.Host.Resize(80, 24);
        ModeBarPainter.Stamp(
            sink,
            AttachClientMode.Terminal,
            ModeBarSlot.Bottom,
            80,
            24,
            endpointError: live.StatusError);
        Assert.Contains("already held", HostText(live.Host), StringComparison.Ordinal);

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
            tty,
            linked,
            CancellationToken.None);
        Assert.Equal(1, forwarded);
    }

    [Fact]
    public async Task NewerDestRetiresPriorPrep()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        live.Cubes =
        [
            live.Cubes[0],
            live.Cubes[1],
            new SidebarCubeItem
            {
                Id = "plc_other",
                Name = "Other",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        var hang = new HangRetargeter();
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var firstGeneration = live.DestConnectGeneration;
            await AttachSession.ApplyCubesConnectAsync(
                    new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "plc_other"),
                    live,
                    port,
                    tty: null,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(firstGeneration + 1, live.DestConnectGeneration);
            Assert.Equal("plc_other", live.DestConnectAttempt!.EndpointId);
        }
        finally
        {
            hang.Gate.TrySetCanceled();
            try
            {
                await AttachSession.FlushDestConnectPrepForTests(live);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task ConcurrentRunTickDoesNotInterleaveDestApplyWithAnotherDrain()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        var first = new HangEndpoint();
        var second = new HangEndpoint();

        lock (live.ActivationGate)
            live.DestConnectCompletions.Enqueue(DestCompletion(live, port, first, 11));

        var firstTick = AttachEndpointActivationPump.RunTick(live, tty: null);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lock (live.ActivationGate)
            live.DestConnectCompletions.Enqueue(DestCompletion(live, port, second, 12));
        var secondTick = AttachEndpointActivationPump.RunTick(live, tty: null);

        await Task.Delay(300);
        Assert.False(second.Started.Task.IsCompleted);
        Assert.False(firstTick.IsCompleted);
        Assert.False(secondTick.IsCompleted);

        first.Release.TrySetResult();
        await firstTick.WaitAsync(TimeSpan.FromSeconds(2));
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        second.Release.TrySetResult();
        await secondTick.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(live.PendingActivation);
        Assert.Empty(live.DestConnectCompletions);
    }

    [Fact]
    public async Task ShutdownDestConnectPrepDoesNotInterleaveDestApplyWithRunTick()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        var first = new HangEndpoint();
        var second = new HangEndpoint();

        lock (live.ActivationGate)
            live.DestConnectCompletions.Enqueue(DestCompletion(live, port, first, 11));

        var tick = AttachEndpointActivationPump.RunTick(live, tty: null);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lock (live.ActivationGate)
            live.DestConnectCompletions.Enqueue(DestCompletion(live, port, second, 12));
        var shutdown = AttachSession.ShutdownDestConnectPrepAsync(live);

        await Task.Delay(300);
        Assert.False(second.Started.Task.IsCompleted);
        Assert.False(tick.IsCompleted);
        Assert.False(shutdown.IsCompleted);

        first.Release.TrySetResult();
        await tick.WaitAsync(TimeSpan.FromSeconds(2));
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        second.Release.TrySetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(live.PendingActivation);
        Assert.Empty(live.DestConnectCompletions);
    }

    [Fact]
    public async Task SourceDisconnectDuringDestPrepLeavesDestAttempt()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        var hang = new HangRetargeter();
        live.CubesConnect = hang;
        Task? prep = null;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);
            prep = live.DestConnectAttempt!.PrepTask;
            var generation = live.DestConnectGeneration;
            var sourceClient = live.SourceBackup!.SourceClient;

            AttachSession.RecordEndpointTransportFailure(
                live,
                "plc_local",
                0,
                "source connection was lost");
            AttachSession.DrainQueuedEndpointFailures(live, tty: null);

            Assert.NotNull(live.DestConnectAttempt);
            Assert.Equal(generation, live.DestConnectGeneration);
            Assert.Equal("plc_peer", live.DestConnectAttempt!.EndpointId);
            Assert.True(sourceClient!.IsDisposed);
            Assert.Null(live.SourceBackup!.SourceClient);
            Assert.True(live.PresentationFrozen);
        }
        finally
        {
            hang.Gate.TrySetCanceled();
            if (prep is not null)
            {
                try
                {
                    await prep.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task DestTargetDisconnectDuringDestPrepRetiresAttemptAndLaterSuccessDoesNotInstall()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-dest-target-disconnect-prep.sock");
        var hang = new HangRetargeter { IgnoreCancellation = true };
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);
            var prep = live.DestConnectAttempt!.PrepTask;

            AttachSession.HandleEndpointDisconnect(
                live,
                "plc_peer",
                0,
                "destination connection was lost",
                tty: null);

            Assert.Null(live.DestConnectAttempt);
            Assert.False(live.PresentationFrozen);
            Assert.False(live.InputFrozen);

            hang.Gate.TrySetResult(OkOutcome(dest));
            await prep.WaitAsync(TimeSpan.FromSeconds(2));
            await AttachSession.DrainDestConnectCompletions(live, tty: null);

            Assert.Null(live.PendingActivation);
            Assert.False(live.PresentationFrozen);
            Assert.True(SpinWait.SpinUntil(() => dest.IsDisposed, TimeSpan.FromSeconds(2)));
            Assert.True(live.TryGetPaneFrame("p1", out _));
        }
        finally
        {
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task UnrelatedEndpointDisconnectLeavesDestPrep()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        var hang = new HangRetargeter();
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);
            var generation = live.DestConnectGeneration;
            var attempt = live.DestConnectAttempt;

            AttachSession.HandleEndpointDisconnect(
                live,
                "plc_unrelated",
                0,
                "unrelated endpoint gone",
                tty: null);

            Assert.Same(attempt, live.DestConnectAttempt);
            Assert.Equal(generation, live.DestConnectGeneration);
            Assert.Equal("plc_peer", live.DestConnectAttempt!.EndpointId);
            Assert.False(live.PresentationFrozen);
        }
        finally
        {
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task CatalogRetireConnectedSourceDuringDestPrepDoesNotInstallLaterSuccess()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-catalog-source-retire-dest-prep.sock");
        var sourceClient = new ControlPlaneClient("/tmp/hypa-catalog-source-retire-source.sock");
        live.ControlSlot = new AttachControlSlot { Client = sourceClient };
        live.InputSender = new AttachInputSender(
            sourceClient,
            () => (live.PaneId, live.InputLease),
            blocksForward: () => AttachSession.BlocksPaneKeys(live));
        Assert.True(live.InputSender.TryEnqueue([0x61]));
        var sender = live.InputSender;
        var hang = new HangRetargeter { IgnoreCancellation = true };
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);
            var prep = live.DestConnectAttempt!.PrepTask;
            var generation = live.DestConnectGeneration;

            live.QueueCatalogReload(
                [live.Cubes[1]],
                SidebarCubeCatalogState.Ready,
                "plc_local");
            Assert.True(AttachSession.TryFlushCatalogReloadPaint(live, tty: null));

            Assert.Null(live.DestConnectAttempt);
            Assert.True(live.DestConnectGeneration > generation);
            Assert.True(live.PlacementOwnerUnavailable);
            Assert.True(live.PresentationFrozen);
            Assert.True(AttachSession.BlocksPaneKeys(live));
            Assert.Same(sender, live.InputSender);
            Assert.False(live.InputSender.TryEnqueue([0x62]));

            hang.Gate.TrySetResult(OkOutcome(dest));
            await prep.WaitAsync(TimeSpan.FromSeconds(2));
            await AttachSession.DrainDestConnectCompletions(live, tty: null);

            Assert.Null(live.PendingActivation);
            Assert.True(SpinWait.SpinUntil(() => dest.IsDisposed, TimeSpan.FromSeconds(2)));
            Assert.Same(sourceClient, live.ControlSlot?.Client);
            Assert.Same(sender, live.InputSender);
            Assert.True(AttachSession.BlocksPaneKeys(live));
            Assert.False(live.InputSender.TryEnqueue([0x63]));
        }
        finally
        {
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task CatalogDisableDestCubeDuringDestPrepDoesNotInstallLaterSuccess()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-catalog-dest-disable-dest-prep.sock");
        var sourceClient = new ControlPlaneClient("/tmp/hypa-catalog-dest-disable-source.sock");
        live.ControlSlot = new AttachControlSlot { Client = sourceClient };
        live.InputSender = new AttachInputSender(
            sourceClient,
            () => (live.PaneId, live.InputLease),
            blocksForward: () => AttachSession.BlocksPaneKeys(live));
        var sender = live.InputSender;
        var hang = new HangRetargeter { IgnoreCancellation = true };
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);
            var prep = live.DestConnectAttempt!.PrepTask;
            var generation = live.DestConnectGeneration;

            live.QueueCatalogReload(
                [
                    live.Cubes[0],
                    live.Cubes[1] with { ConnectEnabled = false },
                ],
                SidebarCubeCatalogState.Ready,
                retiredPlacementId: null);
            Assert.True(AttachSession.TryFlushCatalogReloadPaint(live, tty: null));

            Assert.Null(live.DestConnectAttempt);
            Assert.True(live.DestConnectGeneration > generation);
            Assert.False(live.PlacementOwnerUnavailable);
            Assert.False(live.PresentationFrozen);
            Assert.False(AttachSession.BlocksPaneKeys(live));
            Assert.Same(sender, live.InputSender);

            hang.Gate.TrySetResult(OkOutcome(dest));
            await prep.WaitAsync(TimeSpan.FromSeconds(2));
            await AttachSession.DrainDestConnectCompletions(live, tty: null);

            Assert.Null(live.PendingActivation);
            Assert.True(SpinWait.SpinUntil(() => dest.IsDisposed, TimeSpan.FromSeconds(2)));
            Assert.Same(sourceClient, live.ControlSlot?.Client);
            Assert.Same(sender, live.InputSender);
            Assert.False(live.PresentationFrozen);
            Assert.False(AttachSession.BlocksPaneKeys(live));
            Assert.True(live.TryGetPaneFrame("p1", out _));
        }
        finally
        {
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task QueuedCatalogRetireOfConnectedSourceDoesNotInstallDestOnRunTick()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-queued-catalog-source-retire-dest.sock");
        var sourceClient = new ControlPlaneClient("/tmp/hypa-queued-catalog-source-retire-source.sock");
        live.ControlSlot = new AttachControlSlot { Client = sourceClient };
        live.InputSender = new AttachInputSender(
            sourceClient,
            () => (live.PaneId, live.InputLease),
            blocksForward: () => AttachSession.BlocksPaneKeys(live));
        var sender = live.InputSender;
        var hang = new HangRetargeter { IgnoreCancellation = true };
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);
            var prep = live.DestConnectAttempt!.PrepTask;

            live.QueueCatalogReload(
                [live.Cubes[1]],
                SidebarCubeCatalogState.Ready,
                "plc_local");
            Assert.True(live.HasCatalogReloadPending);

            hang.Gate.TrySetResult(OkOutcome(dest));
            await prep.WaitAsync(TimeSpan.FromSeconds(2));
            await AttachEndpointActivationPump.RunTick(live, tty: null);

            Assert.True(live.HasCatalogReloadPending);
            Assert.Null(live.PendingActivation);
            Assert.True(live.PlacementOwnerUnavailable);
            Assert.True(live.PresentationFrozen);
            Assert.True(AttachSession.BlocksPaneKeys(live));
            Assert.True(SpinWait.SpinUntil(() => dest.IsDisposed, TimeSpan.FromSeconds(2)));
            Assert.Same(sourceClient, live.ControlSlot?.Client);
            Assert.Same(sender, live.InputSender);
            Assert.False(live.InputSender.TryEnqueue([0x62]));
        }
        finally
        {
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task QueuedCatalogDisableDestCubeDoesNotInstallDestOnRunTick()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-queued-catalog-dest-disable-dest.sock");
        var sourceClient = new ControlPlaneClient("/tmp/hypa-queued-catalog-dest-disable-source.sock");
        live.ControlSlot = new AttachControlSlot { Client = sourceClient };
        live.InputSender = new AttachInputSender(
            sourceClient,
            () => (live.PaneId, live.InputLease),
            blocksForward: () => AttachSession.BlocksPaneKeys(live));
        var sender = live.InputSender;
        var hang = new HangRetargeter { IgnoreCancellation = true };
        live.CubesConnect = hang;

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);
            var prep = live.DestConnectAttempt!.PrepTask;

            live.QueueCatalogReload(
                [
                    live.Cubes[0],
                    live.Cubes[1] with { ConnectEnabled = false },
                ],
                SidebarCubeCatalogState.Ready,
                retiredPlacementId: null);
            Assert.True(live.HasCatalogReloadPending);

            hang.Gate.TrySetResult(OkOutcome(dest));
            await prep.WaitAsync(TimeSpan.FromSeconds(2));
            await AttachEndpointActivationPump.RunTick(live, tty: null);

            Assert.True(live.HasCatalogReloadPending);
            Assert.Null(live.PendingActivation);
            Assert.False(live.PlacementOwnerUnavailable);
            Assert.False(live.PresentationFrozen);
            Assert.False(AttachSession.BlocksPaneKeys(live));
            Assert.True(SpinWait.SpinUntil(() => dest.IsDisposed, TimeSpan.FromSeconds(2)));
            Assert.Same(sourceClient, live.ControlSlot?.Client);
            Assert.Same(sender, live.InputSender);
            Assert.True(live.InputSender.TryEnqueue([0x62]));
            Assert.True(live.TryGetPaneFrame("p1", out _));
        }
        finally
        {
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task CatalogFlushSkippedOnBusyControlGateStillFencesDestPrep()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-catalog-control-gate-dest.sock");
        var sourceClient = new ControlPlaneClient("/tmp/hypa-catalog-control-gate-source.sock");
        live.ControlSlot = new AttachControlSlot { Client = sourceClient };
        live.InputSender = new AttachInputSender(
            sourceClient,
            () => (live.PaneId, live.InputLease),
            blocksForward: () => AttachSession.BlocksPaneKeys(live));
        var sender = live.InputSender;
        var hang = new HangRetargeter { IgnoreCancellation = true };
        live.CubesConnect = hang;
        using var controlGate = new SemaphoreSlim(1, 1);
        await controlGate.WaitAsync();

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    DestClick(), live, port, tty: null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(live.DestConnectAttempt);
            var prep = live.DestConnectAttempt!.PrepTask;
            var generation = live.DestConnectGeneration;

            live.QueueCatalogReload(
                [live.Cubes[1]],
                SidebarCubeCatalogState.Ready,
                "plc_local");
            Assert.False(AttachSession.TryFlushCatalogReloadPaint(live, tty: null, controlGate));
            Assert.True(live.HasCatalogReloadPending);
            Assert.Null(live.DestConnectAttempt);
            Assert.True(live.DestConnectGeneration > generation);
            Assert.True(live.PlacementOwnerUnavailable);
            Assert.True(AttachSession.BlocksPaneKeys(live));

            hang.Gate.TrySetResult(OkOutcome(dest));
            await prep.WaitAsync(TimeSpan.FromSeconds(2));
            await AttachSession.DrainDestConnectCompletions(live, tty: null);

            Assert.Null(live.PendingActivation);
            Assert.True(SpinWait.SpinUntil(() => dest.IsDisposed, TimeSpan.FromSeconds(2)));
            Assert.Same(sourceClient, live.ControlSlot?.Client);
            Assert.Same(sender, live.InputSender);
            Assert.False(live.InputSender.TryEnqueue([0x62]));
        }
        finally
        {
            controlGate.Release();
            hang.Gate.TrySetCanceled();
        }
    }

    [Fact]
    public async Task CatalogOwnerFreezeAbortsDestPendingAndBlocksSetActive()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-catalog-abort-pending-dest.sock");
        var sourceClient = new ControlPlaneClient("/tmp/hypa-catalog-abort-pending-source.sock");
        live.ControlSlot = new AttachControlSlot { Client = sourceClient };
        live.InputSender = new AttachInputSender(
            sourceClient,
            () => (live.PaneId, live.InputLease),
            blocksForward: () => AttachSession.BlocksPaneKeys(live));
        live.InputLease = "src-in";
        var sender = live.InputSender;
        live.PendingConnectControl = port;
        live.PendingConnectCube = live.Cubes[1];
        live.PendingConnectOutcome = OkOutcome(dest) with
        {
            Action = CubesConnectActions.Retargeted,
            DestPaneId = "p-dest",
        };
        var destLease = new EndpointActivationLease
        {
            EndpointId = "plc_peer",
            ConnectionGeneration = 7,
            BootId = "dest-boot",
            MinimumProjectionRevision = 0,
            ClientId = "attach-client",
            LeaseId = "dest-in",
        };
        var sourceLease = new EndpointActivationLease
        {
            EndpointId = "plc_local",
            ConnectionGeneration = 1,
            BootId = "local-boot",
            MinimumProjectionRevision = 0,
            ClientId = "attach-client",
            LeaseId = "src-in",
        };
        AttachSession.InstallPendingActivationForTests(
            live,
            PendingEndpointActivation.ForTests(
                new ActivationPhase.AwaitingPresentationEffects(
                    destLease,
                    "9:7:dest-boot",
                    true,
                    new ActivationCompletion.Activated()),
                sourceLease,
                destLease),
            "Peer");
        Assert.NotNull(live.PendingActivation);

        live.QueueCatalogReload(
            [live.Cubes[1]],
            SidebarCubeCatalogState.Ready,
            "plc_local");
        Assert.True(AttachSession.TryFlushCatalogReloadPaint(live, tty: null));

        Assert.Null(live.PendingActivation);
        Assert.Null(live.PendingConnectOutcome);
        Assert.True(live.PlacementOwnerUnavailable);
        Assert.True(live.PresentationFrozen);
        Assert.True(AttachSession.BlocksPaneKeys(live));
        Assert.True(SpinWait.SpinUntil(() => dest.IsDisposed, TimeSpan.FromSeconds(2)));
        Assert.False(
            AttachSession.ApplyEndpointActivationSetActive(
                live,
                "plc_peer",
                "plc_local",
                "plc_peer"));
        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(successor);
        Assert.Null(live.PendingActivation);
        Assert.Same(sourceClient, live.ControlSlot?.Client);
        Assert.Same(sender, live.InputSender);
        Assert.Equal("src-in", live.InputLease);
        Assert.Equal("plc_local", live.ConnectedPlacementId);
        Assert.True(live.PlacementOwnerUnavailable);
        Assert.True(live.PresentationFrozen);
        Assert.False(live.InputSender.TryEnqueue([0x62]));
    }

    [Fact]
    public async Task QueuedCatalogRetireAbortsDestPendingOnRunTickWithoutFlush()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = LiveWithSource(port);
        await using var dest = new ControlPlaneClient("/tmp/hypa-queued-catalog-abort-pending-dest.sock");
        var sourceClient = new ControlPlaneClient("/tmp/hypa-queued-catalog-abort-pending-source.sock");
        live.ControlSlot = new AttachControlSlot { Client = sourceClient };
        live.InputSender = new AttachInputSender(
            sourceClient,
            () => (live.PaneId, live.InputLease),
            blocksForward: () => AttachSession.BlocksPaneKeys(live));
        live.InputLease = "src-in";
        var sender = live.InputSender;
        live.PendingConnectControl = port;
        live.PendingConnectCube = live.Cubes[1];
        live.PendingConnectOutcome = OkOutcome(dest) with
        {
            Action = CubesConnectActions.Retargeted,
            DestPaneId = "p-dest",
        };
        var destLease = new EndpointActivationLease
        {
            EndpointId = "plc_peer",
            ConnectionGeneration = 7,
            BootId = "dest-boot",
            MinimumProjectionRevision = 0,
            ClientId = "attach-client",
            LeaseId = "dest-in",
        };
        var sourceLease = new EndpointActivationLease
        {
            EndpointId = "plc_local",
            ConnectionGeneration = 1,
            BootId = "local-boot",
            MinimumProjectionRevision = 0,
            ClientId = "attach-client",
            LeaseId = "src-in",
        };
        AttachSession.InstallPendingActivationForTests(
            live,
            PendingEndpointActivation.ForTests(
                new ActivationPhase.AwaitingPresentationEffects(
                    destLease,
                    "9:7:dest-boot",
                    true,
                    new ActivationCompletion.Activated()),
                sourceLease,
                destLease),
            "Peer");

        live.QueueCatalogReload(
            [live.Cubes[1]],
            SidebarCubeCatalogState.Ready,
            "plc_local");
        await AttachEndpointActivationPump.RunTick(live, tty: null);

        Assert.True(live.HasCatalogReloadPending);
        Assert.Null(live.PendingActivation);
        Assert.Null(live.PendingConnectOutcome);
        Assert.True(live.PlacementOwnerUnavailable);
        Assert.True(SpinWait.SpinUntil(() => dest.IsDisposed, TimeSpan.FromSeconds(2)));
        Assert.False(
            AttachSession.ApplyEndpointActivationSetActive(
                live,
                "plc_peer",
                "plc_local",
                "plc_peer"));
        Assert.Null(AttachSession.CompleteEndpointActivation(live, tty: null));
        Assert.Same(sourceClient, live.ControlSlot?.Client);
        Assert.Same(sender, live.InputSender);
        Assert.Equal("src-in", live.InputLease);
        Assert.Equal("plc_local", live.ConnectedPlacementId);
        Assert.True(live.PresentationFrozen);
        Assert.False(live.InputSender.TryEnqueue([0x62]));
    }

    private static AttachLiveState LiveWithSource(MouseRecordingPort port)
    {
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.SetPaneFrame(MouseTestGeom.Frame("source", "p1", 80));
        live.ObservedPaneIds.Add("p1");
        live.ConnectedPlacementId = "plc_local";
        live.PlacementKind = SidebarCubeKind.Local;
        live.PlacementDisplayName = "Local";
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
                Id = "plc_peer",
                Name = "Peer",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourceClient = new ControlPlaneClient("/tmp/hypa-dest-prep-source.sock"),
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
        return live;
    }

    private static MouseEngineResult DestClick() =>
        new(MouseCommandKind.ApplyMenu, PlacementId: "plc_peer");

    private static string HostText(HostFrame host)
    {
        var text = new StringBuilder(host.Cols * host.Rows);
        for (var row = 0; row < host.Rows; row++)
        {
            for (var col = 0; col < host.Cols; col++)
                text.Append(host.CellAt(col, row).Text);
        }

        return text.ToString();
    }

    private static CubesConnectRetargetOutcome DeniedOutcome() =>
        new()
        {
            Ok = false,
            Reason = CubesConnectReasons.DestLeaseDenied,
            Detail = AttachEndpointUserCopy.DestLeaseDenied,
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
        };

    private static CubesConnectRetargetOutcome OkOutcome(ControlPlaneClient dest) =>
        DeniedOutcome() with
        {
            Ok = true,
            Reason = null,
            Detail = null,
            DestClient = dest,
            DestInputLease = "dest-in",
            DestResizeLease = "dest-r",
            DestSubscribeId = "sub_dest",
        };

    private static AttachDestConnectCompletion DestCompletion(
        AttachLiveState live,
        IAttachCommandPort control,
        IAttachEndpoint destEndpoint,
        ulong generation)
    {
        var cube = live.Cubes[1];
        return new AttachDestConnectCompletion
        {
            Generation = generation,
            Cube = cube,
            Intent = new EndpointActivationIntent { EndpointId = cube.Id },
            Request = new CubesConnectRequest { Destination = cube },
            HostGeometry = AttachSession.BuildAttachHostGeometry(live),
            Control = control,
            SessionCt = CancellationToken.None,
            Outcome = DeniedOutcome() with { DestEndpoint = destEndpoint },
        };
    }

    private sealed class HangRetargeter : ICubesConnectRetargeter
    {
        public int Calls;
        public bool IgnoreCancellation;
        public TaskCompletionSource<CubesConnectRetargetOutcome> Gate { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CubesConnectRetargetOutcome> ConnectAsync(
            CubesConnectRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return IgnoreCancellation
                ? Gate.Task
                : Gate.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class HangEndpoint : IAttachEndpoint, IAsyncDisposable
    {
        public string Kind => AttachEndpointKinds.Unix;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlPlaneClient CreateClient() =>
            new("/tmp/hypa-pump-owner-hang.sock");

        public async ValueTask DisposeAsync()
        {
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class ImmediateRetargeter(CubesConnectRetargetOutcome outcome) : ICubesConnectRetargeter
    {
        public Task<CubesConnectRetargetOutcome> ConnectAsync(
            CubesConnectRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(outcome);
    }

    private sealed class HangStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return 0;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
