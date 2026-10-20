using System.Runtime.ExceptionServices;
using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach;

/// <summary>
/// The one writer of host TTY bytes. Each <see cref="UnixRawTerminal"/> has
/// one. All host bytes go through <see cref="Submit"/> in the order of the
/// calls, and the writer writes them to the host in that order.
/// <para>
/// A terminal sink (fd 1, or the native writer seam of the tests) has one
/// writer thread. <see cref="Submit"/> puts the bytes in the queue and
/// returns. Only the writer thread waits for the host terminal. Thus a slow
/// or stalled host terminal does not hold a paint lock or the key feed.
/// </para>
/// <para>
/// A capture sink (a test stream) writes on the caller thread. It cannot
/// stall, and the tests read the stream as soon as the call returns.
/// </para>
/// <para>
/// A failed write stops the queue. The writer keeps the failure and drops
/// the queued bytes, because they change a host screen that is no longer
/// known. Each later <see cref="Submit"/> throws that failure until
/// <see cref="ClearFault"/>. The failure also completes
/// <see cref="Faulted"/>, so an idle attach ends too.
/// </para>
/// </summary>
internal sealed class HostOutputWriter : IDisposable
{
    /// <summary>
    /// Writes a prefix of <paramref name="bytes"/> to the host. Returns the
    /// count that the host took (at least 1), or throws.
    /// </summary>
    internal delegate int HostSink(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// The queued bytes above which <see cref="Submit"/> waits for the
    /// writer thread. A stalled host terminal then stops the paint at this
    /// size, not at the memory limit of the process.
    /// </summary>
    internal const long DefaultMaxPendingBytes = 8L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Queue<Chunk> _queue = new();
    private readonly HostSink _sink;
    private readonly bool _threaded;
    private readonly long _maxPending;
    private readonly TaskCompletionSource _faulted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private readonly List<(long Through, TaskCompletionSource Done)> _writtenWaiters = [];
    private ExceptionDispatchInfo? _fault;
    private long _accepted;
    private long _written;
    private long _pending;
    private bool _disposed;

    private HostOutputWriter(HostSink sink, bool threaded, long maxPendingBytes)
    {
        _sink = sink;
        _threaded = threaded;
        _maxPending = Math.Max(1, maxPendingBytes);
    }

    /// <summary>A writer that writes on the caller thread. For a capture stream.</summary>
    internal static HostOutputWriter Inline(HostSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return new HostOutputWriter(sink, threaded: false, DefaultMaxPendingBytes);
    }

    /// <summary>A writer with its own writer thread. For a host terminal.</summary>
    internal static HostOutputWriter Threaded(HostSink sink, long maxPendingBytes = DefaultMaxPendingBytes)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return new HostOutputWriter(sink, threaded: true, maxPendingBytes);
    }

    /// <summary>True when a writer thread writes the bytes.</summary>
    internal bool IsThreaded => _threaded;

    /// <summary>All bytes that the writer accepted. The host offset of the next byte.</summary>
    internal long Accepted
    {
        get { lock (_gate) return _accepted; }
    }

    /// <summary>The accepted bytes that the host did not take yet.</summary>
    internal long Pending
    {
        get { lock (_gate) return _pending; }
    }

    /// <summary>True after a failed write, until <see cref="ClearFault"/>.</summary>
    internal bool HasFault
    {
        get { lock (_gate) return _fault is not null; }
    }

    /// <summary>
    /// Faults with the first failed write of the writer thread. It never
    /// completes without a failure.
    /// </summary>
    internal Task Faulted => _faulted.Task;

    /// <summary>
    /// Accepts <paramref name="bytes"/> after all bytes accepted before.
    /// Returns the host offset of the first byte. A capture sink writes now.
    /// A terminal sink queues the bytes and returns, unless the queue is
    /// above its limit. <paramref name="traceHost"/> records the bytes on the
    /// path trace with the output route of the caller. <paramref name="slices"/>
    /// records them by compose slice instead.
    /// </summary>
    internal long Submit(
        ReadOnlySpan<byte> bytes,
        bool traceHost = true,
        IReadOnlyList<AttachPathTrace.ComposeSlice>? slices = null)
    {
        if (!_threaded)
            return WriteInline(bytes, traceHost, slices);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _fault?.Throw();
            if (bytes.IsEmpty)
                return _accepted;

            // Backpressure: wait while the queue is full. A single chunk
            // larger than the limit still goes in when the queue is empty.
            while (_pending > 0 && _pending + bytes.Length > _maxPending && _fault is null && !_disposed)
                Monitor.Wait(_gate);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _fault?.Throw();

            var offset = _accepted;
            var trace = slices is { Count: > 0 } || traceHost;
            var context = trace && AttachPathTrace.IsEnabled ? ExecutionContext.Capture() : null;
            _queue.Enqueue(new Chunk(bytes.ToArray(), offset, traceHost, slices, context, trace && context is not null));
            _accepted += bytes.Length;
            _pending += bytes.Length;
            EnsureThreadUnlocked();
            Monitor.PulseAll(_gate);
            return offset;
        }
    }

    /// <summary>
    /// <see cref="Submit"/>, then waits until the host took these bytes.
    /// Throws the failure of this write or of an earlier queued write.
    /// </summary>
    internal void SubmitAndWait(
        ReadOnlySpan<byte> bytes,
        bool traceHost = true,
        IReadOnlyList<AttachPathTrace.ComposeSlice>? slices = null)
    {
        var offset = Submit(bytes, traceHost, slices);
        if (!_threaded)
            return;
        WaitWrittenThrough(offset + bytes.Length);
    }

    /// <summary>
    /// Waits until the host took every accepted byte. Throws a failed write.
    /// </summary>
    internal void Drain()
    {
        if (!_threaded)
            return;
        long through;
        lock (_gate)
            through = _accepted;
        WaitWrittenThrough(through);
    }

    /// <summary>
    /// Completes when the host took every byte accepted before this call.
    /// Bytes accepted after the call do not delay it, so other writers
    /// cannot hold the wait. Faults with a failed write. A capture sink
    /// completes at once.
    /// </summary>
    internal Task WhenWrittenAsync(CancellationToken ct)
    {
        if (!_threaded)
            return Task.CompletedTask;
        Task wait;
        lock (_gate)
        {
            if (_fault is not null)
                return Task.FromException(_fault.SourceException);
            if (_written >= _accepted || _disposed)
                return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _writtenWaiters.Add((_accepted, done));
            wait = done.Task;
        }

        return wait.WaitAsync(ct);
    }

    /// <summary>
    /// Forgets a failed write, so the next write tries the host again.
    /// Restore uses it: its bytes go out even after a failed paint.
    /// Returns true when there was a failure.
    /// </summary>
    internal bool ClearFault()
    {
        lock (_gate)
        {
            var had = _fault is not null;
            _fault = null;
            return had;
        }
    }

    /// <summary>
    /// Writes the queued bytes, then stops the writer thread. Later calls to
    /// <see cref="Submit"/> throw.
    /// </summary>
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            thread = _thread;
            Monitor.PulseAll(_gate);
        }

        // The thread writes what is queued, then ends. A stalled host
        // terminal can hold it: do not wait without a bound.
        thread?.Join(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            foreach (var (_, done) in _writtenWaiters)
                done.TrySetResult();
            _writtenWaiters.Clear();
        }
    }

    private long WriteInline(
        ReadOnlySpan<byte> bytes,
        bool traceHost,
        IReadOnlyList<AttachPathTrace.ComposeSlice>? slices)
    {
        long offset;
        lock (_gate)
            offset = _accepted;
        if (bytes.IsEmpty)
            return offset;
        var accepted = WriteAllCounting(bytes, out var failure);
        lock (_gate)
        {
            _accepted += accepted;
            _written = _accepted;
        }

        NoteTrace(offset, accepted, bytes.Length, traceHost, slices);
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        return offset;
    }

    // A failed write can take a prefix first. Count that prefix.
    private int WriteAllCounting(ReadOnlySpan<byte> bytes, out Exception? failure)
    {
        failure = null;
        var accepted = 0;
        var remaining = bytes;
        try
        {
            while (!remaining.IsEmpty)
            {
                var n = _sink(remaining);
                if (n <= 0 || n > remaining.Length)
                    throw new InvalidOperationException("write to TTY returned a bad count.");
                remaining = remaining[n..];
                accepted += n;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        return accepted;
    }

    private void WaitWrittenThrough(long through)
    {
        lock (_gate)
        {
            while (_written < through && _fault is null && !_disposed)
                Monitor.Wait(_gate);
            _fault?.Throw();
        }
    }

    private void EnsureThreadUnlocked()
    {
        if (_thread is not null)
            return;
        _thread = new Thread(RunWriter)
        {
            IsBackground = true,
            Name = "hypa-host-writer",
        };
        _thread.Start();
    }

    private void RunWriter()
    {
        while (true)
        {
            Chunk chunk;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_disposed)
                    Monitor.Wait(_gate);
                if (_queue.Count == 0)
                    return;
                chunk = _queue.Peek();
            }

            // The host write runs with no lock held.
            var accepted = WriteAllCounting(chunk.Bytes, out var failure);
            if (chunk.Trace)
                TraceOnWriter(chunk, accepted);

            lock (_gate)
            {
                _queue.Dequeue();
                _pending -= chunk.Bytes.Length;
                _written = chunk.Offset + chunk.Bytes.Length;
                if (failure is not null)
                {
                    // The host shows a part of this chunk. Later chunks
                    // change a screen that is not known: drop them.
                    while (_queue.TryDequeue(out var dropped))
                    {
                        _pending -= dropped.Bytes.Length;
                        _written = dropped.Offset + dropped.Bytes.Length;
                    }

                    _fault ??= ExceptionDispatchInfo.Capture(failure);
                    _ = _faulted.TrySetException(failure);
                    foreach (var (_, done) in _writtenWaiters)
                        done.TrySetException(failure);
                    _writtenWaiters.Clear();
                }

                CompleteWrittenUnlocked();
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void CompleteWrittenUnlocked()
    {
        for (var i = _writtenWaiters.Count - 1; i >= 0; i--)
        {
            var (through, done) = _writtenWaiters[i];
            if (through > _written)
                continue;
            done.TrySetResult();
            _writtenWaiters.RemoveAt(i);
        }
    }

    // The path trace keeps its route and capture session in async-local
    // state. Run the record in the context of the caller that queued it.
    private static void TraceOnWriter(Chunk chunk, int accepted)
    {
        if (accepted <= 0 || chunk.Context is null)
            return;
        ExecutionContext.Run(
            chunk.Context,
            static state =>
            {
                var (c, n) = ((Chunk, int))state!;
                NoteTrace(c.Offset, n, c.Bytes.Length, c.TraceHost, c.Slices);
            },
            (chunk, accepted));
    }

    private static void NoteTrace(
        long offset,
        int accepted,
        int length,
        bool traceHost,
        IReadOnlyList<AttachPathTrace.ComposeSlice>? slices)
    {
        if (accepted <= 0)
            return;
        // Record only the prefix that the host took. The unsent suffix
        // must never appear in route evidence.
        if (slices is { Count: > 0 })
            AttachPathTrace.FlushComposeSlices(slices, offset, Math.Min(accepted, length));
        else if (traceHost)
            AttachPathTrace.NoteHostWrite(offset, accepted);
    }

    private sealed record Chunk(
        byte[] Bytes,
        long Offset,
        bool TraceHost,
        IReadOnlyList<AttachPathTrace.ComposeSlice>? Slices,
        ExecutionContext? Context,
        bool Trace);
}
