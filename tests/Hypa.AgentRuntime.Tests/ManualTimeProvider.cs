namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Test clock that fires <see cref="TimeProvider.CreateTimer"/> callbacks
/// when <see cref="Advance"/> crosses their due time. Product threads may create
/// timers, change them, and read the time while a test advances the clock, so one
/// lock guards the time and every timer. Callbacks run outside the lock.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow = new(2026, 9, 10, 4, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = [];

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _utcNow;
    }

    /// <summary>The earliest due time of an armed timer, or null when none is armed.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset? next = null;
                foreach (var timer in _timers)
                {
                    if (timer.Due is { } due && (next is null || due < next))
                        next = due;
                }

                return next;
            }
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        DateTimeOffset target;
        lock (_gate)
            target = _utcNow + delta;

        while (true)
        {
            ManualTimer? next = null;
            lock (_gate)
            {
                var nextDue = DateTimeOffset.MaxValue;
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
                next.Disarm();
            }

            next.Run();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _due;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        // Read and written under the owner lock.
        public DateTimeOffset? Due => _due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : _owner._utcNow + dueTime;
            }

            return true;
        }

        // The owner lock is held.
        public void Disarm() => _due = null;

        public void Run() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._gate)
                _due = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
