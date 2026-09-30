namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Test clock that fires <see cref="TimeProvider.CreateTimer"/> callbacks
/// when <see cref="Advance"/> crosses their due time.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 9, 10, 4, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = [];

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        var target = _utcNow + delta;
        while (true)
        {
            ManualTimer? next = null;
            DateTimeOffset nextDue = DateTimeOffset.MaxValue;
            foreach (var timer in _timers)
            {
                if (timer.Due is not { } due || due > target || due >= nextDue)
                    continue;
                next = timer;
                nextDue = due;
            }

            if (next is null)
            {
                _utcNow = target;
                return;
            }

            _utcNow = nextDue;
            next.Fire();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : _owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = null;
            _callback(_state);
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
