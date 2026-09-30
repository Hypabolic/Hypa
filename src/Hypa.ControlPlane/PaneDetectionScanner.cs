namespace Hypa.ControlPlane;

/// <summary>
/// Coalesce one detection scan per pane per 300 ms tick after
/// <see cref="Mark"/>. Identified panes also recheck the foreground
/// bytes. Pending pane-shell clear rechecks on the 300 ms tick.
/// One batch runs at a time. Mark never arms a second tick while a
/// batch is in flight.
/// </summary>
internal sealed class PaneDetectionScanner : IDisposable
{
    internal static readonly TimeSpan DetectionTick = TimeSpan.FromMilliseconds(300);

    internal static readonly TimeSpan TickUnidentified = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _marked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Recheck> _rechecks = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly Action<string, int> _scan;
    private ITimer? _timer;
    private bool _scheduled;
    private DateTimeOffset _scheduledDue;
    private bool _inFlight;
    private bool _disposed;

    internal PaneDetectionScanner(TimeProvider time, Action<string, int> scan)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(scan);
        _time = time;
        _scan = scan;
    }

    /// <summary>
    /// Test hook: runs after the tick copies the marked set and before each scan.
    /// Forget cannot retract an id that is already in that batch.
    /// </summary>
    internal Action? AfterBatchCopied { get; set; }

    /// <summary>
    /// Record that <paramref name="paneId"/> has new output and arm the tick.
    /// Does not read the screen. Last occupant generation for a pane wins.
    /// </summary>
    internal void Mark(string paneId, int occupantGeneration = 0)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            _marked[paneId] = occupantGeneration;
            ArmUnlocked();
        }
    }

    /// <summary>
    /// bytes when restore, pane-shell clear, or identified recheck is due.
    /// </summary>
    internal void ScheduleRecheck(string paneId, int occupantGeneration, TimeSpan delay)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            var wait = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
            _rechecks[paneId] = new Recheck(occupantGeneration, _time.GetUtcNow() + wait);
            ArmUnlocked();
        }
    }

    internal void CancelRecheck(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
            _rechecks.Remove(paneId);
    }

    internal bool HasRecheck(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        lock (_gate)
            return _rechecks.ContainsKey(paneId);
    }

    /// <summary>
    /// Force one scan now and drop a pending mark for <paramref name="paneId"/>.
    /// Tests use this. Process exit calls the scan body directly.
    /// </summary>
    internal void ScanNow(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        var generation = 0;
        lock (_gate)
        {
            if (_disposed)
                return;
            _marked.Remove(paneId, out generation);
        }

        _scan(paneId, generation);
    }

    /// <summary>
    /// Drop a pending mark so a closed or replaced pane cannot grow the set.
    /// Does not retract an id already copied into an in-flight batch.
    /// </summary>
    internal void Forget(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            _marked.Remove(paneId);
            _rechecks.Remove(paneId);
        }
    }

    internal void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _scheduled = false;
            _inFlight = false;
            _marked.Clear();
            _rechecks.Clear();
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    void IDisposable.Dispose() => Dispose();

    private void ArmUnlocked()
    {
        if (_disposed || _inFlight)
            return;
        var delay = NextDelayUnlocked();
        if (delay is null)
            return;

        var due = _time.GetUtcNow() + delay.Value;
        if (_scheduled && due >= _scheduledDue)
            return;

        _timer ??= _time.CreateTimer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _scheduled = true;
        _scheduledDue = due;
        _timer.Change(delay.Value, Timeout.InfiniteTimeSpan);
    }

    private TimeSpan? NextDelayUnlocked()
    {
        if (_marked.Count > 0)
            return DetectionTick;
        if (_rechecks.Count == 0)
            return null;

        var now = _time.GetUtcNow();
        var minDue = DateTimeOffset.MaxValue;
        foreach (var rec in _rechecks.Values)
        {
            if (rec.Due < minDue)
                minDue = rec.Due;
        }

        var wait = minDue - now;
        return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private void OnTick(object? _)
    {
        List<(string PaneId, int OccupantGeneration)> batch;
        lock (_gate)
        {
            if (_disposed)
            {
                _scheduled = false;
                _scheduledDue = default;
                return;
            }

            // A System TimeProvider callback can overlap a long batch.
            // Leave _scheduled alone so the in-flight body still owns rearm.
            if (_inFlight)
                return;

            _scheduled = false;
            _scheduledDue = default;
            if (_marked.Count == 0 && _rechecks.Count == 0)
                return;

            _inFlight = true;
            batch = new List<(string, int)>(_marked.Count + _rechecks.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in _marked)
            {
                batch.Add((kv.Key, kv.Value));
                seen.Add(kv.Key);
            }

            _marked.Clear();
            var now = _time.GetUtcNow();
            List<string>? due = null;
            foreach (var kv in _rechecks)
            {
                if (kv.Value.Due > now)
                    continue;
                due ??= [];
                due.Add(kv.Key);
            }

            if (due is not null)
            {
                foreach (var id in due)
                {
                    var rec = _rechecks[id];
                    _rechecks.Remove(id);
                    if (seen.Add(id))
                        batch.Add((id, rec.Generation));
                }
            }
        }

        try
        {
            AfterBatchCopied?.Invoke();
            foreach (var (paneId, occupantGeneration) in batch)
            {
                try
                {
                    _scan(paneId, occupantGeneration);
                }
                catch
                {
                    // Best-effort. The scan body already logs.
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = false;
                if (!_disposed && !_scheduled)
                    ArmUnlocked();
            }
        }
    }

    private readonly record struct Recheck(int Generation, DateTimeOffset Due);
}
