using System.Collections.Concurrent;

namespace Hypa.ControlPlane;

/// <summary>The limits and the timers of render credit.</summary>
internal sealed record RenderCreditOptions
{
    public static RenderCreditOptions Default { get; } = new();

    /// <summary>The budget of a connection before the first ack.</summary>
    public int StartBudgetBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// The smallest budget. On a slow link, this many bytes can wait in front
    /// of an echo, so it sets the echo delay at a low bandwidth.
    /// </summary>
    public int MinBudgetBytes { get; init; } = 4 * 1024;

    public int MaxBudgetBytes { get; init; } = 1024 * 1024;

    /// <summary>The largest budget of the focused pane. Echo frames are small.</summary>
    public int MaxFocusedBudgetBytes { get; init; } = 32 * 1024;

    /// <summary>The budget is this many times the measured bandwidth-delay product.</summary>
    public double BudgetGain { get; init; } = 1.5;

    /// <summary>The RTT value below which a sample counts as this value.</summary>
    public TimeSpan RttFloor { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>The window of the delivery rate maximum.</summary>
    public TimeSpan RateWindow { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The window of the RTT minimum.</summary>
    public TimeSpan MinRttWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The shortest time without an ack before the mux gives the credit back.</summary>
    public TimeSpan MinAckTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The ack timeout is at least this many smoothed RTTs.</summary>
    public int AckTimeoutRtts { get; init; } = 4;

    /// <summary>
    /// The target size of one slice of a large cells frame or JSON full
    /// snapshot for a connection with credit. Each slice is one line with
    /// its own charge, so the bytes in front of an echo stay near one slice,
    /// not one full frame. A row larger than this value goes alone in its
    /// slice.
    /// </summary>
    public int SliceBytes { get; init; } = 8 * 1024;
}

/// <summary>
/// One line of a frame that the mux charges: its bytes and the end row
/// (exclusive) of its last row. The slices of one frame have one generation
/// and rising end rows, so an ack of a slice gives back only the slices up to
/// it. <see cref="RenderRows.All"/> stands for a line that holds the whole
/// frame.
/// </summary>
internal readonly record struct RenderLine(int Bytes, int RowEnd);

/// <summary>One entry of a <c>render.ack</c>: every line of the pane up to this generation and end row.</summary>
internal readonly record struct RenderAck(string PaneId, long Generation, int RowEnd = RenderRows.All);

/// <summary>A copy of the state of one <see cref="RenderCredit"/>. Diagnostic.</summary>
internal readonly record struct RenderCreditState(
    long UnackedBytes,
    long FocusedUnackedBytes,
    int Budget,
    int FocusedBudget,
    int InFlightFrames,
    long FailOpens,
    long ExpiredFrames,
    string? FocusedPaneId);

/// <summary>
/// The render credit of one attach connection. The mux charges each cells
/// frame and each snapshot slice that it admits for the connection. The
/// client acks the frames that it painted. A pane without the focus gets no
/// new frame while the unacked bytes of the connection are at or above the
/// budget. The focused pane (the pane that got the latest input of the
/// connection) has its own small budget, so other panes never delay its
/// frames. When no ack comes in the ack timeout, the credit comes back (fail
/// open).
/// <para>
/// The budget follows the bandwidth-delay product of the link: the gain times
/// the highest recent delivery rate times the lowest recent RTT, between the
/// smallest and the largest budget. The ack of a frame gives one RTT sample and
/// one delivery rate sample.
/// </para>
/// The connection owns this state. One lock guards it.
/// </summary>
internal sealed class RenderCredit : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly RenderCreditOptions _options;
    private readonly Action _creditReturned;
    private readonly LinkedList<Entry> _inFlight = new();
    private readonly Queue<(DateTimeOffset At, double BytesPerSecond)> _rates = new();
    private readonly Queue<(DateTimeOffset At, TimeSpan Rtt)> _rtts = new();
    private long _unacked;
    private long _focusedUnacked;
    private long _delivered;
    private DateTimeOffset _lastProgress;
    private DateTimeOffset _lastDeliveredAt;
    private int _budget;
    private TimeSpan _smoothedRtt;
    private string? _focusedPaneId;
    private long _failOpens;
    private long _expiredFrames;
    private ITimer? _timer;
    private bool _timerArmed;
    private bool _writerFull;
    private bool _disposed;

    private const int MaxSamples = 256;

    public RenderCredit(TimeProvider time, Action creditReturned, RenderCreditOptions? options = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _creditReturned = creditReturned ?? throw new ArgumentNullException(nameof(creditReturned));
        _options = options ?? RenderCreditOptions.Default;
        _budget = Math.Clamp(_options.StartBudgetBytes, _options.MinBudgetBytes, _options.MaxBudgetBytes);
    }

    public RenderCreditState State
    {
        get
        {
            lock (_gate)
            {
                return new RenderCreditState(
                    _unacked,
                    _focusedUnacked,
                    _budget,
                    FocusedBudgetUnlocked,
                    _inFlight.Count,
                    _failOpens,
                    _expiredFrames,
                    _focusedPaneId);
            }
        }
    }

    public string? FocusedPaneId
    {
        get { lock (_gate) return _focusedPaneId; }
    }

    private int FocusedBudgetUnlocked => Math.Min(_options.MaxFocusedBudgetBytes, _budget);

    /// <summary>The time without an ack after which the credit comes back.</summary>
    public TimeSpan AckTimeout
    {
        get { lock (_gate) return AckTimeoutUnlocked; }
    }

    private TimeSpan AckTimeoutUnlocked
    {
        get
        {
            var byRtt = _smoothedRtt * _options.AckTimeoutRtts;
            return byRtt > _options.MinAckTimeout ? byRtt : _options.MinAckTimeout;
        }
    }

    /// <summary>
    /// True when a new frame of <paramref name="paneId"/> may go to the
    /// writer now. When it returns false, the caller keeps the frame as the
    /// latest deferred frame of the pane. Credit that comes back later runs
    /// the <c>creditReturned</c> callback.
    /// </summary>
    public bool MayAdmit(string paneId)
    {
        var restored = false;
        bool may;
        lock (_gate)
        {
            if (_disposed)
                return true;
            restored = ExpireUnlocked(_time.GetUtcNow());
            may = string.Equals(paneId, _focusedPaneId, StringComparison.Ordinal)
                ? _focusedUnacked < FocusedBudgetUnlocked
                : _unacked < _budget;
            if (!may)
                ArmTimerUnlocked();
        }

        if (restored)
            _creditReturned();
        return may;
    }

    /// <summary>
    /// Charges one encoded frame of <paramref name="paneId"/> when it fits in
    /// its budget now. A frame always fits when nothing of its budget is in
    /// flight, so a frame larger than the budget still goes, alone. The check
    /// and the charge are one step, so two panes cannot both take the same
    /// room. When it returns false, the caller defers the frame. When the
    /// writer then refuses a charged frame, the caller calls
    /// <see cref="Refund"/>.
    /// </summary>
    public bool TryCharge(string paneId, long generation, int bytes, int rowEnd = RenderRows.All)
    {
        bool restored;
        bool fits;
        lock (_gate)
        {
            if (_disposed)
                return true;
            var now = _time.GetUtcNow();
            restored = ExpireUnlocked(now);
            var focused = string.Equals(paneId, _focusedPaneId, StringComparison.Ordinal);
            fits = focused
                ? _focusedUnacked == 0 || _focusedUnacked + bytes <= FocusedBudgetUnlocked
                : _unacked == 0 || _unacked + bytes <= _budget;
            if (fits)
                ChargeUnlocked(paneId, generation, bytes, rowEnd, now);
            else
                ArmTimerUnlocked();
        }

        if (restored)
            _creditReturned();
        return fits;
    }

    /// <summary>
    /// Charges the longest first part of the lines of one frame that fits in
    /// the budget of <paramref name="paneId"/> now, one entry for each line,
    /// so the ack of one slice gives back only the slices up to it. The first
    /// line always fits when nothing of the budget is in flight, so one line
    /// larger than the budget still goes, alone. Returns the count of lines
    /// that were charged, and their bytes. Zero means that the frame waits.
    /// When the writer then refuses the charged lines, the caller calls
    /// <see cref="RefundLines"/> with the same lines and count.
    /// </summary>
    public (int Lines, int Bytes) TryChargeLines(string paneId, long generation, IReadOnlyList<RenderLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var restored = false;
        var count = 0;
        var bytes = 0;
        lock (_gate)
        {
            if (_disposed)
            {
                foreach (var line in lines)
                    bytes += line.Bytes;
                return (lines.Count, bytes);
            }

            var now = _time.GetUtcNow();
            restored = ExpireUnlocked(now);
            var focused = string.Equals(paneId, _focusedPaneId, StringComparison.Ordinal);
            var unacked = focused ? _focusedUnacked : _unacked;
            var budget = focused ? FocusedBudgetUnlocked : _budget;
            while (count < lines.Count)
            {
                var next = lines[count].Bytes;
                var fits = (count == 0 && unacked == 0) || unacked + bytes + next <= budget;
                if (!fits)
                    break;
                bytes += next;
                count++;
            }

            for (var i = 0; i < count; i++)
                ChargeUnlocked(paneId, generation, lines[i].Bytes, lines[i].RowEnd, now);
            if (count < lines.Count)
                ArmTimerUnlocked();
        }

        if (restored)
            _creditReturned();
        return (count, bytes);
    }

    /// <summary>Takes back the charge of a frame that the writer did not take.</summary>
    public void Refund(string paneId, long generation, int bytes, int rowEnd = RenderRows.All)
    {
        lock (_gate)
            RefundUnlocked(paneId, generation, bytes, rowEnd);
    }

    /// <summary>Takes back the charges of the first <paramref name="count"/> lines that the writer did not take.</summary>
    public void RefundLines(string paneId, long generation, IReadOnlyList<RenderLine> lines, int count)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_gate)
        {
            for (var i = 0; i < count && i < lines.Count; i++)
                RefundUnlocked(paneId, generation, lines[i].Bytes, lines[i].RowEnd);
        }
    }

    private void RefundUnlocked(string paneId, long generation, int bytes, int rowEnd)
    {
        for (var node = _inFlight.Last; node is not null; node = node.Previous)
        {
            var entry = node.Value;
            if (entry.Generation != generation
                || entry.Bytes != bytes
                || entry.RowEnd != rowEnd
                || !string.Equals(entry.PaneId, paneId, StringComparison.Ordinal))
            {
                continue;
            }

            SubtractUnlocked(entry);
            _inFlight.Remove(node);
            return;
        }
    }

    /// <summary>
    /// The connection sent input to <paramref name="paneId"/>. Its echo
    /// comes next, so the pane takes the focused budget at once, before the
    /// next ack names it. Returns true when the focused pane changed.
    /// </summary>
    public bool NoteFocus(string paneId)
    {
        if (string.IsNullOrEmpty(paneId))
            return false;
        lock (_gate)
            return !_disposed && SetFocusUnlocked(paneId);
    }

    /// <summary>
    /// The writer of the connection refused a frame because its lanes were
    /// full. The next drain of the writer runs the deferred frames again.
    /// A frame that waits for credit runs again on an ack, not on a drain.
    /// </summary>
    public void NoteWriterFull()
    {
        lock (_gate)
            _writerFull = true;
    }

    /// <summary>
    /// True while a <see cref="NoteWriterFull"/> waits for a writer drain.
    /// It does not clear the flag.
    /// </summary>
    public bool WriterFullPending
    {
        get { lock (_gate) return _writerFull; }
    }

    /// <summary>True once after <see cref="NoteWriterFull"/>.</summary>
    public bool TakeWriterFull()
    {
        lock (_gate)
        {
            var full = _writerFull;
            _writerFull = false;
            return full;
        }
    }

    /// <summary>Charges one admitted cells frame of <paramref name="paneId"/>.</summary>
    public void Charge(string paneId, long generation, int bytes, int rowEnd = RenderRows.All)
    {
        lock (_gate)
        {
            if (!_disposed)
                ChargeUnlocked(paneId, generation, bytes, rowEnd, _time.GetUtcNow());
        }
    }

    private void ChargeUnlocked(string paneId, long generation, int bytes, int rowEnd, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(paneId) || bytes <= 0)
            return;
        if (_inFlight.Count == 0)
            _lastProgress = now;
        var focused = string.Equals(paneId, _focusedPaneId, StringComparison.Ordinal);
        if (_lastDeliveredAt == default)
            _lastDeliveredAt = now;
        _inFlight.AddLast(new Entry(paneId, generation, rowEnd, bytes, focused, now, _delivered, _lastDeliveredAt));
        if (focused)
            _focusedUnacked += bytes;
        else
            _unacked += bytes;
    }

    private void SubtractUnlocked(Entry entry)
    {
        if (entry.Focused)
            _focusedUnacked = Math.Max(0, _focusedUnacked - entry.Bytes);
        else
            _unacked = Math.Max(0, _unacked - entry.Bytes);
    }

    /// <summary>
    /// Moves the focus. The frames in flight of the new focused pane move to
    /// the focused budget, and the frames of the old one move to the budget
    /// of the connection.
    /// </summary>
    private bool SetFocusUnlocked(string paneId)
    {
        if (string.Equals(paneId, _focusedPaneId, StringComparison.Ordinal))
            return false;
        _focusedPaneId = paneId;
        for (var node = _inFlight.First; node is not null; node = node.Next)
        {
            var entry = node.Value;
            var focused = string.Equals(entry.PaneId, paneId, StringComparison.Ordinal);
            if (focused == entry.Focused)
                continue;
            SubtractUnlocked(entry);
            node.Value = entry with { Focused = focused };
            if (focused)
                _focusedUnacked += entry.Bytes;
            else
                _unacked += entry.Bytes;
        }

        return true;
    }

    /// <summary>
    /// Applies one <c>render.ack</c>. Each ack gives back the bytes of every
    /// line of its pane of an older generation, and of every line of its
    /// generation up to and including its end row. A frame that a timeout
    /// already gave back gives nothing again. Returns true when credit came
    /// back. An ack never moves the focus: only input does, in the order that
    /// the mux reads the input of the connection.
    /// </summary>
    public bool Ack(IReadOnlyList<RenderAck> acks)
    {
        ArgumentNullException.ThrowIfNull(acks);
        lock (_gate)
        {
            if (_disposed)
                return false;
            var now = _time.GetUtcNow();

            long released = 0;
            Entry? newest = null;
            var node = _inFlight.First;
            while (node is not null)
            {
                var next = node.Next;
                var entry = node.Value;
                if (Covers(acks, entry))
                {
                    released += entry.Bytes;
                    SubtractUnlocked(entry);
                    if (newest is null || entry.AdmittedAt >= newest.Value.AdmittedAt)
                        newest = entry;
                    _inFlight.Remove(node);
                }

                node = next;
            }

            if (released == 0)
                return false;

            _delivered += released;
            _lastProgress = now;
            _lastDeliveredAt = now;
            if (newest is { } sample)
                AddSampleUnlocked(now, sample);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _inFlight.Clear();
            _unacked = 0;
            _focusedUnacked = 0;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static bool Covers(IReadOnlyList<RenderAck> acks, Entry entry)
    {
        for (var i = 0; i < acks.Count; i++)
        {
            var ack = acks[i];
            if (!string.Equals(ack.PaneId, entry.PaneId, StringComparison.Ordinal))
                continue;
            if (ack.Generation > entry.Generation
                || (ack.Generation == entry.Generation && ack.RowEnd >= entry.RowEnd))
            {
                return true;
            }
        }

        return false;
    }

    private void AddSampleUnlocked(DateTimeOffset now, Entry sample)
    {
        var rtt = now - sample.AdmittedAt;
        if (rtt < TimeSpan.Zero)
            rtt = TimeSpan.Zero;
        // The bytes acked since the frame left were sent after the last ack
        // before it left, so that ack time starts the interval. The admit
        // time would start it too late and count bytes that were already on
        // the link.
        var interval = now - sample.DeliveredTimeAtAdmit;
        if (interval < _options.RttFloor)
            interval = _options.RttFloor;
        var rate = (_delivered - sample.DeliveredAtAdmit) / interval.TotalSeconds;

        _smoothedRtt = _smoothedRtt == TimeSpan.Zero
            ? rtt
            : TimeSpan.FromTicks((_smoothedRtt.Ticks * 7 + rtt.Ticks) / 8);

        Push(_rates, (now, rate));
        Push(_rtts, (now, rtt));
        while (_rates.Count > 1 && now - _rates.Peek().At > _options.RateWindow)
            _rates.Dequeue();
        while (_rtts.Count > 1 && now - _rtts.Peek().At > _options.MinRttWindow)
            _rtts.Dequeue();

        var maxRate = 0.0;
        foreach (var (_, value) in _rates)
            maxRate = Math.Max(maxRate, value);
        var minRtt = TimeSpan.MaxValue;
        foreach (var (_, value) in _rtts)
        {
            if (value < minRtt)
                minRtt = value;
        }

        if (minRtt < _options.RttFloor)
            minRtt = _options.RttFloor;
        var bdp = _options.BudgetGain * maxRate * minRtt.TotalSeconds;
        _budget = (int)Math.Clamp(bdp, _options.MinBudgetBytes, _options.MaxBudgetBytes);
    }

    private static void Push<T>(Queue<T> queue, T value)
    {
        if (queue.Count >= MaxSamples)
            queue.Dequeue();
        queue.Enqueue(value);
    }

    /// <summary>
    /// Gives credit back without an ack. When no ack came for the ack
    /// timeout, every frame in flight is given back (fail open). When acks
    /// come but one frame waits for two timeouts, its pane acks no more (the
    /// pane went out of view, or the client dropped the frame), so only that
    /// frame is given back. Returns true when credit came back.
    /// </summary>
    private bool ExpireUnlocked(DateTimeOffset now)
    {
        if (_inFlight.Count == 0)
            return false;
        var timeout = AckTimeoutUnlocked;
        if (now - _lastProgress >= timeout)
        {
            _inFlight.Clear();
            _unacked = 0;
            _focusedUnacked = 0;
            _lastProgress = now;
            _failOpens++;
            return true;
        }

        var expired = false;
        while (_inFlight.First is { } oldest && now - oldest.Value.AdmittedAt >= timeout * 2)
        {
            SubtractUnlocked(oldest.Value);
            _inFlight.RemoveFirst();
            _expiredFrames++;
            expired = true;
        }

        return expired;
    }

    private void ArmTimerUnlocked()
    {
        if (_timerArmed || _disposed)
            return;
        var now = _time.GetUtcNow();
        var timeout = AckTimeoutUnlocked;
        var due = timeout;
        if (_inFlight.First is { } oldest)
        {
            var stall = _lastProgress + timeout - now;
            var expiry = oldest.Value.AdmittedAt + timeout * 2 - now;
            due = stall < expiry ? stall : expiry;
        }

        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;
        _timerArmed = true;
        if (_timer is null)
            _timer = _time.CreateTimer(static state => ((RenderCredit)state!).OnTimer(), this, due, Timeout.InfiniteTimeSpan);
        else
            _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        bool restored;
        lock (_gate)
        {
            _timerArmed = false;
            if (_disposed)
                return;
            restored = ExpireUnlocked(_time.GetUtcNow());
            // Progress came after the timer started. Wait for the rest of the
            // timeout while frames are still in flight.
            if (!restored && _inFlight.Count > 0)
                ArmTimerUnlocked();
        }

        if (restored)
            _creditReturned();
    }

    private readonly record struct Entry(
        string PaneId,
        long Generation,
        int RowEnd,
        int Bytes,
        bool Focused,
        DateTimeOffset AdmittedAt,
        long DeliveredAtAdmit,
        DateTimeOffset DeliveredTimeAtAdmit);
}

/// <summary>
/// The render credit of each attach connection that offered it in its hello.
/// A connection without an entry gets no credit limit.
/// </summary>
internal sealed class RenderCreditLedger
{
    private readonly ConcurrentDictionary<string, RenderCredit> _connections = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public RenderCreditLedger(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Turns credit on or off for <paramref name="connectionId"/>. A new
    /// hello on the same connection starts a new credit.
    /// </summary>
    public void Configure(
        string connectionId,
        bool enabled,
        Action creditReturned,
        RenderCreditOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;
        if (_connections.TryRemove(connectionId, out var prior))
            prior.Dispose();
        if (enabled)
            _connections[connectionId] = new RenderCredit(_time, creditReturned, options);
    }

    public RenderCredit? For(string connectionId) =>
        !string.IsNullOrEmpty(connectionId) && _connections.TryGetValue(connectionId, out var credit)
            ? credit
            : null;

    /// <summary>The connections that have credit now. Diagnostic.</summary>
    public IReadOnlyList<string> ConnectionIds => [.. _connections.Keys];

    public void Forget(string connectionId)
    {
        if (!string.IsNullOrEmpty(connectionId) && _connections.TryRemove(connectionId, out var credit))
            credit.Dispose();
    }
}
