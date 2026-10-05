using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Cubes;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Cubes;
using Xunit;

namespace Hypa.AgentRuntime.Tests.Cubes;

public sealed class CubeShareSupervisorTests
{
    private static readonly CubeShareSettings Default = new();

    [Fact]
    public async Task Start_runs_the_listener_and_persists_share()
    {
        var launcher = new FakeLauncher();
        var store = new FakeStore();
        await using var share = new CubeShareSupervisor(launcher, store, new ManualTime());

        var status = await share.StartAsync(Default, CancellationToken.None);

        Assert.True(status.Enabled);
        Assert.Equal(CubeShareStates.Running, status.State);
        Assert.Equal(7443, status.Listen!.Port);
        Assert.Equal(0, status.Restarts);
        Assert.Equal(Default, store.Saved);
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public async Task Exited_listener_is_started_again_after_backoff()
    {
        var launcher = new FakeLauncher();
        var time = new ManualTime();
        await using var share = new CubeShareSupervisor(launcher, new FakeStore(), time);
        await share.StartAsync(Default, CancellationToken.None);

        launcher.Listeners[0].Exit("share listener exited with code 134");
        await Until(() => share.Status.State == CubeShareStates.Retrying);

        Assert.Contains("code 134", share.Status.Error, StringComparison.Ordinal);
        Assert.Equal(1, share.Status.Restarts);
        Assert.True(launcher.Listeners[0].Disposed);
        Assert.Equal(1, launcher.Launches);

        await Until(() => time.HasTimerDueWithin(CubeShareSupervisor.Backoff(1)));
        time.Advance(CubeShareSupervisor.Backoff(1));
        await Until(() => share.Status.State == CubeShareStates.Running);

        Assert.Equal(2, launcher.Launches);
        Assert.Equal(1, share.Status.Restarts);
        Assert.Null(share.Status.Error);
    }

    [Fact]
    public async Task Failed_start_reports_retrying_and_keeps_trying()
    {
        var launcher = new FakeLauncher();
        launcher.Failures.Enqueue("port 7443 is already in use");
        var time = new ManualTime();
        await using var share = new CubeShareSupervisor(launcher, new FakeStore(), time);

        var status = await share.StartAsync(Default, CancellationToken.None);

        Assert.True(status.Enabled);
        Assert.Equal(CubeShareStates.Retrying, status.State);
        Assert.Equal("port 7443 is already in use", status.Error);

        await Until(() => time.HasTimerDueWithin(CubeShareSupervisor.Backoff(1)));
        time.Advance(CubeShareSupervisor.Backoff(1));
        await Until(() => share.Status.State == CubeShareStates.Running);
        Assert.Equal(2, launcher.Launches);
    }

    [Fact]
    public async Task Stop_ends_the_listener_and_forgets_share()
    {
        var launcher = new FakeLauncher();
        var store = new FakeStore();
        await using var share = new CubeShareSupervisor(launcher, store, new ManualTime());
        await share.StartAsync(Default, CancellationToken.None);

        var status = await share.StopAsync(CancellationToken.None);

        Assert.False(status.Enabled);
        Assert.Equal(CubeShareStates.Stopped, status.State);
        Assert.True(launcher.Listeners[0].Disposed);
        Assert.Null(store.Saved);
        Assert.Equal(1, store.Clears);
    }

    [Fact]
    public async Task Mux_shutdown_ends_the_listener_but_keeps_share_for_the_next_mux()
    {
        var launcher = new FakeLauncher();
        var store = new FakeStore();
        var share = new CubeShareSupervisor(launcher, store, new ManualTime());
        await share.StartAsync(Default, CancellationToken.None);

        await share.DisposeAsync();

        Assert.True(launcher.Listeners[0].Disposed);
        Assert.Equal(Default, store.Saved);
        Assert.Equal(0, store.Clears);
    }

    [Fact]
    public async Task Resume_starts_the_share_the_last_mux_left_enabled()
    {
        var launcher = new FakeLauncher();
        var saved = new CubeShareSettings { Bind = "::", Port = 7500 };
        await using var share = new CubeShareSupervisor(
            launcher,
            new FakeStore { Saved = saved },
            new ManualTime());

        await share.ResumeAsync(CancellationToken.None);
        await Until(() => share.Status.State == CubeShareStates.Running);

        Assert.Equal(saved, launcher.LastSettings);
        Assert.Equal(saved, share.Status.Settings);
    }

    [Fact]
    public async Task Resume_without_saved_share_starts_nothing()
    {
        var launcher = new FakeLauncher();
        await using var share = new CubeShareSupervisor(launcher, new FakeStore(), new ManualTime());

        await share.ResumeAsync(CancellationToken.None);

        Assert.Equal(CubeShareStates.Stopped, share.Status.State);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task Start_with_the_running_settings_keeps_the_listener()
    {
        var launcher = new FakeLauncher();
        await using var share = new CubeShareSupervisor(launcher, new FakeStore(), new ManualTime());
        await share.StartAsync(Default, CancellationToken.None);

        await share.StartAsync(new CubeShareSettings(), CancellationToken.None);

        Assert.Equal(1, launcher.Launches);
        Assert.False(launcher.Listeners[0].Disposed);
    }

    [Fact]
    public async Task Start_with_new_settings_replaces_the_listener()
    {
        var launcher = new FakeLauncher();
        await using var share = new CubeShareSupervisor(launcher, new FakeStore(), new ManualTime());
        await share.StartAsync(Default, CancellationToken.None);

        var moved = new CubeShareSettings { Port = 7500 };
        var status = await share.StartAsync(moved, CancellationToken.None);

        Assert.True(launcher.Listeners[0].Disposed);
        Assert.Equal(2, launcher.Launches);
        Assert.Equal(moved, status.Settings);
        Assert.Equal(CubeShareStates.Running, status.State);
    }

    [Fact]
    public async Task Start_while_retrying_tries_again_without_waiting_out_the_backoff()
    {
        var launcher = new FakeLauncher();
        launcher.Failures.Enqueue("port 7443 is already in use");
        await using var share = new CubeShareSupervisor(launcher, new FakeStore(), new ManualTime());
        await share.StartAsync(Default, CancellationToken.None);
        Assert.Equal(CubeShareStates.Retrying, share.Status.State);

        var status = await share.StartAsync(Default, CancellationToken.None);

        Assert.Equal(CubeShareStates.Running, status.State);
        Assert.Equal(2, launcher.Launches);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(40, 30)]
    public void Backoff_doubles_up_to_thirty_seconds(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), CubeShareSupervisor.Backoff(failures));

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition was not met");
            await Task.Delay(5);
        }
    }

    private sealed class FakeStore : ICubeShareSettingsStore
    {
        public CubeShareSettings? Saved { get; set; }

        public int Clears { get; private set; }

        public CubeShareSettings? LoadEnabled() => Saved;

        public void SaveEnabled(CubeShareSettings settings) => Saved = settings;

        public void Clear()
        {
            Clears++;
            Saved = null;
        }
    }

    private sealed class FakeLauncher : ICubeShareListenerLauncher
    {
        private int _launches;

        public Queue<string> Failures { get; } = new();

        public List<FakeListener> Listeners { get; } = [];

        public CubeShareSettings? LastSettings { get; private set; }

        public int Launches => Volatile.Read(ref _launches);

        public Task<Result<ICubeShareListener, string>> LaunchAsync(
            CubeShareSettings settings,
            CancellationToken ct)
        {
            LastSettings = settings;
            lock (Failures)
            {
                Interlocked.Increment(ref _launches);
                if (Failures.TryDequeue(out var failure))
                    return Task.FromResult(Result<ICubeShareListener, string>.Fail(failure));
                var listener = new FakeListener(settings);
                Listeners.Add(listener);
                return Task.FromResult(Result<ICubeShareListener, string>.Ok(listener));
            }
        }
    }

    private sealed class FakeListener(CubeShareSettings settings) : ICubeShareListener
    {
        private readonly TaskCompletionSource<string> _exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CubeShareListen Listen { get; } = new()
        {
            Bind = settings.Bind,
            Port = settings.Port,
            CertificateSha256 = new string('a', 64),
        };

        public Task<string> Exited => _exited.Task;

        public bool Disposed { get; private set; }

        public void Exit(string detail) => _exited.TrySetResult(detail);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _exited.TrySetResult("disposed");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Timers fire only on <see cref="Advance"/>.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public bool HasTimerDueWithin(TimeSpan delta)
        {
            lock (_gate)
                return _timers.Any(t => t.DueAt <= _now + delta);
        }

        public void Advance(TimeSpan delta)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += delta;
                due = _timers.Where(t => t.DueAt <= _now).ToList();
                foreach (var timer in due)
                    _timers.Remove(timer);
            }

            foreach (var timer in due)
                timer.Fire();
        }

        private void Schedule(ManualTimer timer, TimeSpan dueTime)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return;
                timer.DueAt = _now + dueTime;
                _timers.Add(timer);
            }
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset DueAt { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.Schedule(this, dueTime);
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose() => owner.Schedule(this, Timeout.InfiniteTimeSpan);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
