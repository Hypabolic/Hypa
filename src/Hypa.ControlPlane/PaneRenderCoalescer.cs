using System.Diagnostics;
using Hypa.AgentRuntime.Application;

namespace Hypa.ControlPlane;

/// <summary>
/// Immediate coalescer decision for a caller that already holds the pane emit
/// gate. The coalescer does not launch the flush callback for this item. The
/// caller runs the flush and then calls
/// <see cref="PaneRenderCoalescer.CompleteFlush"/> with its outcome.
/// </summary>
internal readonly record struct PaneRenderFlushDecision(
    string PaneId,
    long FeedGeneration,
    long DispatchToken);

/// <summary>
/// What one coalesced live flush did.
/// </summary>
internal enum PaintOutcome
{
    /// <summary>The flush sent a frame (or bytes) to at least one observer.</summary>
    Posted = 0,

    /// <summary>The flush captured the pane and found nothing to send.</summary>
    NothingToPost = 1,

    /// <summary>
    /// The flush did not capture: shutdown, a lost occupant, no live observer,
    /// a stale feed generation, an open DEC 2026 block, or a capture failure
    /// that queued a retry.
    /// </summary>
    Skipped = 2,
}

/// <summary>
/// Coalesce one live VT paint per pane per render tick (16 ms).
/// Highest pending feed generation wins. Journal bytes stay per chunk.
/// Each pane keeps its own last-post time. Only a flush that posts stamps it.
/// A shared timer only wakes panes that are due. Keyboard input opens an echo
/// window of <see cref="EchoWindow"/> from the key. Each key gets its own
/// echo budget, also a key inside an open window. Inside the window, a
/// request is immediate until <see cref="EchoFlushCap"/> flushes posted for
/// the latest key. A flush that posts nothing is free. All panes together get at most
/// <see cref="ServerEchoCeiling"/> early flushes in one render tick. A pane
/// has at most one flush in progress, and an immediate flush paints only its
/// own pane.
/// Pending state is pane id, generation, and force. It does not store
/// PTY body copies.
/// </summary>
internal sealed class PaneRenderCoalescer : IDisposable
{
    internal static readonly TimeSpan RenderTick = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// How long one keyboard input keeps requests immediate, from the input.
    /// A post does not move the end. A later input does.
    /// <c>src/server/headless.rs:171</c> <c>INTERACTIVE_RENDER_WINDOW</c>.
    /// </summary>
    internal static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(32);

    /// <summary>
    /// The most immediate flushes that post for one key. Each key starts a
    /// new budget. A flush that posts nothing, or that skipped, does not
    /// count. This cap is a Hypa rule: herdr has only the server budget
    /// (<see cref="ServerEchoCeiling"/>).
    /// </summary>
    internal const int EchoFlushCap = 4;

    /// <summary>
    /// The most early flushes (flushes before the render tick of their pane)
    /// for all panes together in one render tick. Free flushes count too, so
    /// this is their limit.
    /// <c>src/server/headless.rs:170</c> <c>INTERACTIVE_RENDER_BUDGET</c>.
    /// </summary>
    internal const int ServerEchoCeiling = 16;

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastFlush = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new(StringComparer.Ordinal);

    private readonly Dictionary<string, DateTimeOffset> _echoUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _echoFlushes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _echoWindowId = new(StringComparer.Ordinal);

    // Per pane with a flush in progress: its dispatch token and the echo
    // window id at dispatch (0 when no window was open). Only that window's
    // budget pays for the flush. A completion with another token is stale.
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly Func<string, long, bool, Task<PaintOutcome>> _flush;
    private ITimer? _timer;
    private bool _scheduled;
    private bool _disposed;
    private Task _lastTickFlush = Task.CompletedTask;
    private long _lastEchoWindowId;
    private long _lastDispatchToken;

    // The server-wide early flush budget: the render tick number and the
    // early flushes in it.
    private long _ceilingEpoch = -1;
    private int _ceilingUsed;

    internal PaneRenderCoalescer(
        TimeProvider time,
        Func<string, long, bool, Task<PaintOutcome>> flush)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(flush);
        _time = time;
        _flush = flush;
    }

    /// <summary>
    /// PTY thread after <c>process_pty_bytes</c>.
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
            var since = Stopwatch.GetTimestamp();
            if (_pending.TryGetValue(paneId, out var existing))
            {
                forceFull = existing.ForceFull;
                since = existing.Since;
                if (existing.FeedGeneration > feedGeneration)
                    feedGeneration = existing.FeedGeneration;
            }

            _pending[paneId] = new Pending(feedGeneration, forceFull, since);
        }
    }

    /// <summary>
    /// Request a live paint for <paramref name="paneId"/>. Keep
    /// max(existing, requested) generation like <see cref="RequestPty"/>. A
    /// delayed older closer must not replace a later feed-thread request.
    /// A non-null result is an immediate flush that the caller runs under the
    /// pane emit gate. The caller then calls <see cref="CompleteFlush"/>.
    /// </summary>
    internal PaneRenderFlushDecision? Request(string paneId, long feedGeneration)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return null;

        lock (_gate)
        {
            if (_disposed)
                return null;

            // A new paint request ends a hold: the feed that asked for it
            // ended outside synchronized output, so the flush checks again.
            _notBefore.Remove(paneId);
            var keepForce = false;
            var since = Stopwatch.GetTimestamp();
            if (_pending.TryGetValue(paneId, out var prev))
            {
                keepForce = prev.ForceFull;
                since = prev.Since;
                if (prev.FeedGeneration > feedGeneration)
                    feedGeneration = prev.FeedGeneration;
            }

            var pending = new Pending(feedGeneration, keepForce, since);
            _pending[paneId] = pending;

            // A flush in progress captures the current VT. Its completion
            // arms the timer for this request if the request is still due.
            if (_inFlight.ContainsKey(paneId))
                return null;

            var now = _time.GetUtcNow();
            if (keepForce || DueAtUnlocked(paneId, pending, now) > now)
            {
                ArmSoonestUnlocked();
                return null;
            }

            _pending.Remove(paneId);
            NoteCoalesceWait(paneId, pending);
            var token = DispatchUnlocked(paneId, now);
            return new PaneRenderFlushDecision(paneId, feedGeneration, token);
        }
    }

    /// <summary>
    /// A live paint that the input barrier held back. It stays pending like
    /// <see cref="RequestPty"/>, and the timer is armed: the next due tick runs
    /// it even when no write end does. Never flushes on this call.
    /// </summary>
    internal void RequestHeld(string paneId, long feedGeneration)
    {
        RequestPty(paneId, feedGeneration);
        lock (_gate)
        {
            if (!_disposed)
                ArmSoonestUnlocked();
        }
    }

    /// <summary>
    /// Record the outcome of a flush that <see cref="Request"/> returned.
    /// The coalescer calls this itself for its timer flushes.
    /// <list type="bullet">
    /// <item>Only <see cref="PaintOutcome.Posted"/> stamps the render tick
    /// of the last post. The stamp is the completion time, so a flush that
    /// waited for the pane emit gate does not shorten the next interval.</item>
    /// <item>Inside an echo window, a flush that posts spends one unit of
    /// the echo budget. A flush that does not post is free: the next
    /// request inside the window is still immediate.</item>
    /// <item>A flush that does not post stamps the last attempt. Outside the
    /// echo window, a capture that found nothing, and a failed capture that
    /// queued a retry, wait one render tick before the next flush.</item>
    /// </list>
    /// </summary>
    internal void CompleteFlush(PaneRenderFlushDecision decision, PaintOutcome outcome) =>
        CompleteFlush(decision.PaneId, decision.DispatchToken, outcome);

    private void CompleteFlush(string paneId, long token, PaintOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            // A flush that started before Forget, or before Dispose, finds
            // no entry or a newer dispatch. It changes nothing.
            if (_disposed
                || !_inFlight.TryGetValue(paneId, out var inFlight)
                || inFlight.Token != token)
            {
                return;
            }

            _inFlight.Remove(paneId);
            var dispatchWindow = inFlight.EchoWindow;

            var now = _time.GetUtcNow();
            // Only a post spends the budget of the window that was open at
            // dispatch. A read that changes only colour state or the title
            // sends nothing and is free.
            if (outcome == PaintOutcome.Posted
                && dispatchWindow != 0
                && _echoWindowId.TryGetValue(paneId, out var window)
                && window == dispatchWindow)
            {
                _echoFlushes[paneId] = _echoFlushes.GetValueOrDefault(paneId) + 1;
            }

            // A post never moves the end of the echo window. Every flush
            // stamps the normal rhythm. Inside the window the echo path does
            // not read the rhythm, so a free flush does not delay the next
            // one there. When the budget or the server ceiling is spent, the
            // pane waits one render tick after its last flush.
            if (outcome == PaintOutcome.Posted)
                _lastFlush[paneId] = now;
            else
                _lastAttempt[paneId] = now;

            if (_pending.ContainsKey(paneId))
                ArmSoonestUnlocked();
        }
    }

    /// <summary>
    /// Keyboard input for <paramref name="paneId"/> was admitted. Open the
    /// echo window before the PTY write completes, or move its end to the
    /// admission time plus <see cref="EchoWindow"/>. The key gets a new echo
    /// budget of <see cref="EchoFlushCap"/> posts.
    /// </summary>
    internal void MarkInputAdmitted(string paneId) => OpenEchoWindow(paneId);

    /// <summary>
    /// A keyboard input write completed. A scheduling signal only: a pending
    /// paint that the open echo window makes due runs now. The window comes
    /// from the admission of the key. The write time does not move its end
    /// and does not open a new window, so a slow PTY write cannot give the
    /// same key a second budget.
    /// </summary>
    internal void MarkInputWritten(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (!_disposed && _pending.ContainsKey(paneId))
                ArmSoonestUnlocked();
        }
    }

    private void OpenEchoWindow(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            // Each key gets its own budget and its own window id, also a key
            // inside an open window. Keys less than one window apart keep the
            // window open, so a shared budget would make each echo after the
            // first few of a fast burst wait for the render tick. The id
            // makes sure that a flush dispatched for an earlier key does not
            // spend the budget of this key. The cap still limits the posts
            // for one key, and the server ceiling limits all panes.
            var now = _time.GetUtcNow();
            _echoFlushes[paneId] = 0;
            _echoWindowId[paneId] = ++_lastEchoWindowId;

            var until = now + EchoWindow;
            if (!_echoUntil.TryGetValue(paneId, out var open) || until > open)
                _echoUntil[paneId] = until;
            // A pending paint, or a pending origin Full, is now due at once.
            if (_pending.ContainsKey(paneId))
                ArmSoonestUnlocked();
        }
    }

    /// <summary>
    /// Ask for one live paint of <paramref name="paneId"/> at
    /// <paramref name="notBefore"/> or later. The shared timer fires it; this
    /// call adds no timer. A later <see cref="Request"/> ends the hold, and a
    /// pending origin Full ignores it. A time in the past is a normal
    /// rate-capped request.
    /// </summary>
    internal void RequestNotBefore(string paneId, long feedGeneration, DateTimeOffset notBefore)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (_disposed)
                return;

            var forceFull = false;
            var since = Stopwatch.GetTimestamp();
            if (_pending.TryGetValue(paneId, out var existing))
            {
                forceFull = existing.ForceFull;
                since = existing.Since;
                if (existing.FeedGeneration > feedGeneration)
                    feedGeneration = existing.FeedGeneration;
            }

            _pending[paneId] = new Pending(feedGeneration, forceFull, since);
            _notBefore[paneId] = notBefore;
            ArmSoonestUnlocked();
        }
    }

    /// <summary>
    /// Latest-wins origin Full. Never flushes on this call.
    /// <c>src/app/mod.rs:39</c> MIN_RENDER_INTERVAL 16 ms. An open echo
    /// window makes it due at once.
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
                _pending[paneId] = new Pending(0, ForceFull: true, Stopwatch.GetTimestamp());
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
        var soonest = DateTimeOffset.MaxValue;
        foreach (var (id, pending) in _pending)
        {
            var due = DueAtUnlocked(id, pending, now);
            if (due < soonest)
                soonest = due;
            if (soonest <= now)
                break;
        }

        // Every pending pane has a flush in progress. Each completion arms.
        if (soonest == DateTimeOffset.MaxValue)
            return;

        var remain = soonest - now;
        if (remain < TimeSpan.Zero)
            remain = TimeSpan.Zero;

        _timer ??= _time.CreateTimer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _scheduled = true;
        _timer.Change(remain, Timeout.InfiniteTimeSpan);
    }

    private bool EchoOpenUnlocked(string paneId, DateTimeOffset at) =>
        _echoUntil.TryGetValue(paneId, out var until) && at < until;

    /// <summary>The id of the echo window open at <paramref name="at"/>, or 0.</summary>
    private long EchoWindowAtUnlocked(string paneId, DateTimeOffset at) =>
        EchoOpenUnlocked(paneId, at) ? _echoWindowId.GetValueOrDefault(paneId) : 0;

    /// <summary>
    /// Mark a flush of <paramref name="paneId"/> in progress and return its
    /// token. A flush that the echo window makes earlier than the normal
    /// rhythm is an early flush. It uses one unit of the server ceiling, also
    /// when it posts nothing. A flush that the rhythm already allows is not
    /// early and uses no unit.
    /// </summary>
    private long DispatchUnlocked(string paneId, DateTimeOffset now)
    {
        var token = ++_lastDispatchToken;
        var window = EchoWindowAtUnlocked(paneId, now);
        if (EchoImmediateUnlocked(paneId, now) && RhythmDueUnlocked(paneId, now) > now)
            _ceilingUsed++;
        _inFlight[paneId] = new InFlight(token, window);
        return token;
    }

    /// <summary>
    /// The echo window of <paramref name="paneId"/> is open, its budget of
    /// posts is not spent, and the server ceiling has a unit left.
    /// </summary>
    private bool EchoImmediateUnlocked(string paneId, DateTimeOffset now) =>
        EchoOpenUnlocked(paneId, now)
        && _echoFlushes.GetValueOrDefault(paneId) < EchoFlushCap
        && CeilingLeftUnlocked(now);

    /// <summary>Start a new ceiling budget when a new render tick starts.</summary>
    private void RollCeilingUnlocked(DateTimeOffset now)
    {
        var epoch = now.UtcTicks / RenderTick.Ticks;
        if (epoch == _ceilingEpoch)
            return;
        _ceilingEpoch = epoch;
        _ceilingUsed = 0;
    }

    private bool CeilingLeftUnlocked(DateTimeOffset now)
    {
        RollCeilingUnlocked(now);
        return _ceilingUsed < ServerEchoCeiling;
    }

    /// <summary>
    /// The normal rhythm: one render tick after the last post, or after the
    /// last flush that did not post.
    /// </summary>
    private DateTimeOffset RhythmDueUnlocked(string paneId, DateTimeOffset now)
    {
        var due = now;
        if (_lastFlush.TryGetValue(paneId, out var last) && last + RenderTick > due)
            due = last + RenderTick;
        if (_lastAttempt.TryGetValue(paneId, out var idle) && idle + RenderTick > due)
            due = idle + RenderTick;
        return due;
    }

    /// <summary>
    /// The earliest time a pending pane may flush. A pane with a flush in
    /// progress is never due; the completion arms again. An open echo window
    /// makes the pane due now while its budget lasts, for a live paint and
    /// for a pending origin Full. Otherwise the pane waits one render tick
    /// after its last post, or after its last flush that did not post. A hold
    /// from <see cref="RequestNotBefore"/> delays a live paint, not an origin
    /// Full.
    /// </summary>
    private DateTimeOffset DueAtUnlocked(string paneId, Pending pending, DateTimeOffset now)
    {
        if (_inFlight.ContainsKey(paneId))
            return DateTimeOffset.MaxValue;

        // Inside the window the pane is due now while its budget and the
        // server ceiling last. Otherwise it keeps its normal rhythm.
        var due = EchoImmediateUnlocked(paneId, now) ? now : RhythmDueUnlocked(paneId, now);

        if (!pending.ForceFull
            && _notBefore.TryGetValue(paneId, out var notBefore)
            && notBefore > due)
        {
            due = notBefore;
        }

        return due;
    }

    /// <summary>
    /// Run the live paint that the input barrier held, now that its write
    /// ended. The held paint obeys the echo budget, the server ceiling and
    /// the render tick like any request. When it is due and no flush is in
    /// progress, it runs on this call with its own dispatch token, so its
    /// outcome counts like any other flush. Otherwise it stays pending: the
    /// completion of the flush in progress, or the timer that
    /// <see cref="RequestHeld"/> armed, runs it when it is due.
    /// </summary>
    internal async Task RunHeldAsync(string paneId)
    {
        Pending pending;
        long token;
        lock (_gate)
        {
            if (_disposed || !_pending.TryGetValue(paneId, out pending))
                return;
            // A flush in progress arms the timer at its completion.
            if (_inFlight.ContainsKey(paneId))
                return;

            var now = _time.GetUtcNow();
            if (DueAtUnlocked(paneId, pending, now) > now)
            {
                ArmSoonestUnlocked();
                return;
            }

            _pending.Remove(paneId);
            _notBefore.Remove(paneId);
            token = DispatchUnlocked(paneId, now);
        }

        NoteCoalesceWait(paneId, pending);

        var outcome = PaintOutcome.Skipped;
        try
        {
            outcome = await _flush(
                    paneId,
                    pending.FeedGeneration,
                    pending.ForceFull)
                .ConfigureAwait(false);
        }
        finally
        {
            CompleteFlush(paneId, token, outcome);
        }
    }

    /// <summary>
    /// Test seam: flush the pending paint of <paramref name="paneId"/> now,
    /// whatever its due time.
    /// </summary>
    internal async Task FlushPaneNowAsync(string paneId)
    {
        Pending pending;
        long token = 0;
        lock (_gate)
        {
            if (!_pending.Remove(paneId, out pending))
                return;
            _notBefore.Remove(paneId);
            if (!_inFlight.ContainsKey(paneId))
                token = DispatchUnlocked(paneId, _time.GetUtcNow());
        }

        NoteCoalesceWait(paneId, pending);

        var outcome = PaintOutcome.Skipped;
        try
        {
            outcome = await _flush(
                    paneId,
                    pending.FeedGeneration,
                    pending.ForceFull)
                .ConfigureAwait(false);
        }
        finally
        {
            if (token != 0)
                CompleteFlush(paneId, token, outcome);
        }
    }

    /// <summary>True when a live paint of <paramref name="paneId"/> is pending.</summary>
    internal bool HasPending(string paneId)
    {
        lock (_gate)
            return _pending.ContainsKey(paneId);
    }

    /// <summary>
    /// True when a live paint of <paramref name="paneId"/> is pending or a
    /// flush of it was dispatched and has not ended. A dispatched flush can
    /// still wait for the pane emit gate.
    /// </summary>
    internal bool HasPendingOrInFlight(string paneId)
    {
        lock (_gate)
            return _pending.ContainsKey(paneId) || _inFlight.ContainsKey(paneId);
    }

    /// <summary>
    /// Drop a pending paint for <paramref name="paneId"/> without posting.
    /// Caller holds the pane emit gate and will post immediately.
    /// </summary>
    internal void Cancel(string paneId)
    {
        lock (_gate)
        {
            _pending.Remove(paneId);
        }
    }

    /// <summary>
    /// Forget every per-pane rate window, pending entry, hold, and echo
    /// window for a pane that no longer exists so closed panes cannot grow
    /// the coalescer for the process lifetime.
    /// </summary>
    internal void Forget(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            _pending.Remove(paneId);
            _lastFlush.Remove(paneId);
            _lastAttempt.Remove(paneId);
            _notBefore.Remove(paneId);
            _echoUntil.Remove(paneId);
            _echoWindowId.Remove(paneId);
            _echoFlushes.Remove(paneId);
            _inFlight.Remove(paneId);
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
            _lastFlush.Clear();
            _lastAttempt.Clear();
            _notBefore.Clear();
            _echoUntil.Clear();
            _echoWindowId.Clear();
            _echoFlushes.Clear();
            _inFlight.Clear();
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    void IDisposable.Dispose() => Dispose();

    private void OnTick(object? _)
    {
        List<(string PaneId, Pending Pending, long Token)> batch;
        lock (_gate)
        {
            _scheduled = false;
            if (_disposed || _pending.Count == 0)
                return;

            // Check and dispatch one pane at a time. Each early dispatch uses
            // one unit of the server ceiling, so the next check sees it.
            var now = _time.GetUtcNow();
            List<(string PaneId, Pending Pending)> candidates = [.. _pending.Select(p => (p.Key, p.Value))];
            batch = new List<(string, Pending, long)>(candidates.Count);
            // The batch flushes one pane after the other. A pane with an open
            // echo window goes first, so its echo does not wait for the flush
            // of a bulk pane.
            var echoFirst = 0;
            foreach (var (id, pending) in candidates)
            {
                if (DueAtUnlocked(id, pending, now) > now)
                    continue;

                var echo = EchoOpenUnlocked(id, now);
                _pending.Remove(id);
                _notBefore.Remove(id);
                var item = (id, pending, DispatchUnlocked(id, now));
                if (echo)
                    batch.Insert(echoFirst++, item);
                else
                    batch.Add(item);
            }

            if (_pending.Count > 0)
                ArmSoonestUnlocked();
        }

        if (batch.Count > 0)
        {
            var flush = FlushBatchAsync(batch);
            lock (_gate)
                _lastTickFlush = flush;
        }
    }

    /// <summary>
    /// Test seam: the flush that the last timer tick started. A test that
    /// moves a fake clock awaits this to see the tick's paint.
    /// </summary>
    internal Task LastTickFlushForTests
    {
        get
        {
            lock (_gate)
                return _lastTickFlush;
        }
    }

    private async Task FlushBatchAsync(List<(string PaneId, Pending Pending, long Token)> batch)
    {
        foreach (var (paneId, pending, token) in batch)
        {
            var outcome = PaintOutcome.Skipped;
            NoteCoalesceWait(paneId, pending);
            try
            {
                outcome = await _flush(
                        paneId,
                        pending.FeedGeneration,
                        pending.ForceFull)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort live paint. The next tick or attach snapshot retries.
            }
            finally
            {
                CompleteFlush(paneId, token, outcome);
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
    /// The time from the first request of a pending paint to its flush, on the
    /// monotonic clock. A trace record only; the coalescer keeps no histogram.
    /// </summary>
    private static void NoteCoalesceWait(string paneId, Pending pending) =>
        AttachPathTrace.RecordHop(
            AttachPathTrace.DomainOutput,
            AttachPathTrace.StageCoalesceWait,
            paneId,
            Stopwatch.GetTimestamp() - pending.Since);

    /// <summary>
    /// One pending paint. <see cref="Since"/> is the monotonic time of the
    /// first request that the flush answers.
    /// </summary>
    private readonly record struct Pending(
        long FeedGeneration,
        bool ForceFull,
        long Since);

    private readonly record struct InFlight(
        long Token,
        long EchoWindow);
}
