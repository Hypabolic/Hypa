using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Latency;

/// <summary>One measured hop of the attach client.</summary>
public enum AttachLatencyHop
{
    Decode,
    StdinGateWait,
    RenderGateWait,
    PaintWait,
    InputWire,
    ClientRecv,

    /// <summary>
    /// The decode of the typed cells object from the event that the render
    /// loop took. The time from the socket read to the typed object is
    /// <see cref="ClientRecv"/> plus this hop.
    /// </summary>
    ClientDecode,
    Apply,
    Compose,
    Encode,
    HostWrite,

    /// <summary>From the stdin read of a key to the host write of its shown prediction.</summary>
    PredictedPaint,
}

/// <summary>
/// Keys of one stdin read that the sender put in one batch. The read time is
/// a value of <see cref="AttachLatencyMeter.Now"/>. <see cref="Mark"/> is the
/// input mark of the echo predictor, or 0.
/// </summary>
public readonly record struct AttachKeyStamp(long ReadTimestamp, int KeyCount, long Mark = 0)
{
    public bool IsEmpty => KeyCount <= 0;
}

/// <summary>
/// Keys of one stdin read that a painted frame closed. <see cref="ReadTimestamp"/>
/// is the read time of the stamp. <see cref="KeyToPaintUs"/> is the value that
/// the meter puts in its key-to-paint histogram for each of the
/// <see cref="KeyCount"/> keys.
/// </summary>
public readonly record struct AttachKeyClosed(
    string PaneId,
    long Seq,
    long ReadTimestamp,
    int KeyCount,
    long KeyToPaintUs);

/// <summary>
/// Always-on latency meter of one attach. The attach owns one instance.
/// All durations come from one monotonic clock (<see cref="TimeProvider.GetTimestamp"/>).
/// A key opens when the client reads it from stdin. It closes when the client
/// writes a frame of its pane to the host and that frame has an
/// <c>input_ack</c> at or above the key seq. Only a frame that changed cells
/// or the cursor closes a key.
/// </summary>
public sealed class AttachLatencyMeter
{
    /// <summary>A key that is still open after this time counts as a stale key.</summary>
    public static readonly TimeSpan StaleKeyAge = TimeSpan.FromMilliseconds(250);

    public const int DefaultMaxOpenBatches = 1024;

    private static readonly AttachLatencyHop[] Hops = Enum.GetValues<AttachLatencyHop>();

    private readonly TimeProvider _time;
    private readonly long _staleTicks;
    private readonly LatencyHistogram _keyToPaint = new();
    private readonly LatencyHistogram[] _hops;
    private readonly object _gate = new();
    private readonly OpenBatch[] _open;
    private long _seq;
    private int _openCount;
    private long _keysRead;
    private long _keysSent;
    private long _keysAcked;
    private long _staleKeys;
    private long _droppedKeys;
    private int _ackUnavailable;
    private long _predictionsShown;
    private long _predictionsMispredicted;
    private readonly Action<AttachKeyClosed>? _keyClosed;

    /// <summary>
    /// Gets the seq of each sent batch with the input marks of its keys. The
    /// attach session sets the echo predictor here once.
    /// </summary>
    internal IInputSeqObserver? SeqObserver { get; set; }

    /// <summary>The echo predictor showed <paramref name="count"/> more predictions.</summary>
    public void NotePredictionsShown(int count) => Interlocked.Add(ref _predictionsShown, count);

    /// <summary>A mismatch or an expiry deleted <paramref name="count"/> shown predictions.</summary>
    public void NotePredictionsMispredicted(int count) => Interlocked.Add(ref _predictionsMispredicted, count);

    /// <param name="time">The meter clock. The default is the system clock.</param>
    /// <param name="maxOpenBatches">The limit of open batches.</param>
    /// <param name="keyClosed">
    /// Optional. Gets the keys of each closed stamp with their exact
    /// key-to-paint time, after the meter releases its lock. A bench uses it
    /// to keep the time of each key.
    /// </param>
    public AttachLatencyMeter(
        TimeProvider? time = null,
        int maxOpenBatches = DefaultMaxOpenBatches,
        Action<AttachKeyClosed>? keyClosed = null)
    {
        _time = time ?? TimeProvider.System;
        _keyClosed = keyClosed;
        _staleTicks = (long)(StaleKeyAge.TotalSeconds * _time.TimestampFrequency);
        _open = new OpenBatch[Math.Max(1, maxOpenBatches)];
        _hops = new LatencyHistogram[Hops.Length];
        for (var i = 0; i < _hops.Length; i++)
            _hops[i] = new LatencyHistogram();
    }

    /// <summary>The meter clock. Pass a value of it to <see cref="RecordHop"/>.</summary>
    public long Now() => _time.GetTimestamp();

    /// <summary>Host paint counters: patch paints, full composes, no-op paints and fallback reasons.</summary>
    internal HostPaintCounters Paint { get; } = new();

    /// <summary>
    /// The read time of the stdin chunk that the input loop handles now. The
    /// input loop is the only writer.
    /// </summary>
    public long CurrentReadTimestamp { get; private set; }

    /// <summary>Counts the keys of one stdin read and keeps its read time.</summary>
    public void NoteStdinRead(ReadOnlySpan<byte> decoded, long readTimestamp)
    {
        CurrentReadTimestamp = readTimestamp;
        var keys = CountKeys(decoded);
        if (keys > 0)
            Interlocked.Add(ref _keysRead, keys);
    }

    /// <summary>A stamp for pane bytes from the current stdin read.</summary>
    public AttachKeyStamp StampKeys(ReadOnlySpan<byte> paneBytes) =>
        new(CurrentReadTimestamp == 0 ? Now() : CurrentReadTimestamp, CountKeys(paneBytes));

    /// <summary>Records the time from <paramref name="startTimestamp"/> to now.</summary>
    public void RecordHop(AttachLatencyHop hop, long startTimestamp) =>
        RecordElapsed(hop, Now() - startTimestamp);

    /// <summary>
    /// Records the time since a <see cref="System.Diagnostics.Stopwatch"/>
    /// timestamp that another component took. Zero means unknown.
    /// </summary>
    public void RecordSinceStopwatch(AttachLatencyHop hop, long stopwatchStart)
    {
        if (stopwatchStart == 0)
            return;
        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - stopwatchStart;
        RecordElapsed(
            hop,
            (long)(elapsed * (double)_time.TimestampFrequency / System.Diagnostics.Stopwatch.Frequency));
    }

    public void RecordElapsed(AttachLatencyHop hop, long elapsedTicks)
    {
        if (elapsedTicks < 0)
            return;
        _hops[(int)hop].Record(ToMicroseconds(elapsedTicks));
        // A zero wait (a free paint lock) goes in the histogram only. The
        // trace keeps the hops that took time.
        if (elapsedTicks > 0 && AttachPathTrace.IsEnabled)
        {
            var domain = hop is AttachLatencyHop.Decode or AttachLatencyHop.StdinGateWait or AttachLatencyHop.InputWire
                ? AttachPathTrace.DomainInput
                : AttachPathTrace.DomainOutput;
            AttachPathTrace.RecordHop(
                domain,
                TraceStage(hop),
                paneId: null,
                (long)(elapsedTicks * (double)System.Diagnostics.Stopwatch.Frequency / _time.TimestampFrequency));
        }
    }

    /// <summary>
    /// The next input seq. One counter serves every pane, so the seq is
    /// monotonic for each pane for the life of the attach, and the meter
    /// keeps no state for each pane it ever saw.
    /// </summary>
    public long NextSeq() => Interlocked.Increment(ref _seq);

    /// <summary>
    /// Records keys that the sender put on the wire. With a null
    /// <paramref name="seq"/> the peer has no input ack: the keys count as
    /// sent, the meter reports <c>ack_unavailable</c>, and no key stays open.
    /// </summary>
    public void NoteSent(string paneId, long? seq, ReadOnlySpan<AttachKeyStamp> stamps)
    {
        var keys = 0;
        long maxMark = 0;
        foreach (var stamp in stamps)
        {
            keys += Math.Max(0, stamp.KeyCount);
            maxMark = Math.Max(maxMark, stamp.Mark);
        }

        if (maxMark > 0)
            SeqObserver?.OnSeqAssigned(paneId, seq, maxMark);
        if (seq is null)
        {
            Volatile.Write(ref _ackUnavailable, 1);
            if (keys > 0)
                Interlocked.Add(ref _keysSent, keys);
            return;
        }

        Volatile.Write(ref _ackUnavailable, 0);
        if (keys == 0)
            return;
        Interlocked.Add(ref _keysSent, keys);
        lock (_gate)
        {
            foreach (var stamp in stamps)
            {
                if (stamp.IsEmpty)
                    continue;
                if (_openCount == _open.Length)
                    EvictOldestLocked();
                _open[_openCount++] = new OpenBatch(paneId, seq.Value, stamp.ReadTimestamp, stamp.KeyCount, Stale: false);
            }
        }
    }

    /// <summary>
    /// A frame of <paramref name="paneId"/> reached the host. Closes every
    /// open key of the pane with a seq at or below <paramref name="inputAck"/>.
    /// The caller passes only a frame that changed cells or the cursor.
    /// </summary>
    public void NoteInputAck(string paneId, long inputAck)
    {
        var now = Now();
        List<AttachKeyClosed>? closed = null;
        List<(string PaneId, long Elapsed)>? traced = null;
        lock (_gate)
        {
            // A key that waited 250 ms or more is stale, also when this ack
            // closes it before the next report.
            MarkStaleLocked(now);
            var kept = 0;
            for (var i = 0; i < _openCount; i++)
            {
                var batch = _open[i];
                if (batch.Seq <= inputAck && string.Equals(batch.PaneId, paneId, StringComparison.Ordinal))
                {
                    var us = ToMicroseconds(now - batch.ReadTimestamp);
                    for (var k = 0; k < batch.KeyCount; k++)
                        _keyToPaint.Record(us);
                    if (AttachPathTrace.IsEnabled)
                        (traced ??= []).Add((batch.PaneId, now - batch.ReadTimestamp));
                    _keysAcked += batch.KeyCount;
                    if (_keyClosed is not null)
                        (closed ??= []).Add(new AttachKeyClosed(batch.PaneId, batch.Seq, batch.ReadTimestamp, batch.KeyCount, us));
                    continue;
                }

                _open[kept++] = batch;
            }

            Array.Clear(_open, kept, _openCount - kept);
            _openCount = kept;
        }

        // One record for each closed key batch, so a trace can find the time
        // of a slow key and join it to the mux. Written outside the lock.
        if (traced is not null)
        {
            foreach (var (tracedPane, elapsed) in traced)
            {
                AttachPathTrace.RecordHop(
                    AttachPathTrace.DomainInput,
                    AttachPathTrace.StageKeyToPaint,
                    tracedPane,
                    (long)(elapsed * (double)System.Diagnostics.Stopwatch.Frequency / _time.TimestampFrequency));
            }
        }

        if (closed is null)
            return;
        foreach (var batch in closed)
            _keyClosed!(batch);
    }

    /// <summary>
    /// Drops the open keys. A retarget or a discard of queued input calls
    /// this: those keys can never get an ack on the new connection.
    /// </summary>
    public void DropOpen()
    {
        SeqObserver?.OnInputDropped();
        lock (_gate)
        {
            MarkStaleLocked(Now());
            for (var i = 0; i < _openCount; i++)
                _droppedKeys += _open[i].KeyCount;
            Array.Clear(_open, 0, _openCount);
            _openCount = 0;
        }
    }

    /// <summary>Drops the open keys of one batch whose send failed.</summary>
    public void DropOpen(string paneId, long seq)
    {
        SeqObserver?.OnInputDropped();
        lock (_gate)
        {
            MarkStaleLocked(Now());
            var kept = 0;
            for (var i = 0; i < _openCount; i++)
            {
                var batch = _open[i];
                if (batch.Seq == seq && string.Equals(batch.PaneId, paneId, StringComparison.Ordinal))
                {
                    _droppedKeys += batch.KeyCount;
                    continue;
                }

                _open[kept++] = batch;
            }

            Array.Clear(_open, kept, _openCount - kept);
            _openCount = kept;
        }
    }

    /// <summary>Clears the histograms and the counters. Seq values continue.</summary>
    public void Reset()
    {
        _keyToPaint.Reset();
        foreach (var hop in _hops)
            hop.Reset();
        Paint.Reset();
        lock (_gate)
        {
            Array.Clear(_open, 0, _openCount);
            _openCount = 0;
            _keysAcked = 0;
            _staleKeys = 0;
            _droppedKeys = 0;
        }

        Interlocked.Exchange(ref _keysRead, 0);
        Interlocked.Exchange(ref _keysSent, 0);
        Interlocked.Exchange(ref _predictionsShown, 0);
        Interlocked.Exchange(ref _predictionsMispredicted, 0);
    }

    public AttachLatencyReport Report()
    {
        var now = Now();
        int open;
        long acked;
        long stale;
        long dropped;
        lock (_gate)
        {
            MarkStaleLocked(now);
            open = 0;
            for (var i = 0; i < _openCount; i++)
                open += _open[i].KeyCount;
            acked = _keysAcked;
            stale = _staleKeys;
            dropped = _droppedKeys;
        }

        var hops = new Dictionary<string, LatencyHistogramReport>(StringComparer.Ordinal);
        foreach (var hop in Hops)
            hops[HopName(hop)] = _hops[(int)hop].Report();
        return new AttachLatencyReport
        {
            AckUnavailable = Volatile.Read(ref _ackUnavailable) != 0,
            KeysRead = Interlocked.Read(ref _keysRead),
            KeysSent = Interlocked.Read(ref _keysSent),
            KeysAcked = acked,
            OpenKeys = open,
            StaleKey = stale,
            DroppedKeys = dropped,
            KeyToPaint = _keyToPaint.Report(),
            Gc = ProcessGcSnapshot.Capture(),
            Hops = hops,
            PaintCounters = Paint.Report(),
            PredictedCellsShown = Interlocked.Read(ref _predictionsShown),
            MispredictedCellsShown = Interlocked.Read(ref _predictionsMispredicted),
        };
    }

    /// <summary>The report name of a hop.</summary>
    public static string HopName(AttachLatencyHop hop) => hop switch
    {
        AttachLatencyHop.Decode => "decode",
        AttachLatencyHop.StdinGateWait => "gate-wait-stdin",
        AttachLatencyHop.RenderGateWait => "gate-wait-render",
        AttachLatencyHop.PaintWait => "paint-wait",
        AttachLatencyHop.InputWire => "input-wire",
        AttachLatencyHop.ClientRecv => "client-recv",
        AttachLatencyHop.ClientDecode => "client-decode",
        AttachLatencyHop.Apply => "apply",
        AttachLatencyHop.Compose => "compose",
        AttachLatencyHop.Encode => "encode",
        AttachLatencyHop.HostWrite => "host-write",
        AttachLatencyHop.PredictedPaint => "predicted-paint",
        _ => hop.ToString(),
    };

    private static string TraceStage(AttachLatencyHop hop) => hop switch
    {
        AttachLatencyHop.Decode => AttachPathTrace.StageDecode,
        AttachLatencyHop.StdinGateWait or AttachLatencyHop.RenderGateWait => AttachPathTrace.StageGateWait,
        AttachLatencyHop.PaintWait => AttachPathTrace.StagePaintWait,
        AttachLatencyHop.InputWire => AttachPathTrace.StageInputWire,
        AttachLatencyHop.ClientRecv => AttachPathTrace.StageClientRecv,
        AttachLatencyHop.ClientDecode => AttachPathTrace.StageClientDecode,
        AttachLatencyHop.Apply => AttachPathTrace.StageApply,
        AttachLatencyHop.Compose => AttachPathTrace.StageCompose,
        AttachLatencyHop.Encode => AttachPathTrace.StageEncode,
        _ => AttachPathTrace.StageHostWrite,
    };

    /// <summary>
    /// Counts keys in decoded stdin bytes. An escape sequence (CSI, SS3, or
    /// ESC with one byte) is one key. A UTF-8 sequence is one key. Each other
    /// byte is one key.
    /// </summary>
    internal static int CountKeys(ReadOnlySpan<byte> bytes)
    {
        var keys = 0;
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i];
            keys++;
            i++;
            if (b == 0x1b)
            {
                if (i >= bytes.Length)
                    break;
                var next = bytes[i++];
                if (next == (byte)'[')
                {
                    while (i < bytes.Length && bytes[i] is >= 0x20 and <= 0x3f)
                        i++;
                    if (i < bytes.Length)
                        i++;
                }
                else if (next == (byte)'O' && i < bytes.Length)
                {
                    i++;
                }
            }
            else if (b >= 0xc0)
            {
                while (i < bytes.Length && (bytes[i] & 0xc0) == 0x80)
                    i++;
            }
        }

        return keys;
    }

    private long ToMicroseconds(long ticks) =>
        ticks <= 0 ? 0 : (long)(ticks * 1_000_000.0 / _time.TimestampFrequency);

    private void MarkStaleLocked(long now)
    {
        for (var i = 0; i < _openCount; i++)
        {
            ref var batch = ref _open[i];
            if (batch.Stale || now - batch.ReadTimestamp < _staleTicks)
                continue;
            batch = batch with { Stale = true };
            _staleKeys += batch.KeyCount;
        }
    }

    /// <summary>
    /// The open-key limit removes the oldest batch. It counts as dropped. It
    /// counts as stale only when it reached the stale age.
    /// </summary>
    private void EvictOldestLocked()
    {
        MarkStaleLocked(Now());
        var oldest = _open[0];
        _droppedKeys += oldest.KeyCount;
        Array.Copy(_open, 1, _open, 0, _openCount - 1);
        _open[--_openCount] = default;
    }

    private readonly record struct OpenBatch(
        string PaneId,
        long Seq,
        long ReadTimestamp,
        int KeyCount,
        bool Stale);
}

/// <summary>The meter JSON that <c>latency.report</c> returns.</summary>
public sealed record AttachLatencyReport
{
    /// <summary>True when the last batch went to a peer without <c>hypa.attach.input_ack.v2</c>.</summary>
    public bool AckUnavailable { get; init; }

    /// <summary>Keys that the client read from stdin.</summary>
    public long KeysRead { get; init; }

    /// <summary>Keys that the input sender put on the wire to a pane.</summary>
    public long KeysSent { get; init; }

    /// <summary>Keys that a painted frame closed.</summary>
    public long KeysAcked { get; init; }

    /// <summary>Keys that are open now.</summary>
    public long OpenKeys { get; init; }

    /// <summary>Keys that were open for 250 ms or more. A stale key that closes later is also in <see cref="KeyToPaint"/>.</summary>
    public long StaleKey { get; init; }

    /// <summary>Open keys that a retarget, a discard, a failed send, or the open-key limit removed.</summary>
    public long DroppedKeys { get; init; }

    /// <summary>Time from the stdin read of a key to the host write of a frame that acks it.</summary>
    public required LatencyHistogramReport KeyToPaint { get; init; }

    /// <summary>Per-hop histograms, by hop name.</summary>
    public required Dictionary<string, LatencyHistogramReport> Hops { get; init; }

    /// <summary>Host paint counters, by name: patch paints, full composes, no-op paints and each patch fallback reason.</summary>
    public Dictionary<string, long> PaintCounters { get; init; } = [];

    /// <summary>Predicted cells and cursor moves that a host paint showed.</summary>
    public long PredictedCellsShown { get; init; }

    /// <summary>Shown predictions that a mismatch or an expiry deleted.</summary>
    public long MispredictedCellsShown { get; init; }

    /// <summary>The GC totals of the attach process at the time of the report.</summary>
    public ProcessGcSnapshot? Gc { get; init; }

    /// <summary>
    /// The counters of the echo predictor since the attach started, by name.
    /// <c>latency.reset</c> does not clear them. <c>reset.REASON</c> counts
    /// each reset call, also when no epoch was there to delete.
    /// </summary>
    public Dictionary<string, long> PredictionCounters { get; init; } = [];
}
