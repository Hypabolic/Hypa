using System.Diagnostics;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Mux;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class MuxReadyTimeoutTests
{
    [Fact]
    public void ReadyBudgetIsFifteenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), MuxReadyBudget.Timeout);
        Assert.Equal(TimeSpan.FromMilliseconds(100), MuxReadyBudget.PollInterval);
    }

    [Fact]
    public void AttachSupervisorUsesTheReadyBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), new ProcessMuxSupervisor().ReadyTimeout);
    }

    [Fact]
    public async Task AttachSupervisorTimeoutKeepsTheNotReadyText()
    {
        var clock = new SteppingTimeProvider();
        var supervisor = new ProcessMuxSupervisor(readyTimeout: null, time: clock);
        var socketPath = MissingSocketPath();
        var logPath = LogPath(socketPath);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<MuxAttachException>(() =>
            supervisor.WaitForReadyAsync(socketPath, logPath, cts.Token));

        Assert.Equal(
            $"Mux server did not become ready at {socketPath}. " +
            $"It wrote no log at {logPath}. Run 'hypa mux serve' to see the startup error.",
            ex.Message);
        Assert.Equal(15_000 / 100, clock.Delays);
    }

    [Fact]
    public async Task AttachSupervisorHonorsInjectedTimeout()
    {
        var clock = new SteppingTimeProvider();
        var supervisor = new ProcessMuxSupervisor(TimeSpan.FromMilliseconds(200), clock);
        var socketPath = MissingSocketPath();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<MuxAttachException>(() =>
            supervisor.WaitForReadyAsync(socketPath, LogPath(socketPath), cts.Token));

        Assert.Equal(2, clock.Delays);
    }

    [Fact]
    public async Task AppBootstrapTimeoutKeepsTheNotReadyText()
    {
        var clock = new SteppingTimeProvider();
        var socketPath = MissingSocketPath();
        var logPath = LogPath(socketPath);
        const string bin = "hypa";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Hypa.App.MuxBootstrap.WaitForReadyAsync(
                socketPath,
                logPath,
                bin,
                cts.Token,
                readyTimeout: null,
                time: clock));

        Assert.Equal(
            $"Mux did not become ready at {socketPath}. See {logPath}. bin={bin}",
            ex.Message);
        Assert.Equal(15_000 / 100, clock.Delays);
    }

    [Fact]
    public async Task AppBootstrapHonorsInjectedTimeout()
    {
        var clock = new SteppingTimeProvider();
        var socketPath = MissingSocketPath();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Hypa.App.MuxBootstrap.WaitForReadyAsync(
                socketPath,
                LogPath(socketPath),
                "hypa",
                cts.Token,
                TimeSpan.FromMilliseconds(200),
                clock));

        Assert.Equal(2, clock.Delays);
    }

    [SkippableFact]
    public async Task AttachSupervisorExitedChildFailsBeforeTheBudget()
    {
        Skip.If(OperatingSystem.IsWindows(), "the mux child is a Unix process");
        var clock = new SteppingTimeProvider();
        var supervisor = new ProcessMuxSupervisor(readyTimeout: null, time: clock);
        var socketPath = MissingSocketPath();
        var logPath = LogPath(socketPath);
        using var child = StartExitedChild();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<MuxAttachException>(() =>
            supervisor.WaitForReadyAsync(socketPath, logPath, cts.Token, child));

        Assert.Equal(
            $"Mux server did not become ready at {socketPath}. " +
            $"It wrote no log at {logPath}. Run 'hypa mux serve' to see the startup error.",
            ex.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"elapsed {clock.Elapsed}");
    }

    [SkippableFact]
    public async Task AppBootstrapExitedChildFailsBeforeTheBudget()
    {
        Skip.If(OperatingSystem.IsWindows(), "the mux child is a Unix process");
        var clock = new SteppingTimeProvider();
        var socketPath = MissingSocketPath();
        var logPath = LogPath(socketPath);
        const string bin = "hypa";
        using var child = StartExitedChild();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Hypa.App.MuxBootstrap.WaitForReadyAsync(
                socketPath,
                logPath,
                bin,
                cts.Token,
                readyTimeout: null,
                time: clock,
                child: child));

        Assert.Equal(
            $"Mux did not become ready at {socketPath}. See {logPath}. bin={bin}",
            ex.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"elapsed {clock.Elapsed}");
    }

    private static Process StartExitedChild()
    {
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/true",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("failed to start the probe process");
        child.WaitForExit();
        Assert.True(child.HasExited);
        return child;
    }

    private static string MissingSocketPath() =>
        Path.Combine(Path.GetTempPath(), "hypa-mux-ready-" + Guid.NewGuid().ToString("N"), "missing.sock");

    private static string LogPath(string socketPath) =>
        Path.Combine(Path.GetDirectoryName(socketPath)!, "mux.log");

    /// <summary>
    /// Advances on each delay and runs timer callbacks on this thread.
    /// A full ready budget then finishes without waiting wall-clock time.
    /// </summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private readonly Queue<(TimerCallback Callback, object? State)> _pending = new();
        private readonly DateTimeOffset _start = new(2026, 9, 27, 4, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _utcNow;
        private int _depth;

        public SteppingTimeProvider() => _utcNow = _start;

        public int Delays { get; private set; }

        public TimeSpan Elapsed => _utcNow - _start;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
                _utcNow += dueTime;

            Delays++;
            if (Delays > 10_000)
                throw new InvalidOperationException("ready poll exceeded the deadline");

            if (_depth > 0)
            {
                _pending.Enqueue((callback, state));
                return new NoopTimer();
            }

            _depth++;
            try
            {
                callback(state);
                while (_pending.Count > 0)
                {
                    var next = _pending.Dequeue();
                    next.Callback(next.State);
                }
            }
            finally
            {
                _depth--;
            }

            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
