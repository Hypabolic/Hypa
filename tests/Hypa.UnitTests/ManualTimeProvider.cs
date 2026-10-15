namespace Hypa.UnitTests;

/// <summary>
/// A clock that moves only when a test calls <see cref="Advance"/>.
/// A timer that is due runs its callback on the thread that advances the clock.
/// A test waits for the code under test to arm a timer, then advances past it.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly SemaphoreSlim _armed = new(0);
    private DateTimeOffset _utcNow = new(2026, 9, 27, 4, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _utcNow;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Returns when the code under test has armed one more timer. A run that
    /// arms no timer fails at the hang guard.
    /// </summary>
    public async Task WaitForArmedTimerAsync()
    {
        if (!await _armed.WaitAsync(TestWait.HangGuard))
            throw new TimeoutException("The code under test did not arm a timer.");
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _utcNow += by;
            due = _timers.Where(timer => timer.IsDue(_utcNow)).ToList();
        }

        foreach (var timer in due)
            timer.Fire(_utcNow);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    _dueAt = null;
                    return true;
                }

                _dueAt = owner._utcNow + dueTime;
            }

            owner._armed.Release();
            return true;
        }

        public bool IsDue(DateTimeOffset now) => _dueAt is { } due && due <= now;

        public void Fire(DateTimeOffset now)
        {
            lock (owner._gate)
            {
                if (_dueAt is null)
                    return;
                _dueAt = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero
                    ? null
                    : now + _period;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _dueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
