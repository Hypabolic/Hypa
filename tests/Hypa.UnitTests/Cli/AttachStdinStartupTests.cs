using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachStdinStartupTests
{
    [Fact]
    public async Task Timers_and_supervision_start_before_first_key()
    {
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var linked = new CancellationTokenSource();
        using var attempt = new CancellationTokenSource();
        using var gate = new SemaphoreSlim(1, 1);
        await using var stdin = new BlockingAttachStdin();
        var session = new AttachSession();
        var live = Live();
        var renewHits = 0;
        var renew = new LeaseRenewLoop(
            (_, _, _) =>
            {
                Interlocked.Increment(ref renewHits);
                return Task.CompletedTask;
            },
            period: TimeSpan.FromMilliseconds(40));
        renew.Track("lease-in");
        var beatHits = 0;
        var started = Task.Run(
            () => session.BeginAttachIoForTests(
                tty,
                live,
                new ControlPlaneClient("/tmp/hypa-stdin-unused.sock"),
                gate,
                linked,
                stdin,
                renew,
                _ =>
                {
                    Interlocked.Increment(ref beatHits);
                    return Task.CompletedTask;
                },
                attempt.Token,
                TimeSpan.FromMilliseconds(40)));

        var assigned = await Task.WhenAny(started, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(started, assigned);
        var loops = await started;
        Assert.False(loops.Input.IsCompleted);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while ((renewHits == 0 || beatHits == 0) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(renewHits > 0, "lease renew did not start before the first key");
        Assert.True(beatHits > 0, "control heartbeat did not start before the first key");
        Assert.False(loops.Input.IsCompleted);
        Assert.False(loops.Renew.IsCompleted);
        Assert.False(loops.Beat.IsCompleted);

        var supervised = Task.WhenAny(loops.Input, loops.Renew, loops.Beat, loops.TabBar);
        await Task.Delay(40);
        Assert.False(supervised.IsCompleted, "supervision returned before cancel");

        attempt.Cancel();
        await stdin.DisposeAsync();
        await Task.WhenAny(supervised, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(supervised.IsCompleted);
        await Drain(loops.Input);
        await Drain(loops.Renew);
        await Drain(loops.Beat);
        await Drain(loops.TabBar);
    }

    [Fact]
    public async Task Owned_producer_thread_stops_on_dispose()
    {
        using var stop = new CancellationTokenSource();
        var polls = 0;
        await using var producer = AttachStdinProducer.Start(
            fd: 0,
            stop.Token,
            pollReadable: (_, timeoutMs) =>
            {
                Interlocked.Increment(ref polls);
                Thread.Sleep(Math.Clamp(timeoutMs, 1, 20));
                return false;
            },
            tryRead: (_, _, _) => -1);
        Assert.True(producer.ThreadAlive);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (Volatile.Read(ref polls) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(polls > 0);
        await producer.DisposeAsync();
        Assert.False(producer.ThreadAlive);
    }

    [Fact]
    public async Task Read_input_assignment_returns_before_first_key()
    {
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var linked = new CancellationTokenSource();
        using var attempt = new CancellationTokenSource();
        using var gate = new SemaphoreSlim(1, 1);
        await using var stdin = new BlockingAttachStdin();
        var session = new AttachSession();
        var assigned = session.ReadInputForTests(
            tty,
            Live(),
            new ControlPlaneClient("/tmp/hypa-stdin-unused.sock"),
            gate,
            linked,
            stdin,
            attempt.Token);
        Assert.False(assigned.IsCompleted);
        await Task.Delay(40);
        Assert.False(assigned.IsCompleted);
        attempt.Cancel();
        await Drain(assigned);
    }

    private static AttachLiveState Live()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new SilentPort(), "w1", "t1", "p1", "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = "p1",
            InputLease = "lease-in",
            ResizeLease = "lease-r",
            ChromeEnabled = true,
        };
    }

    private static async Task Drain(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class BlockingAttachStdin : IAttachStdinSource
    {
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ThreadAlive => false;

        public async ValueTask<AttachStdinRead> ReadAsync(
            byte[] buffer,
            int timeoutMs,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutMs > 0)
                timeout.CancelAfter(timeoutMs);
            try
            {
                await _end.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                return AttachStdinRead.End;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return AttachStdinRead.Timeout;
            }
        }

        public ValueTask DisposeAsync()
        {
            _end.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SilentPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
    }
}
