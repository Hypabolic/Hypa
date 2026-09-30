using System.Text;
using Hypa.AgentRuntime.Application;

namespace Hypa.ControlPlane;

/// <summary>
/// queued render before the socket write path runs.
/// </summary>

/// <summary>
/// one replaceable render slot. Drain order is control, ordered, render.
/// <see cref="DefaultMaxReliableBytes"/> is a limit on the queued reliable
/// bytes. It is not a reservation. The queue holds a running counter
/// (<see cref="ReliableBytes"/>) and never preallocates the budget.
/// </summary>
internal sealed class ClientWriterQueue
{
    public const int DefaultMaxReliableBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Queue<WriterItem> _control = new();
    private readonly Queue<WriterItem> _ordered = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private WriterItem? _render;
    private long _reliableBytes;
    private long _reliableBytesHighWater;
    private bool _closed;
    private readonly int _maxReliableBytes;
    private readonly int _maxLineBytes;

    public ClientWriterQueue(
        int maxReliableBytes = DefaultMaxReliableBytes,
        int maxLineBytes = UnixSocketServerOptions.DefaultMaxLineBytes)
    {
        _maxReliableBytes = maxReliableBytes > 0 ? maxReliableBytes : DefaultMaxReliableBytes;
        _maxLineBytes = maxLineBytes > 0 ? maxLineBytes : UnixSocketServerOptions.DefaultMaxLineBytes;
    }

    public int MaxLineBytes => _maxLineBytes;

    public long ReliableBytes
    {
        get { lock (_gate) return _reliableBytes; }
    }

    /// <summary>Peak reliable bytes that this lane held. The lane counts bytes; it reserves none.</summary>
    public long ReliableBytesHighWater
    {
        get { lock (_gate) return _reliableBytesHighWater; }
    }

    public int MaxReliableBytes => _maxReliableBytes;

    public bool HasRender
    {
        get { lock (_gate) return _render is not null; }
    }

    public bool HasOrdered
    {
        get { lock (_gate) return _ordered.Count > 0; }
    }

    public int OrderedCount
    {
        get { lock (_gate) return _ordered.Count; }
    }

    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    public WriterLaneResult EnqueueResponse(string jsonLine) =>
        EnqueueControl(new WriterItem(jsonLine, reliable: true), enforceLineBudget: false);

    public WriterLaneResult EnqueueReliableEvent(string jsonLine) =>
        EnqueueControl(new WriterItem(jsonLine, reliable: true), enforceLineBudget: true);

    public WriterLaneResult TryEnqueueRender(string jsonLine) =>
        TryEnqueueRender(new WriterItem(jsonLine, reliable: false));

    public WriterLaneResult TryEnqueueRender(byte[] utf8Json) =>
        TryEnqueueRender(new WriterItem(utf8Json, reliable: false));

    public WriterLaneResult TryEnqueueRender(ReadOnlyMemory<byte> ndjsonLine) =>
        TryEnqueueRender(WriterItem.FromNdjsonLine(ndjsonLine, reliable: false));

    public WriterLaneResult TryEnqueueRender(
        ReadOnlyMemory<byte> ndjsonLine,
        AttachSurfaceEmitBinding? emitBinding) =>
        TryEnqueueRender(WriterItem.FromNdjsonLine(ndjsonLine, reliable: false, emitBinding: emitBinding));

    private WriterLaneResult TryEnqueueRender(WriterItem item)
    {
        lock (_gate)
        {
            if (_closed)
                return WriterLaneResult.Closed;
            if (item.ByteCount > _maxLineBytes)
                return WriterLaneResult.TooLarge;
            if (_render is not null)
                return WriterLaneResult.Full;
            _render = item;
        }

        Pulse();
        return WriterLaneResult.Ok;
    }

    public WriterLaneResult EnqueueOrderedRender(string jsonLine) =>
        EnqueueOrderedRender(new WriterItem(jsonLine, reliable: false));

    public WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) =>
        EnqueueOrderedRender(new WriterItem(utf8Json, reliable: false));

    public WriterLaneResult EnqueueOrderedRender(ReadOnlyMemory<byte> ndjsonLine) =>
        EnqueueOrderedRender(WriterItem.FromNdjsonLine(ndjsonLine, reliable: false));

    public WriterLaneResult EnqueueOrderedRender(
        ReadOnlyMemory<byte> ndjsonLine,
        AttachSurfaceEmitBinding? emitBinding) =>
        EnqueueOrderedRender(WriterItem.FromNdjsonLine(ndjsonLine, reliable: false, emitBinding: emitBinding));

    private WriterLaneResult EnqueueOrderedRender(WriterItem item)
    {
        lock (_gate)
        {
            if (_closed)
                return WriterLaneResult.Closed;
            if (item.ByteCount > _maxLineBytes)
                return WriterLaneResult.TooLarge;
            // A pending replaceable render is
            // promoted in front of that barrier and does not charge the reliable
            // byte budget. A second Full defers instead of accumulating or
            // fail-closing the client.
            if (_ordered.Count > 0)
                return WriterLaneResult.Full;
            if (_render is { } pending)
            {
                _render = null;
                _ordered.Enqueue(pending);
            }

            _ordered.Enqueue(item);
        }

        Pulse();
        return WriterLaneResult.Ok;
    }

    /// <summary>
    /// Admit every line of one Full generation as a single ordered barrier.
    /// A second Full still returns <see cref="WriterLaneStatus.Full"/>.
    /// </summary>
    public WriterLaneResult EnqueueOrderedRenderBatch(IReadOnlyList<string> jsonLines)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        if (jsonLines.Count == 0)
            return WriterLaneResult.Ok;
        if (jsonLines.Count == 1)
            return EnqueueOrderedRender(jsonLines[0]);

        lock (_gate)
        {
            if (_closed)
                return WriterLaneResult.Closed;
            if (_ordered.Count > 0)
                return WriterLaneResult.Full;
            var items = new WriterItem[jsonLines.Count];
            for (var i = 0; i < jsonLines.Count; i++)
            {
                ArgumentNullException.ThrowIfNull(jsonLines[i]);
                items[i] = new WriterItem(jsonLines[i], reliable: false);
                if (items[i].ByteCount > _maxLineBytes)
                    return WriterLaneResult.TooLarge;
            }

            if (_render is { } pending)
            {
                _render = null;
                _ordered.Enqueue(pending);
            }

            for (var i = 0; i < items.Length; i++)
                _ordered.Enqueue(items[i]);
        }

        Pulse();
        return WriterLaneResult.Ok;
    }

    public WriterLaneResult EnqueueOrderedRenderBatch(IReadOnlyList<byte[]> utf8JsonLines)
    {
        ArgumentNullException.ThrowIfNull(utf8JsonLines);
        if (utf8JsonLines.Count == 0)
            return WriterLaneResult.Ok;
        if (utf8JsonLines.Count == 1)
            return EnqueueOrderedRender(utf8JsonLines[0]);

        lock (_gate)
        {
            if (_closed)
                return WriterLaneResult.Closed;
            if (_ordered.Count > 0)
                return WriterLaneResult.Full;
            var items = new WriterItem[utf8JsonLines.Count];
            for (var i = 0; i < utf8JsonLines.Count; i++)
            {
                ArgumentNullException.ThrowIfNull(utf8JsonLines[i]);
                items[i] = new WriterItem(utf8JsonLines[i], reliable: false);
                if (items[i].ByteCount > _maxLineBytes)
                    return WriterLaneResult.TooLarge;
            }

            if (_render is { } pending)
            {
                _render = null;
                _ordered.Enqueue(pending);
            }

            for (var i = 0; i < items.Length; i++)
                _ordered.Enqueue(items[i]);
        }

        Pulse();
        return WriterLaneResult.Ok;
    }

    public WriterLaneResult EnqueueOrderedRenderBatch(IReadOnlyList<ReadOnlyMemory<byte>> ndjsonLines) =>
        EnqueueOrderedRenderBatch(ndjsonLines, emitBinding: null);

    public WriterLaneResult EnqueueOrderedRenderBatch(
        IReadOnlyList<ReadOnlyMemory<byte>> ndjsonLines,
        AttachSurfaceEmitBinding? emitBinding)
    {
        ArgumentNullException.ThrowIfNull(ndjsonLines);
        if (ndjsonLines.Count == 0)
            return WriterLaneResult.Ok;
        if (ndjsonLines.Count == 1)
            return EnqueueOrderedRender(ndjsonLines[0], emitBinding);

        lock (_gate)
        {
            if (_closed)
                return WriterLaneResult.Closed;
            if (_ordered.Count > 0)
                return WriterLaneResult.Full;
            var items = new WriterItem[ndjsonLines.Count];
            for (var i = 0; i < ndjsonLines.Count; i++)
            {
                items[i] = WriterItem.FromNdjsonLine(ndjsonLines[i], reliable: false, emitBinding: emitBinding);
                if (items[i].ByteCount > _maxLineBytes)
                    return WriterLaneResult.TooLarge;
            }

            if (_render is { } pending)
            {
                _render = null;
                _ordered.Enqueue(pending);
            }

            for (var i = 0; i < items.Length; i++)
                _ordered.Enqueue(items[i]);
        }

        Pulse();
        return WriterLaneResult.Ok;
    }

    public WriterLaneResult ReplaceWithCleanup(string jsonLine) =>
        ReplaceWithCleanup(new WriterItem(jsonLine, reliable: true));

    public WriterLaneResult ReplaceWithCleanup(byte[] utf8Json) =>
        ReplaceWithCleanup(new WriterItem(utf8Json, reliable: true));

    private WriterLaneResult ReplaceWithCleanup(WriterItem item)
    {
        lock (_gate)
        {
            if (_closed)
                return WriterLaneResult.Closed;
            DropRenderAndOrderedUnlocked();
            if (_reliableBytes + item.ByteCount > _maxReliableBytes)
                return FailClosedUnlocked();
            _control.Enqueue(item);
            AddReliableUnlocked(item.ByteCount);
        }

        Pulse();
        return WriterLaneResult.Ok;
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            DiscardPendingRenderUnlocked();
            _reliableBytes = 0;
            foreach (var item in _control)
                item.Completion?.TrySetCanceled();
            _control.Clear();
        }

        Pulse();
    }

    public void DiscardPendingRender()
    {
        lock (_gate)
        {
            DiscardPendingRenderUnlocked();
        }

        Pulse();
    }

    private WriterItem? _inFlight;

    public async Task<WriterItem?> DequeueAsync(
        CancellationToken ct,
        Func<AttachSurfaceEmitBinding?, bool>? canWriteRender = null)
    {
        while (!ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                while (true)
                {
                    var candidate = TryTakeNextUnlocked();
                    if (candidate is null)
                    {
                        if (_closed)
                            return null;
                        break;
                    }

                    // Cubes Connect requires a surface binding. Local attach
                    // has no snapshot; canWriteRender(null) stays true.
                    if (canWriteRender is not null
                        && !candidate.Reliable
                        && candidate.EmitBinding is null
                        && !canWriteRender(null))
                    {
                        candidate.Completion?.TrySetCanceled();
                        continue;
                    }

                    if (candidate.EmitBinding is null
                        || canWriteRender is null
                        || canWriteRender(candidate.EmitBinding))
                    {
                        _inFlight = candidate;
                        return candidate;
                    }

                    candidate.Completion?.TrySetCanceled();
                }
            }

            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
        }

        return null;
    }

    public WriterLaneResult EnqueueResponse(byte[] utf8Json) =>
        EnqueueControl(new WriterItem(utf8Json, reliable: true), enforceLineBudget: false);

    public WriterLaneResult EnqueueReliableEvent(byte[] utf8Json) =>
        EnqueueControl(new WriterItem(utf8Json, reliable: true), enforceLineBudget: true);

    public WriterLaneResult EnqueueReliableEvent(ReadOnlyMemory<byte> ndjsonLine) =>
        EnqueueControl(
            WriterItem.FromNdjsonLine(ndjsonLine, reliable: true), enforceLineBudget: true);

    private WriterLaneResult EnqueueControl(WriterItem item, bool enforceLineBudget)
    {
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_closed)
                return WriterLaneResult.Closed;
            if (enforceLineBudget && item.ByteCount > _maxLineBytes)
                return WriterLaneResult.TooLarge;
            if (_reliableBytes + item.ByteCount > _maxReliableBytes)
                return FailClosedUnlocked();
            _control.Enqueue(item);
            AddReliableUnlocked(item.ByteCount);
            completion = item.Completion;
        }

        Pulse();
        _ = completion;
        return WriterLaneResult.Ok;
    }

    public WriterLaneResult EnqueueResponseWait(string jsonLine, out Task written) =>
        EnqueueResponseWait(jsonLine, emitBinding: null, out written);

    public WriterLaneResult EnqueueResponseWait(
        string jsonLine,
        AttachSurfaceEmitBinding? emitBinding,
        out Task written)
    {
        ArgumentNullException.ThrowIfNull(jsonLine);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed)
            {
                written = Task.FromException(new ObjectDisposedException(nameof(ClientWriterQueue)));
                return WriterLaneResult.Closed;
            }

            var item = new WriterItem(jsonLine, reliable: true, completion, emitBinding);
            if (_reliableBytes + item.ByteCount > _maxReliableBytes)
            {
                var closed = FailClosedUnlocked();
                written = Task.FromException(new IOException("Reliable writer lane exceeded the byte budget."));
                return closed;
            }

            _control.Enqueue(item);
            AddReliableUnlocked(item.ByteCount);
        }

        Pulse();
        written = completion.Task;
        return WriterLaneResult.Ok;
    }

    private WriterItem? TryTakeNextUnlocked()
    {
        if (_control.Count > 0)
            return TakeControlUnlocked();
        if (_ordered.Count > 0)
            return TakeOrderedUnlocked();
        if (_render is { } render)
        {
            _render = null;
            return render;
        }

        return null;
    }

    private WriterItem TakeControlUnlocked()
    {
        var item = _control.Dequeue();
        _reliableBytes = Math.Max(0, _reliableBytes - item.ByteCount);
        return item;
    }

    private WriterItem TakeOrderedUnlocked()
    {
        var item = _ordered.Dequeue();
        if (item.Reliable)
            _reliableBytes = Math.Max(0, _reliableBytes - item.ByteCount);
        return item;
    }

    internal void NotifyWriteCompleted()
    {
        lock (_gate)
        {
            _inFlight = null;
        }
    }

    private void DiscardPendingRenderUnlocked()
    {
        _render = null;
        while (_ordered.Count > 0)
        {
            var item = _ordered.Dequeue();
            if (item.Reliable)
                _reliableBytes = Math.Max(0, _reliableBytes - item.ByteCount);
            item.Completion?.TrySetCanceled();
        }

        if (_control.Count > 0)
        {
            var retained = new Queue<WriterItem>(_control.Count);
            while (_control.Count > 0)
            {
                var item = _control.Dequeue();
                if (item.EmitBinding is null)
                {
                    retained.Enqueue(item);
                    continue;
                }

                _reliableBytes = Math.Max(0, _reliableBytes - item.ByteCount);
                item.Completion?.TrySetCanceled();
            }

            while (retained.Count > 0)
                _control.Enqueue(retained.Dequeue());
        }

        if (_inFlight is { EmitBinding: not null } inFlight)
        {
            inFlight.Completion?.TrySetCanceled();
            _inFlight = null;
        }
    }

    private void DropRenderAndOrderedUnlocked() => DiscardPendingRenderUnlocked();

    private WriterLaneResult FailClosedUnlocked()
    {
        _closed = true;
        _render = null;
        while (_ordered.Count > 0)
        {
            var item = _ordered.Dequeue();
            item.Completion?.TrySetException(
                new IOException("Reliable writer lane exceeded the byte budget."));
        }

        while (_control.Count > 0)
        {
            var item = _control.Dequeue();
            item.Completion?.TrySetException(
                new IOException("Reliable writer lane exceeded the byte budget."));
        }

        _reliableBytes = 0;
        Pulse();
        return WriterLaneResult.Closed;
    }

    private void AddReliableUnlocked(int bytes)
    {
        _reliableBytes += bytes;
        if (_reliableBytes > _reliableBytesHighWater)
            _reliableBytesHighWater = _reliableBytes;
    }

    private void Pulse()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* already signaled */ }
        catch (ObjectDisposedException) { /* disposing */ }
    }
}

internal sealed class WriterItem
{
    public WriterItem(
        string line,
        bool reliable,
        TaskCompletionSource? completion = null,
        AttachSurfaceEmitBinding? emitBinding = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        Utf8 = EncodeNdjson(line);
        Reliable = reliable;
        Completion = completion;
        EmitBinding = emitBinding;
        ByteCount = Utf8.Length;
    }

    public WriterItem(
        byte[] utf8Json,
        bool reliable,
        TaskCompletionSource? completion = null,
        AttachSurfaceEmitBinding? emitBinding = null)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        Utf8 = AppendNewline(utf8Json);
        Reliable = reliable;
        Completion = completion;
        EmitBinding = emitBinding;
        ByteCount = Utf8.Length;
    }

    /// <summary>
    /// Wrap one NDJSON line, trailing <c>\n</c> included, with no copy.
    /// The caller keeps owning the array through the returned memory; no
    /// lane recycles it.
    /// </summary>
    public static WriterItem FromNdjsonLine(
        ReadOnlyMemory<byte> ndjsonLine,
        bool reliable,
        TaskCompletionSource? completion = null,
        AttachSurfaceEmitBinding? emitBinding = null)
    {
        var span = ndjsonLine.Span;
        if (span.IsEmpty || span[^1] != (byte)'\n')
            throw new ArgumentException("NDJSON line must end with a newline.", nameof(ndjsonLine));
        return new WriterItem(ndjsonLine, reliable, completion, emitBinding);
    }

    private WriterItem(
        ReadOnlyMemory<byte> utf8,
        bool reliable,
        TaskCompletionSource? completion,
        AttachSurfaceEmitBinding? emitBinding)
    {
        Utf8 = utf8;
        Reliable = reliable;
        Completion = completion;
        EmitBinding = emitBinding;
        ByteCount = utf8.Length;
    }

    public ReadOnlyMemory<byte> Utf8 { get; }

    public AttachSurfaceEmitBinding? EmitBinding { get; }

    public int ByteCount { get; }

    public bool Reliable { get; }

    public TaskCompletionSource? Completion { get; }

    public string Line
    {
        get
        {
            var span = Utf8.Span;
            if (span.Length > 0 && span[^1] == (byte)'\n')
                span = span[..^1];
            return Encoding.UTF8.GetString(span);
        }
    }

    private static byte[] EncodeNdjson(string line)
    {
        var n = Encoding.UTF8.GetByteCount(line);
        var utf8 = new byte[n + 1];
        Encoding.UTF8.GetBytes(line, utf8.AsSpan(0, n));
        utf8[n] = (byte)'\n';
        return utf8;
    }

    private static byte[] AppendNewline(byte[] json)
    {
        var utf8 = new byte[json.Length + 1];
        json.AsSpan().CopyTo(utf8);
        utf8[json.Length] = (byte)'\n';
        return utf8;
    }
}
