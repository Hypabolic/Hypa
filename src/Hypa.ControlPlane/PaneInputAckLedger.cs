using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Hypa.ControlPlane;

/// <summary>
/// One <c>pane.send_keys</c> batch that carries a client <c>seq</c>. The
/// connection id keeps the value apart from other clients of the same pane.
/// </summary>
internal readonly record struct InputSeqStamp(string ConnectionId, long Seq);

/// <summary>The state of a capture window against the seq writes of its pane.</summary>
internal enum InputCaptureState
{
    /// <summary>No seq write was in flight. The ack read before the capture is exact.</summary>
    Clean,

    /// <summary>A seq write is in flight. A live paint waits for it to end.</summary>
    Hold,

    /// <summary>
    /// A seq write was in flight and the capture runs anyway: an attach
    /// capture, or a live paint held for one render tick. The body carries
    /// no <c>input_ack</c>, because the echo of that write can be in it.
    /// </summary>
    Uncertain,
}

/// <summary>
/// What the end of a seq write asks of the caller. <see cref="RunHeldPaint"/>:
/// a live paint was held back for this write. <see cref="PostAckOnly"/>: an
/// earlier body went out without <c>input_ack</c> while this write was in
/// flight, every write has ended, and the pane owes an ack-only body.
/// </summary>
internal readonly record struct InputWriteEnd(bool RunHeldPaint, bool PostAckOnly);

/// <summary>
/// The capture window of one paint. <see cref="Started"/> is the count of
/// seq writes that had started when the window opened.
/// </summary>
internal readonly record struct InputCaptureWindow(long Started, InputCaptureState State);

/// <summary>
/// The highest written input <c>seq</c> for each pane and each connection,
/// and a barrier between seq writes and captures.
/// <para>
/// The pane input actor records a value after the PTY write completes. A
/// paint reads the values of the pane before its capture starts, so
/// <c>input_ack</c> never claims a write that completed after the capture
/// started. The application can echo before the actor records the write.
/// For that reason a live capture that overlaps a seq write is not posted:
/// the paint waits for the record, then runs again. A capture that must run
/// while a write is in flight posts no <c>input_ack</c>. Thus a body that
/// shows an echo and carries an ack also acks its key.
/// </para>
/// Reads take no lock: a writer publishes a new immutable map.
/// </summary>
internal sealed class PaneInputAckLedger
{
    private readonly ConcurrentDictionary<string, PaneAcks> _panes = new(StringComparer.Ordinal);

    /// <summary>A seq write is about to start. Pair each call with <see cref="EndWrite"/>.</summary>
    public void BeginWrite(string paneId)
    {
        if (!string.IsNullOrWhiteSpace(paneId))
            Pane(paneId).BeginWrite();
    }

    /// <summary>
    /// A seq write ended. When <paramref name="written"/> is true, the PTY
    /// write completed and the seq is recorded first. The result tells the
    /// caller to run a held live paint, to post an ack-only body, or both.
    /// </summary>
    public InputWriteEnd EndWrite(string paneId, InputSeqStamp stamp, bool written)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return default;
        var pane = Pane(paneId);
        if (written && !string.IsNullOrWhiteSpace(stamp.ConnectionId) && stamp.Seq > 0)
            pane.Note(stamp);
        var runHeld = pane.EndWrite();
        return new InputWriteEnd(runHeld, pane.AckOwedAndIdle);
    }

    /// <summary>
    /// A body of the pane was admitted without <c>input_ack</c> while a seq
    /// write was in flight. No later body has to carry the ack of that write,
    /// so the pane owes an ack-only body. Returns true when every write
    /// already ended: the caller posts the ack-only body itself, because no
    /// write end will.
    /// </summary>
    public bool NoteAckWithheld(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId) || !_panes.TryGetValue(paneId, out var pane))
            return false;
        return pane.OweAck();
    }

    /// <summary>The panes that owe an ack-only body and have no seq write in flight.</summary>
    public IEnumerable<string> OwedIdlePanes()
    {
        foreach (var (paneId, pane) in _panes)
        {
            if (pane.AckOwedAndIdle)
                yield return paneId;
        }
    }

    /// <summary>True when the pane owes an ack-only body and no seq write is in flight.</summary>
    public bool AckOwedAndIdle(string paneId) =>
        _panes.TryGetValue(paneId, out var pane) && pane.AckOwedAndIdle;

    /// <summary>
    /// Takes the owed ack of the pane. Returns the acks to post: for each
    /// connection, the written seq when it is above the last ack posted to
    /// that connection. The caller posts them and records each with
    /// <see cref="NotePosted"/>.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, long>> PeekOwedAcks(string paneId, out long version)
    {
        version = 0;
        if (!_panes.TryGetValue(paneId, out var pane) || pane.PeekOwed() is not { } owed)
            return [];
        version = owed;
        return pane.Unposted();
    }

    /// <summary>
    /// Clears the owed ack after every ack-only body of
    /// <see cref="PeekOwedAcks"/> was admitted. An ack that became owed after
    /// the peek (a new version) stays owed. The flag stays set while the
    /// posts run, so a writer drain in that time still finds the pane.
    /// </summary>
    public void ClearOwed(string paneId, long version)
    {
        if (_panes.TryGetValue(paneId, out var pane))
            pane.ClearOwed(version);
    }

    /// <summary>
    /// Records that a body with <paramref name="inputAck"/> went to
    /// <paramref name="connectionId"/>. An ack-only body is not sent for an
    /// ack that a body already carried.
    /// </summary>
    public void NotePosted(string paneId, string connectionId, long inputAck)
    {
        if (string.IsNullOrWhiteSpace(paneId)
            || string.IsNullOrWhiteSpace(connectionId)
            || !_panes.TryGetValue(paneId, out var pane))
        {
            return;
        }

        pane.NotePosted(connectionId, inputAck);
    }

    /// <summary>
    /// The values of one pane at this instant. Empty when no connection sent a
    /// <c>seq</c> to the pane.
    /// </summary>
    public ImmutableDictionary<string, long> Snapshot(string paneId) =>
        _panes.TryGetValue(paneId, out var acks)
            ? acks.Current
            : ImmutableDictionary<string, long>.Empty;

    /// <summary>
    /// Opens the capture window of a paint. With <paramref name="mayHold"/>,
    /// a seq write in flight gives <see cref="InputCaptureState.Hold"/>: the
    /// caller holds the paint and calls <see cref="HoldPaint"/>. After
    /// <paramref name="maxHold"/> of continuous holds the window opens as
    /// <see cref="InputCaptureState.Uncertain"/>, so a paste cannot starve
    /// the paint.
    /// </summary>
    public InputCaptureWindow OpenCaptureWindow(
        string paneId,
        DateTimeOffset now,
        TimeSpan maxHold,
        bool mayHold)
    {
        if (!_panes.TryGetValue(paneId, out var pane))
            return new InputCaptureWindow(0, InputCaptureState.Clean);
        return pane.Open(now, maxHold, mayHold);
    }

    /// <summary>True when a seq write started after the window opened.</summary>
    public bool WriteStartedSince(string paneId, InputCaptureWindow window) =>
        _panes.TryGetValue(paneId, out var pane) && pane.StartedCount != window.Started;

    /// <summary>
    /// Marks a held paint. Returns true when every write already ended, so
    /// the caller runs the paint itself: no write end will run it.
    /// </summary>
    public bool HoldPaint(string paneId) => Pane(paneId).Hold();

    public void ForgetConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;
        foreach (var acks in _panes.Values)
            acks.Forget(connectionId);
    }

    public void ForgetPane(string paneId)
    {
        if (!string.IsNullOrWhiteSpace(paneId))
            _panes.TryRemove(paneId, out _);
    }

    private PaneAcks Pane(string paneId) => _panes.GetOrAdd(paneId, static _ => new PaneAcks());

    private sealed class PaneAcks
    {
        private readonly object _gate = new();
        private ImmutableDictionary<string, long> _current =
            ImmutableDictionary.Create<string, long>(StringComparer.Ordinal);

        private readonly Dictionary<string, long> _posted = new(StringComparer.Ordinal);
        private long _started;
        private long _completed;
        private int _held;
        private int _ackOwed;
        private long _oweVersion;
        private DateTimeOffset? _holdSince;

        public ImmutableDictionary<string, long> Current => Volatile.Read(ref _current);

        public long StartedCount => Interlocked.Read(ref _started);

        public void BeginWrite() => Interlocked.Increment(ref _started);

        /// <summary>Returns true when a held paint must run now.</summary>
        public bool EndWrite()
        {
            Interlocked.Increment(ref _completed);
            return Interlocked.Exchange(ref _held, 0) != 0;
        }

        public InputCaptureWindow Open(DateTimeOffset now, TimeSpan maxHold, bool mayHold)
        {
            // Completed first: a write that starts between the two reads
            // makes them differ, so the window is not clean.
            var completed = Interlocked.Read(ref _completed);
            var started = Interlocked.Read(ref _started);
            if (started == completed)
            {
                if (mayHold)
                {
                    lock (_gate)
                        _holdSince = null;
                }

                return new InputCaptureWindow(started, InputCaptureState.Clean);
            }

            if (!mayHold)
                return new InputCaptureWindow(started, InputCaptureState.Uncertain);

            lock (_gate)
            {
                // A wall clock that moves back restarts the hold. It never
                // makes the hold longer than one more bound.
                if (_holdSince is not { } since || now < since)
                {
                    _holdSince = now;
                    since = now;
                }

                return now - since >= maxHold
                    ? new InputCaptureWindow(started, InputCaptureState.Uncertain)
                    : new InputCaptureWindow(started, InputCaptureState.Hold);
            }
        }

        private bool Idle => Interlocked.Read(ref _completed) == Interlocked.Read(ref _started);

        public bool AckOwedAndIdle => Volatile.Read(ref _ackOwed) != 0 && Idle;

        /// <summary>Marks the owed ack. Returns true when every write already ended.</summary>
        public bool OweAck()
        {
            Interlocked.Increment(ref _oweVersion);
            Volatile.Write(ref _ackOwed, 1);
            return Idle;
        }

        /// <summary>The version of the owed ack, or null when no ack is owed.</summary>
        public long? PeekOwed() =>
            Volatile.Read(ref _ackOwed) != 0 ? Interlocked.Read(ref _oweVersion) : null;

        /// <summary>Clears the owed flag when no new ack became owed after <paramref name="version"/>.</summary>
        public void ClearOwed(long version)
        {
            lock (_gate)
            {
                if (Interlocked.Read(ref _oweVersion) == version)
                    Volatile.Write(ref _ackOwed, 0);
            }
        }

        public List<KeyValuePair<string, long>> Unposted()
        {
            var current = Current;
            var list = new List<KeyValuePair<string, long>>(current.Count);
            lock (_gate)
            {
                foreach (var (connection, seq) in current)
                {
                    if (!_posted.TryGetValue(connection, out var posted) || posted < seq)
                        list.Add(new KeyValuePair<string, long>(connection, seq));
                }
            }

            return list;
        }

        public void NotePosted(string connectionId, long inputAck)
        {
            lock (_gate)
            {
                if (!_posted.TryGetValue(connectionId, out var prior) || prior < inputAck)
                    _posted[connectionId] = inputAck;
            }
        }

        /// <summary>Returns true when every write already ended.</summary>
        public bool Hold()
        {
            Volatile.Write(ref _held, 1);
            if (Interlocked.Read(ref _completed) != Interlocked.Read(ref _started))
                return false;
            // The last write ended before the flag was set. Take the flag back
            // so exactly one side runs the paint.
            return Interlocked.Exchange(ref _held, 0) != 0;
        }

        public void Note(InputSeqStamp stamp)
        {
            lock (_gate)
            {
                if (_current.TryGetValue(stamp.ConnectionId, out var prior) && prior >= stamp.Seq)
                    return;
                Volatile.Write(ref _current, _current.SetItem(stamp.ConnectionId, stamp.Seq));
            }
        }

        public void Forget(string connectionId)
        {
            lock (_gate)
            {
                if (_current.ContainsKey(connectionId))
                    Volatile.Write(ref _current, _current.Remove(connectionId));
                _posted.Remove(connectionId);
            }
        }
    }
}
