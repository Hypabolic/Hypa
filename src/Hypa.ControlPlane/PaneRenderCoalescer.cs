namespace Hypa.ControlPlane;

/// <summary>
/// Immediate coalescer decision for a caller that already holds the pane emit
/// gate. The coalescer does not launch the flush callback for this item.
/// </summary>
internal readonly record struct PaneRenderFlushDecision(
    string PaneId,
    long FeedGeneration);

/// <summary>
/// Coalesce one live VT paint per pane per render tick (16 ms).
/// Highest pending feed generation wins. Journal bytes stay per chunk.
/// Each pane keeps its own last-flush time. A shared timer only wakes
/// panes that are due. An input-write token makes the next Request
/// immediate inside the window. Burst chunks after that stay deferred.
/// Pending state is pane id, generation, and force. It does not store
// / PTY body copies.
/// </summary>
internal sealed class PaneRenderCoalescer : IDisposable
{
    internal static readonly TimeSpan RenderTick = TimeSpan.FromMilliseconds(16);

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastFlush = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inputForce = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly Func<string, long, bool, Task> _flush;
    private ITimer? _timer;
    private bool _scheduled;
    private bool _disposed;

    internal PaneRenderCoalescer(
        TimeProvider time,
        Func<string, long, bool, Task> flush)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(flush);
        _time = time;
        _flush = flush;
    }

    /// <summary>
    // / PTY thread after <c>process_pty_bytes</c>.
    /// <c>src/render_signal.rs:47-56</c> does not cancel a pending request
    /// when the next CSI ?2026h arrives. Always pending. Never flushes here.
    /// Never arms the timer: immediate compose stays under the pane emit gate.
    /// </summary>
    internal void RequestPty(string paneId, long feedGeneration)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;

        lock (_gate)
        {
            if (_disposed)
                return;

            var forceFull = false;
            if (_pending.TryGetValue(paneId, out var existing))
            {
                forceFull = existing.ForceFull;
                if (existing.FeedGeneration > feedGeneration)
                    feedGeneration = existing.FeedGeneration;
            }

            _pending[paneId] = new Pending(feedGeneration, forceFull);
        }
    }

    /// <summary>
    /// Request a live paint for <paramref name="paneId"/>. Snapshot panes
    // / pass generation only.
    /// inserts with no cancel. Keep max(existing, requested) generation
    /// like <see cref="RequestPty"/>. A delayed older closer must not
    /// replace a later feed-thread request.
    /// </summary>
    internal PaneRenderFlushDecision? Request(string paneId, long feedGeneration)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return null;

        lock (_gate)
        {
            if (_disposed)
                return null;

            var keepForce = false;
            if (_pending.TryGetValue(paneId, out var prev))
            {
                keepForce = prev.ForceFull;
                if (prev.FeedGeneration > feedGeneration)
                    feedGeneration = prev.FeedGeneration;
            }

            _pending[paneId] = new Pending(feedGeneration, keepForce);

            if (keepForce)
            {
                ArmSoonestUnlocked();
                return null;
            }

            var now = _time.GetUtcNow();
            var force = _inputForce.Remove(paneId);
            var immediate = force
                || !_lastFlush.TryGetValue(paneId, out var last)
                || now - last >= RenderTick;
            if (immediate)
            {
                _pending.Remove(paneId, out var due);
                _lastFlush[paneId] = now;
                return new PaneRenderFlushDecision(paneId, due.FeedGeneration);
            }

            ArmSoonestUnlocked();
            return null;
        }
    }

    /// <summary>
    /// Arm one next-request bypass after a successful pane input write.
    /// Does not flush pending work and does not stamp the rate window.
    /// Repeated writes before an echo stay one token.
    /// </summary>
    internal void MarkInputWritten(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            _inputForce.Add(paneId);
        }
    }

    /// <summary>
    // / Latest-wins origin Full. Never flushes on this call.
    /// <c>src/app/mod.rs:39</c> MIN_RENDER_INTERVAL 16 ms.
    /// </summary>
    internal void RequestOriginPaint(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_pending.TryGetValue(paneId, out var existing))
            {
                _pending[paneId] = existing with { ForceFull = true };
            }
            else
            {
                _pending[paneId] = new Pending(0, ForceFull: true);
            }

            ArmSoonestUnlocked();
        }
    }

    internal bool TryPeekPending(string paneId, out bool forceFull)
    {
        return TryPeekPending(paneId, out forceFull, out _);
    }

    internal bool TryPeekPending(string paneId, out bool forceFull, out long feedGeneration)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(paneId, out var pending))
            {
                forceFull = pending.ForceFull;
                feedGeneration = pending.FeedGeneration;
                return true;
            }
        }

        forceFull = false;
        feedGeneration = 0;
        return false;
    }

    private void ArmSoonestUnlocked()
    {
        if (_disposed || _pending.Count == 0)
            return;

        var now = _time.GetUtcNow();
        var remain = RenderTick;
        var any = false;
        foreach (var id in _pending.Keys)
        {
            if (!_lastFlush.TryGetValue(id, out var last))
            {
                remain = TimeSpan.Zero;
                any = true;
                break;
            }

            var due = RenderTick - (now - last);
            if (!any || due < remain)
                remain = due;
            any = true;
        }

        if (!any)
            return;
        if (remain < TimeSpan.Zero)
            remain = TimeSpan.Zero;

        _timer ??= _time.CreateTimer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _scheduled = true;
        _timer.Change(remain, Timeout.InfiniteTimeSpan);
    }

    internal async Task FlushPaneNowAsync(string paneId)
    {
        Pending? pending;
        lock (_gate)
        {
            if (!_pending.Remove(paneId, out var p))
                return;
            pending = p;
            _lastFlush[paneId] = _time.GetUtcNow();
        }

        await _flush(
                paneId,
                pending.Value.FeedGeneration,
                pending.Value.ForceFull)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Drop a pending paint for <paramref name="paneId"/> without posting.
    /// Caller holds the pane emit gate and will post immediately.
    /// </summary>
    internal void Cancel(string paneId)
    {
        lock (_gate)
            _pending.Remove(paneId);
    }

    /// <summary>
    /// Forget every per-pane rate-window, pending entry, and input token for a
    /// pane that no longer exists so closed panes cannot grow the coalescer
    /// for the process lifetime.
    /// </summary>
    internal void Forget(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            _pending.Remove(paneId);
            _lastFlush.Remove(paneId);
            _inputForce.Remove(paneId);
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
            _pending.Clear();
            _inputForce.Clear();
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    void IDisposable.Dispose() => Dispose();

    private void OnTick(object? _)
    {
        List<(string PaneId, Pending Pending)> batch;
        lock (_gate)
        {
            _scheduled = false;
            if (_disposed || _pending.Count == 0)
                return;

            batch = new List<(string, Pending)>(_pending.Count);
            var now = _time.GetUtcNow();
            List<string>? keep = null;
            foreach (var (id, pending) in _pending)
            {
                var due = !_lastFlush.TryGetValue(id, out var last)
                    || now - last >= RenderTick;
                if (!due)
                {
                    keep ??= new List<string>();
                    keep.Add(id);
                    continue;
                }

                batch.Add((id, pending));
                _lastFlush[id] = now;
            }

            if (keep is null)
                _pending.Clear();
            else
            {
                foreach (var (id, _) in batch)
                    _pending.Remove(id);
            }

            if (_pending.Count > 0)
                ArmSoonestUnlocked();
        }

        if (batch.Count > 0)
            _ = FlushBatchAsync(batch);
    }

    private async Task FlushBatchAsync(List<(string PaneId, Pending Pending)> batch)
    {
        foreach (var (paneId, pending) in batch)
        {
            try
            {
                await _flush(
                        paneId,
                        pending.FeedGeneration,
                        pending.ForceFull)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort live paint. The next tick or attach snapshot retries.
            }
        }

        lock (_gate)
        {
            if (_disposed || _scheduled || _pending.Count == 0 || _timer is null)
                return;
            ArmSoonestUnlocked();
        }
    }

    /// <summary>
    /// </summary>
    private readonly record struct Pending(
        long FeedGeneration,
        bool ForceFull);
}
