using System.Diagnostics;
using System.Globalization;

namespace Hypa.Continuity.Tests.Impairment;

/// <summary>
/// One direction of an impaired link. A packet waits for the bottleneck (when
/// a cap is set), then for the one-way delay plus jitter. The line keeps the
/// packet order. A dedicated thread delivers each packet at its due time, so
/// a busy thread pool does not add delay.
/// </summary>
internal sealed class DelayLine : IDisposable
{
    private readonly ImpairmentProfile _profile;
    private readonly bool _stream;
    private readonly Action<byte[]?> _deliver;
    private readonly Random _random;
    private readonly long _oneWayTicks;
    private readonly long _jitterTicks;
    private readonly long _lossPenaltyTicks;
    private readonly double _ticksPerByte;
    private readonly object _gate = new();
    private readonly Queue<Item> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly AutoResetEvent _roomFreed = new(false);
    private readonly Thread _thread;
    private long _linkFreeAt;
    private long _lastDue;
    private long _bytesInLine;
    private volatile bool _stopped;

    /// <param name="profile">The link.</param>
    /// <param name="stream">True for a TCP byte stream: a loss stalls the stream and nothing is dropped.</param>
    /// <param name="seed">The seed for this direction.</param>
    /// <param name="deliver">Writes one packet to the far side. Null marks the end of the stream.</param>
    /// <param name="name">The thread name.</param>
    public DelayLine(ImpairmentProfile profile, bool stream, int seed, Action<byte[]?> deliver, string name)
    {
        _profile = profile;
        _stream = stream;
        _deliver = deliver;
        _random = new Random(seed);
        _oneWayTicks = ToTicks(profile.Rtt / 2);
        _jitterTicks = ToTicks(profile.Jitter);
        _lossPenaltyTicks = ToTicks(profile.TcpLossPenalty);
        _ticksPerByte = profile.BandwidthBytesPerSecond > 0
            ? (double)Stopwatch.Frequency / profile.BandwidthBytesPerSecond
            : 0;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public long Packets { get; private set; }

    public long Bytes { get; private set; }

    /// <summary>UDP datagrams that the loss draw dropped.</summary>
    public long Lost { get; private set; }

    /// <summary>UDP datagrams that did not fit the bottleneck queue.</summary>
    public long QueueDrops { get; private set; }

    /// <summary>TCP segments that the loss draw stalled.</summary>
    public long Stalls { get; private set; }

    /// <summary>The error that stopped the delivery, or null while the line runs.</summary>
    public string? Fault { get; private set; }

    /// <summary>The state of the line, for a report when a probe waits too long.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var headWaitMs = _queue.TryPeek(out var head)
                ? (head.Due - now) * 1000.0 / Stopwatch.Frequency
                : 0;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"packets={Packets} bytes={Bytes} queued={_queue.Count}/{_bytesInLine}B head_due_in={headWaitMs:0.#}ms stopped={_stopped} fault={Fault ?? "none"}");
        }
    }

    /// <summary>
    /// The bytes that a TCP reader can put in the line now: the free space of
    /// the bottleneck queue, or, with no cap, of the window. Zero or less
    /// means the reader must wait.
    /// </summary>
    public long RoomBytes
    {
        get
        {
            lock (_gate)
                return RoomBytesLocked(Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Blocks the TCP reader until the line has room for
    /// <paramref name="bytes"/>, or until <paramref name="stop"/>. Returns the
    /// room. With no cap, the room comes when the line delivers a packet, and
    /// the delivery signals it. With a cap, the bottleneck queue drains at the
    /// link rate, so the room comes at a time that the model gives. The
    /// reader waits for that due time, as the delivery thread does.
    /// </summary>
    public long WaitForRoom(long bytes, CancellationToken stop)
    {
        while (true)
        {
            long now;
            long room;
            long due;
            lock (_gate)
            {
                now = Stopwatch.GetTimestamp();
                room = RoomBytesLocked(now);
                // The backlog is at most QueueLimitBytes - bytes from this time.
                due = _ticksPerByte > 0
                    ? _linkFreeAt - (long)((_profile.QueueLimitBytes - bytes) * _ticksPerByte)
                    : long.MaxValue;
            }

            if (room >= bytes || stop.IsCancellationRequested)
                return room;

            try
            {
                if (due == long.MaxValue)
                {
                    WaitHandle.WaitAny([_roomFreed, stop.WaitHandle]);
                    continue;
                }

                var ms = (due - now) * 1000 / Stopwatch.Frequency;
                if (ms >= 2)
                {
                    // Wait most of the time, then spin the rest for a precise
                    // due time. A new packet only moves the due time later.
                    WaitHandle.WaitAny([_roomFreed, stop.WaitHandle], (int)(ms - 1));
                    continue;
                }

                var spin = new SpinWait();
                while (Stopwatch.GetTimestamp() < due && !stop.IsCancellationRequested)
                    spin.SpinOnce(sleep1Threshold: -1);
            }
            catch (ObjectDisposedException)
            {
                return room;
            }
        }
    }

    /// <summary>
    /// Puts one packet in the line. Returns false when the packet is dropped.
    /// The line copies the bytes.
    /// </summary>
    public bool Enqueue(ReadOnlySpan<byte> packet)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            Packets++;
            var departs = now;
            if (_ticksPerByte > 0)
            {
                if (!_stream && BacklogBytesLocked(now) + packet.Length > _profile.QueueLimitBytes)
                {
                    QueueDrops++;
                    return false;
                }

                departs = Math.Max(now, _linkFreeAt) + (long)(packet.Length * _ticksPerByte);
                _linkFreeAt = departs;
            }

            var delay = _oneWayTicks;
            if (_jitterTicks > 0)
                delay += (long)((_random.NextDouble() * 2 - 1) * _jitterTicks);
            if (_profile.LossRate > 0 && _random.NextDouble() < _profile.LossRate)
            {
                if (!_stream)
                {
                    Lost++;
                    return false;
                }

                Stalls++;
                delay += _lossPenaltyTicks;
            }

            var due = Math.Max(_lastDue, departs + Math.Max(0, delay));
            _lastDue = due;
            _queue.Enqueue(new Item(packet.ToArray(), due));
            _bytesInLine += packet.Length;
            Bytes += packet.Length;
        }

        _wake.Set();
        return true;
    }

    /// <summary>Marks the end of the stream. The far side gets it after the bytes before it.</summary>
    public void EnqueueEnd()
    {
        lock (_gate)
            _queue.Enqueue(new Item(null, Math.Max(_lastDue, Stopwatch.GetTimestamp())));
        _wake.Set();
    }

    public void Dispose()
    {
        _stopped = true;
        _wake.Set();
        _roomFreed.Set();
        if (Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
        _roomFreed.Dispose();
    }

    private long RoomBytesLocked(long now) =>
        _ticksPerByte > 0
            ? _profile.QueueLimitBytes - BacklogBytesLocked(now)
            : _profile.TcpWindowBytes - _bytesInLine;

    private long BacklogBytesLocked(long now) =>
        _linkFreeAt <= now ? 0 : (long)((_linkFreeAt - now) / _ticksPerByte);

    private void Run()
    {
        while (!_stopped)
        {
            bool any;
            Item item;
            lock (_gate)
                any = _queue.TryPeek(out item);

            if (!any)
            {
                WaitSafe(50);
                continue;
            }

            var wait = item.Due - Stopwatch.GetTimestamp();
            if (wait > 0)
            {
                var ms = wait * 1000 / Stopwatch.Frequency;
                if (ms >= 2)
                {
                    // Sleep most of the wait, then spin the rest for a precise
                    // due time. The head cannot move earlier: due times only grow.
                    WaitSafe((int)(ms - 1));
                    continue;
                }

                var spin = new SpinWait();
                while (Stopwatch.GetTimestamp() < item.Due && !_stopped)
                    spin.SpinOnce(sleep1Threshold: -1);
            }

            var data = item.Data;
            lock (_gate)
            {
                _queue.Dequeue();
                if (data is not null)
                {
                    _bytesInLine -= data.Length;
                    // A byte stream takes every segment that is due now in one
                    // write, so the line keeps up with a fast sender.
                    if (_stream)
                        data = TakeDueSegmentsLocked(data);
                }
            }

            // The bytes left the line. A reader that waits for room can go on.
            if (data is not null && !SignalSafe(_roomFreed))
                return;

            try
            {
                _deliver(data);
            }
            catch (Exception) when (_stopped)
            {
                return;
            }
            catch (Exception ex)
            {
                // The far side closed. Drop the rest of the line.
                Fault = ex.GetType().Name + ": " + ex.Message;
                _stopped = true;
                return;
            }
        }
    }

    private byte[] TakeDueSegmentsLocked(byte[] first)
    {
        var now = Stopwatch.GetTimestamp();
        if (!_queue.TryPeek(out var next) || next.Data is null || next.Due > now)
            return first;
        var joined = new List<byte>(first);
        while (joined.Count < 256 * 1024
            && _queue.TryPeek(out next)
            && next.Data is not null
            && next.Due <= now)
        {
            _queue.Dequeue();
            _bytesInLine -= next.Data.Length;
            joined.AddRange(next.Data);
        }

        return [.. joined];
    }

    private void WaitSafe(int milliseconds)
    {
        try
        {
            _wake.WaitOne(milliseconds);
        }
        catch (ObjectDisposedException)
        {
            _stopped = true;
        }
    }

    private bool SignalSafe(AutoResetEvent signal)
    {
        try
        {
            signal.Set();
            return true;
        }
        catch (ObjectDisposedException)
        {
            _stopped = true;
            return false;
        }
    }

    private static long ToTicks(TimeSpan span) =>
        span <= TimeSpan.Zero ? 0 : (long)(span.TotalSeconds * Stopwatch.Frequency);

    private readonly record struct Item(byte[]? Data, long Due);
}
