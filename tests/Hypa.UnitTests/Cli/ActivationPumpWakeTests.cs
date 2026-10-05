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
        var time = new ManualPumpTime();
        var loop = AttachSession.ActivationPumpLoopAsync(live, tty: null, stop.Token, time);
        try
        {
            Assert.Equal(0, Volatile.Read(ref live.ActivationPumpTicks));
            AttachSession.EnqueueActivationCompletion(live, Completion());
            await WaitUntil(() => Volatile.Read(ref live.ActivationPumpTicks) >= 1,
                "queued completion did not wake the pump while the timer was held");
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
        var time = new ManualPumpTime();
        var loop = AttachSession.ActivationPumpLoopAsync(live, tty: null, stop.Token, time);
        try
        {
            Assert.Equal(0, Volatile.Read(ref live.ActivationPumpTicks));
            Assert.True(await live.ActivationPumpGate.WaitAsync(TimeSpan.FromSeconds(2)));
            try
            {
                for (var i = 0; i < 100; i++)
                    AttachSession.EnqueueActivationCompletion(live, Completion());
                await WaitUntil(() => Volatile.Read(ref live.ActivationPumpTicks) >= 1,
                    "burst did not wake the pump while the timer was held");
                Assert.Equal(1, Volatile.Read(ref live.ActivationPumpTicks));
            }
            finally
            {
                live.ActivationPumpGate.Release();
            }

            await WaitUntil(() =>
            {
                lock (live.ActivationGate)
                    return live.ActivationCompletions.Count == 0
                        && live.ActivationPumpWake.CurrentCount == 0;
            }, "pump did not drain the completion burst");
        }
        finally
        {
            stop.Cancel();
            await Drain(loop);
            Assert.InRange(Volatile.Read(ref live.ActivationPumpTicks), 1, 2);
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
        var time = new ManualPumpTime();
        var loop = AttachSession.ActivationPumpLoopAsync(live, tty: null, stop.Token, time);
        try
        {
            Assert.Equal(0, Volatile.Read(ref live.ActivationPumpTicks));
            Assert.NotNull(live.PendingActivation);
            time.Tick();
            await WaitUntil(() => live.PendingActivation is null,
                "timer did not expire the activation without a wake");
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
        var time = new ManualPumpTime();
        var loop = AttachSession.ActivationPumpLoopAsync(live, tty: null, stop.Token, time);
        try
        {
            Assert.Equal(0, Volatile.Read(ref live.ActivationPumpTicks));
            var payload = Encoding.UTF8.GetBytes(
                "{\"event\":\"runtime.event\",\"params\":{\"type\":\"ping\"}}\n");
            await serverSocket.GetStream().WriteAsync(payload);
            await WaitUntil(() => Volatile.Read(ref live.ActivationPumpTicks) >= 1,
                "destination line did not wake the pump while the timer was held");
        }
        finally
        {
            stop.Cancel();
            await Drain(loop);
            listener.Stop();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Assert.True(condition(), failure);
    }

    // The periodic timer can only fire when the test explicitly ticks it.
    // A missing wake therefore fails even if the worker is scheduled late.
    private sealed class ManualPumpTime : TimeProvider
    {
        private ManualTimer? _timer;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(AttachSession.SidebarGitTickPeriod, dueTime);
            Assert.Equal(dueTime, period);
            Assert.Null(_timer);
            return _timer = new ManualTimer(callback, state);
        }

        public void Tick() => _timer!.Fire();

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;

            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                    callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
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
