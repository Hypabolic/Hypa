using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Logging;

/// <summary>
/// Keeps file I/O off the product threads. <see cref="Write"/> only puts the
/// record in a bounded in-memory queue. One background thread takes all
/// queued records, writes them to the inner sink, and flushes once for each
/// batch.
/// <list type="bullet">
/// <item>Order: the queue is first in, first out, and one thread writes it.
/// Thus the lines are in the order of the <see cref="Write"/> calls.</item>
/// <item>Bound: <see cref="DefaultCapacity"/> records. When the queue is full,
/// the sink drops the new record and counts it. Before the next records, the
/// writer writes one <see cref="ProcessLogEvents.LogSinkDropped"/> record at
/// <see cref="ProcessLogLevel.Warning"/> with the count. A full queue accepts
/// no record until the writer takes the full queue, so that record is at the
/// position of the gap.</item>
/// <item>Flush: <see cref="Flush"/> returns true when each record accepted
/// before the call has gone through the inner sink and its file flush. The
/// data is then in the operating system, not on the disk. This is the same
/// guarantee as the synchronous sink. It does not call fsync. After the inner
/// sink threw, or lost a record of a batch, <see cref="Flush"/> always
/// returns false, because a record before the call was not written.</item>
/// <item>Time: the flush and drain deadlines are timers of the
/// <see cref="TimeProvider"/>, so a test can move them with a manual
/// clock.</item>
/// <item>Exit: <see cref="Dispose"/> stops new records, drains the queue, and
/// disposes the inner sink. It waits at most the drain timeout, so a stalled
/// disk does not stop the process from exiting. The process exit signal
/// (normal exit, SIGTERM, unhandled exception) calls <see cref="Flush"/>
/// with <see cref="ExitFlushTimeout"/>. The sink keeps that callback until
/// the writer is done, so an exit after a slow <see cref="Dispose"/> also
/// gets one more flush.</item>
/// </list>
/// </summary>
public sealed class QueuedProcessLogSink : IProcessLogSink, IDisposable
{
    /// <summary>
    /// The queue bound in records. One record is about 200 to 400 bytes, so
    /// a full queue is about 1 to 2 MB.
    /// </summary>
    public const int DefaultCapacity = 4096;

    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan ExitFlushTimeout = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly IProcessLogSink _inner;
    private readonly int _capacity;
    private readonly TimeProvider _time;
    private readonly TimeSpan _drainTimeout;
    private readonly IDisposable? _exitRegistration;
    private List<ProcessLogRecord> _pending = [];
    private Thread? _writer;
    private long _accepted;
    private long _written;
    private long _pendingDrops;
    private long _droppedTotal;
    private DateTimeOffset _firstDropAt;
    private bool _completing;
    private bool _writerDone;
    private bool _writeFailed;
    private int _disposed;
    private int _innerDisposed;
    private int _exitReleased;

    public QueuedProcessLogSink(
        IProcessLogSink inner,
        int capacity = DefaultCapacity,
        TimeProvider? timeProvider = null,
        IProcessExitSignal? exitSignal = null,
        TimeSpan? drainTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _inner = inner;
        _capacity = capacity;
        _time = timeProvider ?? TimeProvider.System;
        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        _exitRegistration = exitSignal?.Register(OnProcessExit);
    }

    public bool Disabled => _inner.Disabled;

    public string? Path => _inner.Path;

    public int Capacity => _capacity;

    /// <summary>The number of records that the sink dropped because the queue was full.</summary>
    public long DroppedCount
    {
        get
        {
            lock (_gate)
                return _droppedTotal;
        }
    }

    public bool IsEnabled(ProcessLogLevel level) =>
        Volatile.Read(ref _disposed) == 0 && _inner.IsEnabled(level);

    /// <summary>Puts the record in the queue. Never waits for file I/O.</summary>
    public void Write(ProcessLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsEnabled(record.Level))
            return;

        lock (_gate)
        {
            if (_completing)
                return;

            if (_pending.Count >= _capacity)
            {
                if (_pendingDrops == 0)
                {
                    // The drop record takes one place in the flush count.
                    _firstDropAt = _time.GetUtcNow();
                    _accepted++;
                }

                _pendingDrops++;
                _droppedTotal++;
                return;
            }

            _pending.Add(record);
            _accepted++;
            if (_writer is null)
                StartWriterUnlocked();
            else if (_pending.Count == 1)
                Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Waits until each record accepted before this call has gone through
    /// the inner sink and its flush. Returns false when the time ends first,
    /// when the inner sink is disabled (a write or rotation failed), or when
    /// the inner sink threw or lost a record.
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        long target;
        lock (_gate)
        {
            target = _accepted;
            if (_written >= target)
                return FlushResultUnlocked();
            if (_writerDone || _writer is null || Expired(timeout))
                return false;
        }

        var deadline = new Deadline();
        using var timer = StartDeadline(deadline, timeout);
        lock (_gate)
        {
            while (_written < target)
            {
                if (_writerDone || _writer is null || deadline.Passed)
                    return false;
                Monitor.Wait(_gate);
            }

            return FlushResultUnlocked();
        }
    }

    /// <summary>
    /// Stops new records and drains the queue for at most the drain timeout.
    /// When the writer is not done in that time, it keeps the process exit
    /// callback, so that the exit path tries one more flush. The writer
    /// removes the callback when it is done.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Thread? writer;
        lock (_gate)
        {
            _completing = true;
            writer = _writer;
            Monitor.PulseAll(_gate);
        }

        if (writer is null)
        {
            DisposeInner();
            ReleaseExitRegistration();
            return;
        }

        if (writer != Thread.CurrentThread && WaitForWriter(_drainTimeout))
            ReleaseExitRegistration();
    }

    private bool FlushResultUnlocked() => !_writeFailed && !_inner.Disabled;

    private bool WaitForWriter(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_writerDone)
                return true;
            if (Expired(timeout))
                return false;
        }

        var deadline = new Deadline();
        using var timer = StartDeadline(deadline, timeout);
        lock (_gate)
        {
            while (!_writerDone)
            {
                if (deadline.Passed)
                    return false;
                Monitor.Wait(_gate);
            }

            return true;
        }
    }

    private static bool Expired(TimeSpan timeout) =>
        timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero;

    /// <summary>
    /// Arms a timer of the time provider. When it fires, it marks the
    /// deadline and wakes each waiter on the gate. An infinite timeout arms
    /// no timer.
    /// </summary>
    private ITimer? StartDeadline(Deadline deadline, TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return null;
        return _time.CreateTimer(
            _ =>
            {
                lock (_gate)
                {
                    deadline.Passed = true;
                    Monitor.PulseAll(_gate);
                }
            },
            null,
            timeout,
            Timeout.InfiniteTimeSpan);
    }

    private void StartWriterUnlocked()
    {
        var writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "hypa-process-log",
        };
        _writer = writer;
        writer.Start();
    }

    private void WriterLoop()
    {
        var batch = new List<ProcessLogRecord>();
        try
        {
            while (true)
            {
                int taken;
                lock (_gate)
                {
                    while (_pending.Count == 0 && _pendingDrops == 0 && !_completing)
                        Monitor.Wait(_gate);
                    if (_pending.Count == 0 && _pendingDrops == 0)
                        break;

                    (batch, _pending) = (_pending, batch);
                    if (_pendingDrops > 0)
                    {
                        batch.Add(DropRecord(_pendingDrops, _firstDropAt));
                        _pendingDrops = 0;
                    }

                    taken = batch.Count;
                }

                var complete = WriteToInner(batch);
                batch.Clear();
                lock (_gate)
                {
                    if (!complete)
                        _writeFailed = true;
                    _written += taken;
                    Monitor.PulseAll(_gate);
                }
            }
        }
        finally
        {
            // The queue is empty and the inner sink is closed, so the exit
            // callback has nothing left to flush. Remove it before the writer
            // is marked done: a Dispose that sees the writer done then also
            // sees the callback removed.
            DisposeInner();
            ReleaseExitRegistration();
            lock (_gate)
            {
                _writerDone = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void ReleaseExitRegistration()
    {
        if (Interlocked.Exchange(ref _exitReleased, 1) != 0)
            return;
        try
        {
            _exitRegistration?.Dispose();
        }
        catch
        {
            // A failed unregister must not fail the exit.
        }
    }

    /// <summary>
    /// Writes one batch. Returns false when the inner sink threw or lost a
    /// record. A sink error must not stop the writer, so this does not throw.
    /// </summary>
    private bool WriteToInner(List<ProcessLogRecord> batch)
    {
        if (_inner is IProcessLogBatchSink batchSink)
        {
            try
            {
                return batchSink.WriteBatch(batch) == 0;
            }
            catch
            {
                return false;
            }
        }

        var complete = true;
        foreach (var record in batch)
        {
            try
            {
                _inner.Write(record);
            }
            catch
            {
                // Lose only this record.
                complete = false;
            }
        }

        return complete;
    }

    private ProcessLogRecord DropRecord(long count, DateTimeOffset at) =>
        new()
        {
            Event = ProcessLogEvents.LogSinkDropped,
            Subsystem = ProcessLogEvents.SubsystemLog,
            Outcome = ProcessLogEvents.OutcomeDropped,
            Ts = at,
            Pid = Environment.ProcessId,
            Level = ProcessLogLevel.Warning,
            DropCount = count,
            QueueDepth = _capacity,
        };

    private void DisposeInner()
    {
        if (Interlocked.Exchange(ref _innerDisposed, 1) != 0)
            return;
        if (_inner is not IDisposable disposable)
            return;
        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Sink errors must not fail the exit.
        }
    }

    private void OnProcessExit()
    {
        try
        {
            Flush(ExitFlushTimeout);
        }
        catch
        {
            // The exit path must not throw.
        }
    }

    private sealed class Deadline
    {
        // Read and written under the sink gate.
        public bool Passed;
    }
}
