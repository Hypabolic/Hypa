using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class ActivationPumpWakeTests
{
    [Fact]
    public async Task QueuedCompletionStartsPumpTickBeforeTimer()
    {
        var live = Live();
        using var stop = new CancellationTokenSource();
        var loop = AttachSession.ActivationPumpLoopAsync(live, tty: null, stop.Token);
        try
        {
            await Task.Delay(40);
            Assert.Equal(0, Volatile.Read(ref live.ActivationPumpTicks));
            var started = Stopwatch.StartNew();
            AttachSession.EnqueueActivationCompletion(live, Completion());
            while (Volatile.Read(ref live.ActivationPumpTicks) < 1
                && started.Elapsed < TimeSpan.FromMilliseconds(20))
                await Task.Yield();
            Assert.True(
                Volatile.Read(ref live.ActivationPumpTicks) >= 1,
                "pump tick did not start");
            Assert.True(
                started.Elapsed < TimeSpan.FromMilliseconds(20),
                $"pump tick started after {started.Elapsed.TotalMilliseconds:0} ms");
        }
        finally
        {
            stop.Cancel();
            await Drain(loop);
        }
    }

    [Fact]
    public async Task BurstOfSignalsCausesBoundedPumpTicks()
    {
        var live = Live();
        using var stop = new CancellationTokenSource();
        var loop = AttachSession.ActivationPumpLoopAsync(live, tty: null, stop.Token);
        try
        {
            await Task.Delay(40);
            Assert.Equal(0, Volatile.Read(ref live.ActivationPumpTicks));
            Assert.True(await live.ActivationPumpGate.WaitAsync(TimeSpan.FromSeconds(2)));
            try
            {
                for (var i = 0; i < 100; i++)
                    AttachSession.EnqueueActivationCompletion(live, Completion());
                var entered = DateTime.UtcNow.AddMilliseconds(80);
                while (Volatile.Read(ref live.ActivationPumpTicks) < 1 && DateTime.UtcNow < entered)
                    await Task.Yield();
                Assert.Equal(1, Volatile.Read(ref live.ActivationPumpTicks));
            }
            finally
            {
                live.ActivationPumpGate.Release();
            }

            await Task.Delay(40);
            Assert.InRange(Volatile.Read(ref live.ActivationPumpTicks), 1, 3);
        }
        finally
        {
            stop.Cancel();
            await Drain(loop);
        }
    }

    [Fact]
    public async Task PhaseTimeoutFiresWhenNoSignalArrives()
    {
        var live = Live();
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.RestoringSource(
                "client-shell-surface:9:rollback-source-on",
                null,
                new EndpointActivationEvidence()),
            SourceLease(),
            TargetLease(),
            deadline: DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(5));
        using var stop = new CancellationTokenSource();
        var loop = AttachSession.ActivationPumpLoopAsync(live, tty: null, stop.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (live.PendingActivation is not null && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.Null(live.PendingActivation);
            Assert.Contains(
                AttachEndpointUserCopy.ActivationTimeout,
                live.StatusError,
                StringComparison.Ordinal);
            Assert.True(Volatile.Read(ref live.ActivationPumpTicks) >= 1);
        }
        finally
        {
            stop.Cancel();
            await Drain(loop);
        }
    }

    [Fact]
    public async Task DestinationLineStartsPumpTickBeforeTimer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await accept;
        await using var client = ControlPlaneClient.FromConnectedStream(clientSocket.GetStream());
        await client.ConnectAsync();
        var live = Live();
        AttachSession.ArmActivationPumpForDestClient(live, client);
        using var stop = new CancellationTokenSource();
        var loop = AttachSession.ActivationPumpLoopAsync(live, tty: null, stop.Token);
        try
        {
            await Task.Delay(40);
            Assert.Equal(0, Volatile.Read(ref live.ActivationPumpTicks));
            // The wake must beat the pump timer. Half its period proves that
            // without a wall-clock bound that scheduler jitter on a shared CI
            // runner can exceed (a 20 ms bound failed at 30 ms on macOS).
            var beforeTimer = AttachSession.SidebarGitTickPeriod / 2;
            var started = Stopwatch.StartNew();
            var payload = Encoding.UTF8.GetBytes(
                "{\"event\":\"runtime.event\",\"params\":{\"type\":\"ping\"}}\n");
            await serverSocket.GetStream().WriteAsync(payload);
            while (Volatile.Read(ref live.ActivationPumpTicks) < 1
                && started.Elapsed < beforeTimer)
                await Task.Yield();
            Assert.True(
                Volatile.Read(ref live.ActivationPumpTicks) >= 1,
                "destination line did not wake the pump");
            Assert.True(
                started.Elapsed < beforeTimer,
                $"destination line woke the pump after {started.Elapsed.TotalMilliseconds:0} ms, not before the {AttachSession.SidebarGitTickPeriod.TotalMilliseconds:0} ms timer");
        }
        finally
        {
            stop.Cancel();
            await Drain(loop);
            listener.Stop();
        }
    }

    private static AttachLiveState Live() =>
        MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());

    private static EndpointActivationRpcCompletion Completion() =>
        new EndpointActivationRpcCompletion.Control(
            "remote",
            7,
            new AttachControlResult
            {
                RequestId = "client-shell-surface:9:on",
                BootId = "remote-boot",
                ProjectionRevision = 1,
                GeometryRevision = 1,
            });

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

    private static async Task Drain(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
