using Hypa.Cli.Attach.Latency;

namespace Hypa.Cli.Attach.Prediction;

/// <summary>
/// Predicts the local echo of typed keys for the focused pane (ADR 0018).
/// <para>
/// The predictor is pure: it does no IO, and it reads time from the injected
/// <see cref="TimeProvider"/>. It keeps an overlay of predictions over the
/// last frame of one pane. The attach session calls it from the stdin
/// thread, the sender thread and the render thread, so one lock guards its
/// state.
/// </para>
/// <para>
/// Prediction changes only what the client paints. It never changes the
/// bytes that the client sends.
/// </para>
/// </summary>
internal sealed class EchoPredictor : IInputSeqObserver
{
    /// <summary>
    /// The client judges a prediction only after <c>input_ack</c> covered it
    /// for this time. Before that, a frame that differs is pending: on a cube
    /// the ack can arrive before the body with the echo.
    /// </summary>
    public static readonly TimeSpan JudgeDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// A prediction that gets no ack in this time deletes its epoch. Without
    /// this rule, a shown prediction of a key that the application ignores
    /// would stay drawn, because no frame comes to correct it.
    /// </summary>
    public static readonly TimeSpan AckExpiry = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A DEC 2026 block older than this no longer stops prediction. The mux
    /// paints a held block after the same time, and Ghostty clears the mode
    /// 1 s after a block starts (<c>termio/Thread.zig</c> <c>sync_reset_ms</c>).
    /// A program that ended inside a block leaves the mode set in the pane
    /// VT, and the pane must not lose prediction. A frame does not show the
    /// end of one block and the start of the next when no frame came between
    /// them. The mux also keeps the first start then (a feed that ends inside
    /// a block keeps the start), and it paints the frame after 1 s. The
    /// predictor thus uses the same start as the mux. This rule stays until
    /// the mux clears the mode at the end of its hold (H-390).
    /// </summary>
    public static readonly TimeSpan SynchronizedOutputStale = TimeSpan.FromSeconds(1);

    private const double SrttGain = 0.125;
    private const int MaxAssignments = 256;
    private const int MaxPredictions = 512;

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly IEchoDisplayPolicy _policy;
    private readonly AttachLatencyMeter? _meter;
    private readonly List<PredictionEpoch> _epochs = [];
    private readonly Queue<(long MaxMark, long? Seq)> _assignments = new();
    private readonly Queue<(long Seq, long SentAt)> _sent = new();
    private long _nextMark;
    private long _lastObservedMark;
    private long _nextOrder;
    private long _nextEpoch;
    private string? _paneId;
    private int _cols;
    private int _rows;
    private int _occupant;
    private PredictionEpoch? _current;
    // The unpredicted keys that are not yet acked. While one waits, the
    // cursor is unknown and no key is predicted. The first one retires the
    // older predictions when its ack comes; the last one ends the wait.
    private (long Order, long Mark, long? Seq)? _barrierFirst;
    private (long Order, long Mark, long? Seq)? _barrier;
    private long _lastJudgedAt;
    private long _ackedThrough;

    // Marks of keys that the sender sent to another pane than the one they
    // were typed on. Their observe makes no prediction.
    private readonly SortedSet<long> _foreignMarks = [];

    // The pane that had the focus when each mark was reserved, in mark order.
    private readonly Queue<(long Mark, string Pane)> _markPanes = new();
    private bool _noAck;
    private long? _syncSince;
    private bool _cursorHidden;
    private double? _srttTicks;
    private bool _srttFromOldPane;
    private long _version;
    private long _predictedShown;
    private long _mispredictedShown;
    private long _confirmedByGlyph;
    private long _confirmedByCursor;
    private long _retired;
    private long _mismatches;
    private long _expired;
    private long _resets;
    private long _made;
    private long _unpredicted;
    private long _barrierSkips;
    private readonly long[] _resetCalls = new long[Enum.GetValues<PredictionResetReason>().Length];

    public EchoPredictor(TimeProvider time, IEchoDisplayPolicy policy, AttachLatencyMeter? meter = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(policy);
        _time = time;
        _policy = policy;
        _meter = meter;
    }

    /// <summary>A predictor that predicts nothing.</summary>
    public static EchoPredictor Disabled() => new(TimeProvider.System, new NeverEchoDisplayPolicy());

    /// <summary>False when the policy is <c>never</c>: the predictor then does no work.</summary>
    public bool Predicts => _policy.Predicts;

    /// <summary>The pane of the current predictions, or null.</summary>
    public string? PaneId
    {
        get
        {
            lock (_gate)
                return _paneId;
        }
    }

    /// <summary>
    /// A new input mark. The stdin thread takes one for each pane payload
    /// before it queues the payload, and gives the same mark to
    /// <see cref="Observe"/> after the queue accepts it.
    /// </summary>
    public long ReserveMark() => Interlocked.Increment(ref _nextMark);

    /// <summary>
    /// A new input mark for keys typed while <paramref name="paneId"/> has the
    /// focus. When the sender later sends them to another pane, they are
    /// foreign: no prediction of them can confirm.
    /// </summary>
    public long ReserveMark(string? paneId)
    {
        var mark = Interlocked.Increment(ref _nextMark);
        if (_policy.Predicts && !string.IsNullOrEmpty(paneId))
        {
            lock (_gate)
            {
                _markPanes.Enqueue((mark, paneId));
                while (_markPanes.Count > MaxAssignments)
                    _markPanes.Dequeue();
            }
        }

        return mark;
    }

    /// <summary>
    /// Records the predictions of one payload that the client queued for
    /// <paramref name="paneId"/>. <paramref name="frame"/> is the stored frame
    /// of the pane. <paramref name="allowed"/> is false when the modal overlay
    /// owns the screen or the pane is scrolled back. <paramref name="agentPane"/>
    /// is true when the pane runs a detected agent: the alternate screen then
    /// does not turn prediction off. Returns true when the shown predictions
    /// may have changed, so the caller paints.
    /// </summary>
    public bool Observe(
        string paneId,
        ReadOnlySpan<byte> bytes,
        long mark,
        AssembledSnapshot? frame,
        bool paste,
        bool allowed,
        bool agentPane = false)
    {
        if (!_policy.Predicts || bytes.IsEmpty || string.IsNullOrEmpty(paneId))
            return false;
        lock (_gate)
        {
            var before = _version;
            _lastObservedMark = Math.Max(_lastObservedMark, mark);
            // The sender already sent these keys to another pane.
            if (_foreignMarks.Remove(mark))
                return false;
            if (!string.Equals(paneId, _paneId, StringComparison.Ordinal))
                ChangePaneLocked(paneId);

            var reason = paste ? PredictionResetReason.Paste
                : !allowed ? PredictionResetReason.NotAllowed
                : _noAck ? PredictionResetReason.NoAck
                : frame is null ? PredictionResetReason.NotAllowed
                : OffReasonLocked(frame, agentPane, _time.GetTimestamp());
            if (reason is { } off)
            {
                ResetLocked(off);
                SetBarrierLocked(mark);
                BindMarksLocked();
                return _version != before;
            }

            if (frame!.Cols != _cols || frame.Rows != _rows || frame.OccupantGeneration != _occupant)
            {
                ResetLocked(PredictionResetReason.Resize);
                SetBase(frame);
            }

            var now = _time.GetTimestamp();
            foreach (var key in EchoKeyReader.Read(bytes, frame.ApplicationCursor))
            {
                if (BarrierActiveLocked())
                {
                    // The cursor is unknown until the unpredicted key is acked.
                    _barrierSkips++;
                    SetBarrierLocked(mark);
                    continue;
                }

                _barrier = null;
                _barrierFirst = null;
                var prediction = Predict(key, frame, mark, now);
                if (prediction is null || CountLocked() >= MaxPredictions)
                {
                    _unpredicted++;
                    _current = null;
                    SetBarrierLocked(mark);
                    continue;
                }

                if (_current is null)
                {
                    _current = new PredictionEpoch(++_nextEpoch);
                    _epochs.Add(_current);
                }

                _current.Predictions.Add(prediction);
                _made++;
                if (_current.Confirmed)
                    _version++;
            }

            BindMarksLocked();
            return _version != before;
        }
    }

    /// <summary>
    /// Keys of <paramref name="paneId"/> went out, but the frame they were
    /// typed on is not known. They are unpredicted keys: no prediction starts
    /// until their ack. Returns true when the shown predictions may have
    /// changed.
    /// </summary>
    public bool ObserveUnknownBase(string paneId, long mark, bool paste)
    {
        if (!_policy.Predicts || string.IsNullOrEmpty(paneId))
            return false;
        lock (_gate)
        {
            var before = _version;
            _lastObservedMark = Math.Max(_lastObservedMark, mark);
            if (_foreignMarks.Remove(mark))
                return false;
            if (!string.Equals(paneId, _paneId, StringComparison.Ordinal))
                ChangePaneLocked(paneId);

            // A paste moves the real cursor away from every prediction.
            if (paste)
                ResetLocked(PredictionResetReason.Paste);
            SetBarrierLocked(mark);
            BindMarksLocked();
            return _version != before;
        }
    }

    /// <summary>
    /// The keys with marks up to <paramref name="maxMark"/> went out under
    /// <paramref name="seq"/>. A null seq means the peer sends no ack, so no
    /// prediction can confirm: the predictor deletes its epochs.
    /// </summary>
    public void OnSeqAssigned(string paneId, long? seq, long maxMark)
    {
        if (!_policy.Predicts || maxMark <= 0)
            return;
        lock (_gate)
        {
            if (seq is null)
            {
                _noAck = true;
                ResetLocked(PredictionResetReason.NoAck);
                _assignments.Clear();
                return;
            }

            _noAck = false;
            // Compare the destination with the pane each key was typed on,
            // not with the last predicted pane: the focus can move between
            // the reserve, this send and the observe.
            var foreignOnPredicted = false;
            while (_markPanes.Count > 0 && _markPanes.Peek().Mark <= maxMark)
            {
                var (mark, typedOn) = _markPanes.Dequeue();
                if (string.Equals(typedOn, paneId, StringComparison.Ordinal))
                    continue;
                // This key was typed on another pane than its destination.
                // No prediction of it can confirm; an observe that comes
                // later skips it. Keys of the batch that match keep the seq.
                _foreignMarks.Add(mark);
                // A foreign key typed on the predicted pane, or sent into it,
                // changes what that pane shows.
                foreignOnPredicted |= string.Equals(typedOn, _paneId, StringComparison.Ordinal)
                    || string.Equals(paneId, _paneId, StringComparison.Ordinal);
            }

            while (_foreignMarks.Count > MaxAssignments)
                _foreignMarks.Remove(_foreignMarks.Min);
            if (foreignOnPredicted)
                ResetLocked(PredictionResetReason.FocusChange);

            if (_paneId is not null && !string.Equals(paneId, _paneId, StringComparison.Ordinal))
            {
                // Keys typed on a newly focused pane went out before their
                // observe. Start on that pane, as Observe would.
                ChangePaneLocked(paneId);
                _assignments.Clear();
            }
            _assignments.Enqueue((maxMark, seq));
            while (_assignments.Count > MaxAssignments)
                _assignments.Dequeue();
            _sent.Enqueue((seq.Value, _time.GetTimestamp()));
            while (_sent.Count > MaxAssignments)
                _sent.Dequeue();
            BindMarksLocked();
        }
    }

    public void OnInputDropped()
    {
        if (!_policy.Predicts)
            return;
        lock (_gate)
        {
            ResetLocked(PredictionResetReason.InputDropped);
            _barrier = null;
            _barrierFirst = null;
            _assignments.Clear();
            _sent.Clear();
            _ackedThrough = 0;
        }
    }

    /// <summary>Deletes every epoch. Returns true when shown predictions may have changed.</summary>
    public bool Reset(PredictionResetReason reason)
    {
        if (!_policy.Predicts)
            return false;
        lock (_gate)
        {
            var before = _version;
            ResetLocked(reason);
            return _version != before;
        }
    }

    /// <summary>
    /// Keys went to the pane with no seq, for example on a path without the
    /// input sender. Their effect is unknown and they can never confirm.
    /// </summary>
    public bool NoteUnsequencedInput()
    {
        if (!_policy.Predicts)
            return false;
        lock (_gate)
        {
            var before = _version;
            ResetLocked(PredictionResetReason.NoSeq);
            // A barrier with no seq never clears by an ack. The next frame of
            // the pane clears it, so the next key predicts from that frame.
            SetBarrierLocked(0);
            return _version != before;
        }
    }

    /// <summary>
    /// Judges the predictions against a frame of the pane that the client
    /// applied. <paramref name="inputAck"/> is the ack of the body, or null.
    /// <paramref name="agentPane"/> is true when the pane runs a detected
    /// agent. Returns true when shown predictions may have changed.
    /// </summary>
    public bool Reconcile(
        string paneId,
        AssembledSnapshot frame,
        long? inputAck,
        bool fullOrReanchor,
        bool agentPane = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_policy.Predicts)
            return false;
        lock (_gate)
        {
            if (!string.Equals(paneId, _paneId, StringComparison.Ordinal))
                return false;
            var before = _version;
            if (_barrier is { Seq: null, Mark: 0 })
                _barrier = null;
            if (_barrierFirst is { Seq: null, Mark: 0 })
                _barrierFirst = null;
            if (OffReasonLocked(frame, agentPane, _time.GetTimestamp()) is { } off)
            {
                ResetLocked(off);
                SetBase(frame);
                return _version != before;
            }

            if (fullOrReanchor)
            {
                ResetLocked(PredictionResetReason.Reanchor);
                SetBase(frame);
            }
            else if (frame.Cols != _cols || frame.Rows != _rows || frame.OccupantGeneration != _occupant)
            {
                ResetLocked(frame.OccupantGeneration != _occupant
                    ? PredictionResetReason.OccupantChange
                    : PredictionResetReason.Resize);
                SetBase(frame);
            }

            var hidden = !frame.Cursor.HasCursor || !frame.Cursor.Visible;
            if (hidden != _cursorHidden)
            {
                _cursorHidden = hidden;
                _version++;
            }

            var now = _time.GetTimestamp();
            if (inputAck is { } ack)
                NoteAckLocked(ack, now);
            JudgeLocked(frame, now);
            return _version != before;
        }
    }

    /// <summary>
    /// An ack-only body for the frame that the client shows. It confirms the
    /// ack only: the frame is the stored frame that an earlier body brought.
    /// </summary>
    public bool NoteAck(string paneId, long inputAck, AssembledSnapshot frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_policy.Predicts)
            return false;
        lock (_gate)
        {
            if (!string.Equals(paneId, _paneId, StringComparison.Ordinal))
                return false;
            var before = _version;
            var now = _time.GetTimestamp();
            NoteAckLocked(inputAck, now);
            JudgeLocked(frame, now);
            return _version != before;
        }
    }

    /// <summary>
    /// Runs the timed rules: the judge after 50 ms of ack, the ack expiry and
    /// the 250 ms show rule. Returns true when shown predictions may have changed.
    /// </summary>
    public bool Tick(string paneId, AssembledSnapshot? frame)
    {
        if (!_policy.Predicts)
            return false;
        lock (_gate)
        {
            if (!string.Equals(paneId, _paneId, StringComparison.Ordinal))
                return false;
            var before = _version;
            if (frame is not null)
                JudgeLocked(frame, _time.GetTimestamp());
            // The 250 ms rule can start the display with no new frame.
            if (OldestPendingShownAgeLocked(_time.GetTimestamp()) >= AdaptiveEchoDisplayPolicy.ShowAnywayAfter)
                _version++;
            return _version != before;
        }
    }

    /// <summary>The time until the next timed rule, or null when none waits.</summary>
    public TimeSpan? NextWake()
    {
        if (!_policy.Predicts)
            return null;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            long? due = null;
            var judge = Ticks(JudgeDelay);
            var expiry = Ticks(AckExpiry);
            var showAnyway = Ticks(AdaptiveEchoDisplayPolicy.ShowAnywayAfter);
            foreach (var epoch in _epochs)
            {
                foreach (var p in epoch.Predictions)
                {
                    // A judge that already ran at or after its time found a
                    // hidden cursor: the next frame judges it, not a timer.
                    var at = p.AckSeenAt is { } seen ? seen + judge : p.CreatedAt + expiry;
                    if (p.AckSeenAt is null || at > _lastJudgedAt)
                        Earliest(ref due, at);
                    if (epoch.Confirmed && !p.Shown && p.CreatedAt + showAnyway > now)
                        Earliest(ref due, p.CreatedAt + showAnyway);
                }
            }

            if (due is not { } next)
                return null;
            return next <= now ? TimeSpan.Zero : TimeSpan.FromSeconds((next - now) / (double)_time.TimestampFrequency);
        }
    }

    /// <summary>
    /// The predictions to show over <paramref name="frame"/>, the compose
    /// frame of <paramref name="paneId"/>. Null when nothing shows: another
    /// pane, an off condition, a hidden cursor, a tentative epoch or the
    /// display policy. <paramref name="agentPane"/> is true when the pane runs
    /// a detected agent.
    /// </summary>
    public PredictionView? View(string paneId, AssembledSnapshot? frame, bool agentPane = false)
    {
        if (!_policy.Predicts || frame is null)
            return null;
        lock (_gate)
        {
            if (!string.Equals(paneId, _paneId, StringComparison.Ordinal) || _epochs.Count == 0)
                return null;
            if (OffReasonLocked(frame, agentPane, _time.GetTimestamp()) is not null
                || frame.Cols != _cols
                || frame.Rows != _rows
                || !frame.Cursor.HasCursor
                || !frame.Cursor.Visible)
            {
                return null;
            }

            var now = _time.GetTimestamp();
            // Until the new pane has its own sample, the policy gets no RTT.
            var srtt = _srttFromOldPane ? null : SrttLocked();
            if (!_policy.ShouldDisplay(srtt, OldestPendingShownAgeLocked(now)))
                return null;

            Dictionary<(int Col, int Row), AssembledCell>? cells = null;
            List<long>? shown = null;
            PredictedCursor? cursor = null;
            foreach (var epoch in _epochs)
            {
                if (!epoch.Confirmed)
                    continue;
                foreach (var p in epoch.Predictions)
                {
                    (shown ??= []).Add(p.Order);
                    if (p.Kind == EchoPredictionKind.Glyph)
                        (cells ??= [])[(p.Col, p.Row)] = p.Expected;
                    cursor = p.CursorAfter;
                }
            }

            if (shown is null)
                return null;
            var list = new List<PredictedCell>(cells?.Count ?? 0);
            if (cells is not null)
            {
                foreach (var ((col, row), cell) in cells)
                    list.Add(new PredictedCell(col, row, cell));
            }

            return new PredictionView(paneId, list, cursor, shown);
        }
    }

    /// <summary>A host paint that showed <paramref name="view"/> was accepted.</summary>
    public void NoteShown(PredictionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!_policy.Predicts || view.Shown.Count == 0)
            return;
        var added = 0;
        lock (_gate)
        {
            var orders = new HashSet<long>(view.Shown);
            foreach (var epoch in _epochs)
            {
                foreach (var p in epoch.Predictions)
                {
                    if (p.Shown || !orders.Contains(p.Order))
                        continue;
                    p.Shown = true;
                    added++;
                }
            }

            _predictedShown += added;
        }

        if (added > 0)
            _meter?.NotePredictionsShown(added);
    }

    public EchoPredictorStats Stats()
    {
        lock (_gate)
        {
            return new EchoPredictorStats
            {
                PredictedShown = _predictedShown,
                MispredictedShown = _mispredictedShown,
                ConfirmedByGlyph = _confirmedByGlyph,
                ConfirmedByCursor = _confirmedByCursor,
                Retired = _retired,
                Mismatches = _mismatches,
                Expired = _expired,
                Resets = _resets,
                Made = _made,
                Unpredicted = _unpredicted,
                BarrierSkips = _barrierSkips,
                ResetCalls = Enum.GetValues<PredictionResetReason>()
                    .ToDictionary(r => r, r => _resetCalls[(int)r]),
                Pending = CountLocked(),
                Srtt = SrttLocked(),
            };
        }
    }

    private static void Earliest(ref long? due, long at)
    {
        if (due is null || at < due)
            due = at;
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    private TimeSpan? SrttLocked() =>
        _srttTicks is { } ticks ? TimeSpan.FromSeconds(ticks / _time.TimestampFrequency) : null;

    private int CountLocked()
    {
        var count = 0;
        foreach (var epoch in _epochs)
            count += epoch.Predictions.Count;
        return count;
    }

    private TimeSpan OldestPendingShownAgeLocked(long now)
    {
        long? oldest = null;
        foreach (var epoch in _epochs)
        {
            if (!epoch.Confirmed)
                continue;
            foreach (var p in epoch.Predictions)
            {
                if (oldest is null || p.CreatedAt < oldest)
                    oldest = p.CreatedAt;
            }
        }

        return oldest is { } at && now > at
            ? TimeSpan.FromSeconds((now - at) / (double)_time.TimestampFrequency)
            : TimeSpan.Zero;
    }

    /// <summary>
    /// The frame conditions that turn prediction off (ADR 0018). Echo off in
    /// canonical mode turns it off for all panes: that is a password prompt
    /// (getpass, <c>read -s</c>, sudo, ssh), and no key shows. A line editor
    /// (readline, zle, fish, an agent in raw mode) turns echo and canonical
    /// mode off and draws its own echo, so echo off with canonical mode off
    /// turns nothing off. Epoch confirmation guards that case and an unknown
    /// mode. The alternate
    /// screen turns it off only for a pane with no detected agent: vim and
    /// less draw there, but an agent such as Claude Code draws its line
    /// editor there too. Synchronized output turns it off for all panes,
    /// until the block is older than <see cref="SynchronizedOutputStale"/>.
    /// </summary>
    private PredictionResetReason? OffReasonLocked(AssembledSnapshot frame, bool agentPane, long now)
    {
        if (frame.Echo is false && frame.Canonical is true)
            return PredictionResetReason.EchoOff;
        if (frame.IsAlternateScreen && !agentPane)
            return PredictionResetReason.AlternateScreen;
        if (!frame.SynchronizedOutput)
        {
            _syncSince = null;
            return null;
        }

        _syncSince ??= now;
        return now - _syncSince.Value < Ticks(SynchronizedOutputStale)
            ? PredictionResetReason.SynchronizedOutput
            : null;
    }

    /// <summary>
    /// The keys now go to another pane. Its acks, send times, RTT and DEC
    /// 2026 block are its own: a local pane after a cube must not keep the
    /// cube RTT, or the adaptive policy shows predictions there. The old RTT
    /// stays until the first sample of the new pane replaces it. A display
    /// needs a confirm, and a confirm comes after that sample, so the old RTT
    /// never decides a display on the new pane. The adaptive policy decides
    /// the first sample of the new pane at 25 ms, with no state of the old pane.
    /// </summary>
    private void ChangePaneLocked(string paneId)
    {
        ResetLocked(PredictionResetReason.FocusChange);
        if (_paneId is not null)
        {
            _ackedThrough = 0;
            _sent.Clear();
            _srttFromOldPane = true;
            _policy.NewRoute();
            _syncSince = null;
        }

        _paneId = paneId;
    }

    private void SetBase(AssembledSnapshot frame)
    {
        _cols = frame.Cols;
        _rows = frame.Rows;
        _occupant = frame.OccupantGeneration;
    }

    private void ResetLocked(PredictionResetReason reason)
    {
        _resetCalls[(int)reason]++;
        if (_epochs.Count > 0)
        {
            _resets++;
            _version++;
        }

        _epochs.Clear();
        _current = null;
    }

    private void SetBarrierLocked(long mark)
    {
        _barrier = (++_nextOrder, mark, null);
        _barrierFirst ??= _barrier;
        _current = null;
    }

    /// <summary>True while an unpredicted key is not yet acked.</summary>
    private bool BarrierActiveLocked() =>
        _barrier is { } barrier && !(barrier.Seq is { } seq && _ackedThrough >= seq);

    /// <summary>Gives each prediction and the barrier the seq of its mark.</summary>
    private void BindMarksLocked()
    {
        if (_assignments.Count == 0)
            return;
        long? now = null;
        foreach (var epoch in _epochs)
        {
            foreach (var p in epoch.Predictions)
            {
                if (p.Seq is null && SeqOf(p.Mark) is { } seq)
                {
                    p.Seq = seq;
                    // The ack came before this bind (the echo arrived while
                    // the stdin thread waited). Judge it from now, as if the
                    // ack came now; else no rule ever ends it.
                    if (seq <= _ackedThrough && p.AckSeenAt is null)
                        p.AckSeenAt = now ??= _time.GetTimestamp();
                }
            }
        }

        if (_barrier is { Seq: null, Mark: > 0 } barrier && SeqOf(barrier.Mark) is { } barrierSeq)
            _barrier = barrier with { Seq = barrierSeq };
        if (_barrierFirst is { Seq: null, Mark: > 0 } first && SeqOf(first.Mark) is { } firstSeq)
            _barrierFirst = first with { Seq = firstSeq };

        // The stdin thread observes marks in order. A later key has a mark
        // above the last observed mark, so an assignment below that mark and
        // below every mark that still waits for a seq binds nothing more.
        var oldest = _lastObservedMark;
        foreach (var epoch in _epochs)
        {
            foreach (var p in epoch.Predictions)
            {
                if (p.Seq is null)
                    oldest = Math.Min(oldest, p.Mark);
            }
        }

        if (_barrier is { Seq: null, Mark: > 0 } waiting)
            oldest = Math.Min(oldest, waiting.Mark);
        if (_barrierFirst is { Seq: null, Mark: > 0 } waitingFirst)
            oldest = Math.Min(oldest, waitingFirst.Mark);
        while (_assignments.Count > 0 && _assignments.Peek().MaxMark < oldest)
            _assignments.Dequeue();
    }

    private long? SeqOf(long mark)
    {
        foreach (var (maxMark, seq) in _assignments)
        {
            if (maxMark >= mark)
                return seq;
        }

        return null;
    }

    private void NoteAckLocked(long ack, long now)
    {
        if (ack <= _ackedThrough)
            return;
        _ackedThrough = ack;
        // One sample for each ack: the send time of the newest acked seq.
        long? sentAt = null;
        while (_sent.Count > 0 && _sent.Peek().Seq <= ack)
            sentAt = _sent.Dequeue().SentAt;

        if (sentAt is { } sent && now >= sent)
        {
            var sample = (double)(now - sent);
            // The first sample of a new pane replaces the RTT of the old pane.
            _srttTicks = _srttTicks is { } srtt && !_srttFromOldPane
                ? srtt + (SrttGain * (sample - srtt))
                : sample;
            _srttFromOldPane = false;
            _version++;
        }

        foreach (var epoch in _epochs)
        {
            foreach (var p in epoch.Predictions)
            {
                if (p.AckSeenAt is null && p.Seq is { } seq && seq <= ack)
                    p.AckSeenAt = now;
            }
        }
    }

    /// <summary>
    /// Applies the confirm rules of H-364 to each acked prediction.
    /// <list type="bullet">
    /// <item>A glyph prediction confirms when the frame glyph at its cell is the expected glyph.</item>
    /// <item>A cursor prediction confirms when the frame cursor is at the
    /// predicted cursor of the newest acked key of its epoch and, for a space
    /// or an erase, its cell is blank or a space.</item>
    /// <item>A prediction that a later unpredicted key also acked can no
    /// longer be judged: the key can move the text. It is retired.</item>
    /// <item>A frame with a hidden cursor judges no cursor and finds no mismatch.</item>
    /// <item>After 50 ms of ack, a prediction that did not confirm is a
    /// mismatch: the epoch is deleted.</item>
    /// </list>
    /// </summary>
    private void JudgeLocked(AssembledSnapshot frame, long now)
    {
        var judge = Ticks(JudgeDelay);
        var expiry = Ticks(AckExpiry);
        _lastJudgedAt = now;
        var barrierAcked = _barrierFirst is { Seq: { } barrierSeq } && barrierSeq <= _ackedThrough;
        var barrierOrder = _barrierFirst?.Order ?? long.MaxValue;
        var hidden = !frame.Cursor.HasCursor || !frame.Cursor.Visible;
        for (var e = 0; e < _epochs.Count; e++)
        {
            var epoch = _epochs[e];
            EchoPrediction? newestAcked = null;
            foreach (var p in epoch.Predictions)
            {
                if (p.Seq is { } seq && seq <= _ackedThrough)
                    newestAcked = p;
            }

            var deleted = false;
            for (var i = 0; i < epoch.Predictions.Count; i++)
            {
                var p = epoch.Predictions[i];
                if (p.Seq is not { } seq || seq > _ackedThrough)
                {
                    if (now - p.CreatedAt >= expiry)
                    {
                        _expired++;
                        DeleteEpochLocked(e, judged: null);
                        deleted = true;
                        break;
                    }

                    continue;
                }

                if (Confirms(p, frame, newestAcked!, hidden))
                {
                    if (p.Kind == EchoPredictionKind.Glyph)
                        _confirmedByGlyph++;
                    else
                        _confirmedByCursor++;
                    epoch.Predictions.RemoveAt(i--);
                    // An earlier prediction for the same cell is replaced by
                    // this one (a key, then an erase over it). It must not
                    // show again over the confirmed cell.
                    var superseded = epoch.Predictions.RemoveAll(q =>
                        q.Order < p.Order && q.Col == p.Col && q.Row == p.Row);
                    _retired += superseded;
                    i -= superseded;
                    if (!epoch.Confirmed)
                        epoch.Confirmed = true;
                    _version++;
                    continue;
                }

                if (barrierAcked && p.Order < barrierOrder)
                {
                    _retired++;
                    epoch.Predictions.RemoveAt(i--);
                    _version++;
                    continue;
                }

                if (hidden)
                    continue;
                if (p.AckSeenAt is { } seen && now - seen >= judge)
                {
                    _mismatches++;
                    DeleteEpochLocked(e, judged: frame);
                    deleted = true;
                    break;
                }
            }

            if (deleted)
            {
                e--;
                continue;
            }

            if (epoch.Predictions.Count == 0 && !ReferenceEquals(epoch, _current))
            {
                _epochs.RemoveAt(e--);
            }
        }

        if (barrierAcked)
            _barrierFirst = null;
        if (_barrier is { Seq: { } lastSeq } && lastSeq <= _ackedThrough)
            _barrier = null;
    }

    private static bool Confirms(EchoPrediction p, AssembledSnapshot frame, EchoPrediction newestAcked, bool hidden)
    {
        if (p.Kind == EchoPredictionKind.Glyph)
            return SameGlyph(frame.CellAt(p.Col, p.Row), p.Expected);
        if (hidden)
            return false;
        var cursor = frame.Cursor;
        if (cursor.Col != newestAcked.CursorAfter.Col || cursor.Row != newestAcked.CursorAfter.Row)
            return false;
        return !p.CellMustBeBlank || IsBlank(frame.CellAt(p.Col, p.Row));
    }

    /// <summary>
    /// Deletes an epoch and counts its shown predictions that were wrong. On
    /// a mismatch, <paramref name="judged"/> is the frame: a shown glyph
    /// counts when the frame glyph differs, and the newest shown cursor
    /// prediction counts when the frame cursor is not at its place. A shown
    /// prediction that the frame does not contradict only goes away for one
    /// paint. On an expiry no frame answered, so every shown prediction counts.
    /// </summary>
    private void DeleteEpochLocked(int index, AssembledSnapshot? judged)
    {
        var epoch = _epochs[index];
        var shown = 0;
        EchoPrediction? newestCursor = null;
        foreach (var p in epoch.Predictions)
        {
            if (!p.Shown)
                continue;
            if (judged is null)
            {
                shown++;
                continue;
            }

            if (p.Kind == EchoPredictionKind.Glyph)
            {
                if (!SameGlyph(judged.CellAt(p.Col, p.Row), p.Expected))
                    shown++;
            }
            else
            {
                newestCursor = p;
            }
        }

        if (newestCursor is { } last
            && (judged!.Cursor.Col != last.CursorAfter.Col || judged.Cursor.Row != last.CursorAfter.Row))
        {
            shown++;
        }

        _epochs.RemoveAt(index);
        if (ReferenceEquals(epoch, _current))
            _current = null;
        _version++;
        if (shown == 0)
            return;
        _mispredictedShown += shown;
        _meter?.NotePredictionsMispredicted(shown);
    }

    /// <summary>
    /// The prediction of one key, or null when the key is not predictable
    /// here. The predicted state is the frame with every live prediction on
    /// top, shown or not.
    /// </summary>
    private EchoPrediction? Predict(EchoKey key, AssembledSnapshot frame, long mark, long now)
    {
        if (key.Kind == EchoKeyKind.Other)
            return null;
        var cursor = PredictedCursorLocked(frame);
        if (cursor is not { } at)
            return null;
        var (col, row) = (at.Col, at.Row);
        if ((uint)row >= (uint)frame.Rows || (uint)col >= (uint)frame.Cols)
            return null;
        var under = DisplayedCell(frame, col, row);
        switch (key.Kind)
        {
            case EchoKeyKind.Printable:
                {
                    // The last column wraps. A wide cell does not take one glyph.
                    if (col >= frame.Cols - 1 || under.Width != 1 || under.IsContinuation)
                        return null;
                    var style = col > 0 ? DisplayedCell(frame, col - 1, row).Style : under.Style;
                    if (key.Glyph == (byte)' ' && IsBlank(under))
                    {
                        return New(EchoPredictionKind.Cursor, col, row, new AssembledCell(" ", 1, false, style),
                            new PredictedCursor(col + 1, row), cellMustBeBlank: true);
                    }

                    var glyph = AssembledCell.InternText([(char)key.Glyph]);
                    return New(EchoPredictionKind.Glyph, col, row, new AssembledCell(glyph, 1, false, style),
                        new PredictedCursor(col + 1, row), cellMustBeBlank: false);
                }

            case EchoKeyKind.Erase:
                {
                    // Only an erase at the end of the text is a plain left move
                    // and blank. Inside the text the line editor moves the rest.
                    if (col == 0 || !IsBlank(under))
                        return null;
                    var target = DisplayedCell(frame, col - 1, row);
                    if (target.Width != 1 || target.IsContinuation)
                        return null;
                    return IsBlank(target)
                        ? New(EchoPredictionKind.Cursor, col - 1, row, AssembledCell.Blank,
                            new PredictedCursor(col - 1, row), cellMustBeBlank: true)
                        : New(EchoPredictionKind.Glyph, col - 1, row, AssembledCell.Blank,
                            new PredictedCursor(col - 1, row), cellMustBeBlank: false);
                }

            case EchoKeyKind.Right:
                {
                    // At the end of the text a line editor does not move right.
                    if (col >= frame.Cols - 1 || IsBlank(under) || under.Width != 1 || under.IsContinuation)
                        return null;
                    return New(EchoPredictionKind.Cursor, col, row, under,
                        new PredictedCursor(col + 1, row), cellMustBeBlank: false);
                }

            case EchoKeyKind.Left:
                {
                    if (col == 0)
                        return null;
                    var target = DisplayedCell(frame, col - 1, row);
                    if (target.Width != 1 || target.IsContinuation)
                        return null;
                    return New(EchoPredictionKind.Cursor, col - 1, row, target,
                        new PredictedCursor(col - 1, row), cellMustBeBlank: false);
                }

            default:
                return null;
        }

        EchoPrediction New(
            EchoPredictionKind kind,
            int cellCol,
            int cellRow,
            AssembledCell expected,
            PredictedCursor after,
            bool cellMustBeBlank) =>
            new()
            {
                Order = ++_nextOrder,
                Mark = mark,
                Kind = kind,
                Col = cellCol,
                Row = cellRow,
                Expected = expected,
                CellMustBeBlank = cellMustBeBlank,
                CursorAfter = after,
                CreatedAt = now,
            };
    }

    /// <summary>The cursor after the newest live prediction, or the frame cursor.</summary>
    private PredictedCursor? PredictedCursorLocked(AssembledSnapshot frame)
    {
        for (var e = _epochs.Count - 1; e >= 0; e--)
        {
            var predictions = _epochs[e].Predictions;
            if (predictions.Count > 0)
                return predictions[^1].CursorAfter;
        }

        // A hidden cursor can sit at a place that the application uses
        // only while it draws.
        if (!frame.Cursor.HasCursor || !frame.Cursor.Visible)
            return null;
        return new PredictedCursor(frame.Cursor.Col, frame.Cursor.Row);
    }

    /// <summary>The cell that the predicted state shows: the newest glyph prediction, or the frame.</summary>
    private AssembledCell DisplayedCell(AssembledSnapshot frame, int col, int row)
    {
        for (var e = _epochs.Count - 1; e >= 0; e--)
        {
            var predictions = _epochs[e].Predictions;
            for (var i = predictions.Count - 1; i >= 0; i--)
            {
                var p = predictions[i];
                if (p.Kind == EchoPredictionKind.Glyph && p.Col == col && p.Row == row)
                    return p.Expected;
            }
        }

        return frame.CellAt(col, row);
    }

    private static bool IsBlank(AssembledCell cell) =>
        !cell.IsContinuation && (string.IsNullOrEmpty(cell.Text) || cell.Text == " ");

    private static bool SameGlyph(AssembledCell frame, AssembledCell expected)
    {
        var a = string.IsNullOrEmpty(frame.Text) ? " " : frame.Text;
        var b = string.IsNullOrEmpty(expected.Text) ? " " : expected.Text;
        return string.Equals(a, b, StringComparison.Ordinal) && !frame.IsContinuation;
    }
}
