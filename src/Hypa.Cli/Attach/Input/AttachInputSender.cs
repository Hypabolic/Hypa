using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Latency;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach.Input;

/// <summary>
/// Ordered attach input. The stdin loop appends decoded bytes to one
/// coalescing buffer bounded by bytes, not by key count, so a paste that
/// decodes into thousands of key events costs a memcpy each. One sender
/// drains the buffer in large batches and writes JSON-RPC notifications
/// without waiting for an admission reply. <see cref="EnqueueAsync"/> waits
/// for buffer space instead of dropping bytes, so a large paste stalls the
/// stdin read (the host TTY holds the rest) rather than losing text.
/// </summary>
public sealed class AttachInputSender : IAsyncDisposable
{
    /// <summary>Upper bound on bytes buffered ahead of the sender.</summary>
    public const int DefaultMaxQueuedBytes = 1024 * 1024;

    /// <summary>
    /// Raw bytes per <c>pane.send_keys</c>. Base64 keeps one notification
    /// well under the 1 MiB NDJSON line cap.
    /// </summary>
    public const int MaxBatchBytes = 64 * 1024;

    public const int TransientInvalidStateLimit = 3;

    private const int InitialBufferBytes = 4096;

    /// <summary>
    // / InvalidState bursts reset after this quiet window.
    /// <c>src/server/headless.rs:3141-3143</c> continues after apply failure.
    /// Three lifetime queue-full events must not latch detach.
    /// </summary>
    public static readonly TimeSpan TransientInvalidStateWindow = TimeSpan.FromSeconds(1);

    private readonly object _queueLock = new();
    private readonly Channel<bool> _doorbell;
    private ControlPlaneClient _client;
    private readonly Func<(string PaneId, string LeaseId)> _target;
    private readonly Func<bool>? _blocksForward;
    private readonly Action<AttachInputFault>? _onFault;
    private readonly SemaphoreSlim _sendAdmission = new(1, 1);
    private readonly SemaphoreSlim _retargetAdmission = new(1, 1);
    private readonly Task _loop;
    private readonly ConcurrentQueue<TaskCompletionSource> _flushWaiters = new();
    private int _loopEnded;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _maxQueuedBytes;
    private byte[] _pending = new byte[InitialBufferBytes];
    private byte[] _sending = new byte[InitialBufferBytes];

    // Read stamps of the keys in each buffer, with the buffer offset just
    // after the bytes of each stamp. They swap with the buffers under the
    // queue lock, so a stamp never moves to another buffer.
    private List<AttachKeyStamp> _pendingStamps = [];
    private List<AttachKeyStamp> _sendingStamps = [];
    private List<int> _pendingStampEnds = [];
    private List<int> _sendingStampEnds = [];

    // True when any bytes in the buffer are keyboard input. A batch with
    // key bytes thus never goes out with keyboard = false. They swap with
    // the buffers under the queue lock.
    private bool _pendingKeyboard;
    private bool _sendingKeyboard;
    private readonly AttachLatencyMeter? _latency;
    private int _pendingCount;
    private TaskCompletionSource? _spaceWaiter;
    private long _sendGeneration;
    private int _retargeting;
    private int _rejected;
    private int _undeliverable;
    private int _disposed;
    private Exception? _fault;

    public AttachInputSender(
        ControlPlaneClient client,
        Func<(string PaneId, string LeaseId)> target,
        Action<AttachInputFault>? onFault = null,
        Func<bool>? blocksForward = null,
        int maxQueuedBytes = DefaultMaxQueuedBytes,
        AttachLatencyMeter? latency = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(target);
        _client = client;
        _target = target;
        _blocksForward = blocksForward;
        _onFault = onFault;
        _latency = latency;
        _maxQueuedBytes = Math.Max(1, maxQueuedBytes);
        _doorbell = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    internal void Rebind(ControlPlaneClient client) =>
        Retarget(client, installTarget: null);

    /// <summary>Test seam: in-flight notify gate for concurrent send tests.</summary>
    internal SemaphoreSlim SendAdmissionForTests => _sendAdmission;

    /// <summary>Test seam: retarget drain gate for concurrent retarget fencing tests.</summary>
    internal SemaphoreSlim RetargetAdmissionForTests => _retargetAdmission;

    /// <summary>
    /// Test seam: <see cref="TryEnqueue"/> calls this after it reads the send
    /// generation and before it waits for retarget admission.
    /// </summary>
    internal Action? GenerationReadForTests { get; set; }

    /// <summary>Test seam: nonzero while a retarget or discard is quiescing the queue.</summary>
    internal int RetargetingForTests => Volatile.Read(ref _retargeting);

    /// <summary>
    /// Test seam: when set, the sender loop waits on this task after it reads the
    /// doorbell and before it takes any bytes.
    /// </summary>
    internal Task? HoldRoundForTests { get; set; }

    /// <summary>
    /// Test seam: completes when the sender loop has finished a round that began
    /// after this call. Every byte queued before this call is then sent or retired.
    /// Completes at once when the loop has ended.
    /// </summary>
    internal Task FlushForTests()
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _flushWaiters.Enqueue(waiter);
        _doorbell.Writer.TryWrite(true);
        // The loop sets the flag, then takes the waiters. The enqueue comes
        // before this read. So the loop takes this waiter, or this call sees
        // the flag and completes the waiter.
        if (Volatile.Read(ref _loopEnded) != 0)
            CompleteFlushWaiters(TakeFlushWaiters());
        return waiter.Task;
    }

    /// <summary>Test seam: set true to block delivery inside <see cref="NotifyAsync"/>.</summary>
    internal bool BlockNotifyForTests { get; set; }

    /// <summary>Test seam: signaled when delivery reaches the in-flight notify gate.</summary>
    internal SemaphoreSlim NotifyEnteredForTests { get; } = new(0, 1);

    /// <summary>Test seam: release to unblock an in-flight notify blocked for tests.</summary>
    internal SemaphoreSlim NotifyHoldForTests { get; } = new(0, 1);

    /// <summary>
    /// Quiesce queued input and atomically install a new client/target generation.
    /// <c>catalog_reload.rs:59-65</c> freezes presentation without ending the client.
    /// Retirement must not deliver pre-switch bytes.
    /// </summary>
    internal void Retarget(ControlPlaneClient client, Action? installTarget)
    {
        ArgumentNullException.ThrowIfNull(client);
        Interlocked.Increment(ref _retargeting);
        try
        {
            _sendAdmission.Wait();
            _retargetAdmission.Wait();
            try
            {
                DrainQueuedInternal();
                Interlocked.Increment(ref _sendGeneration);
                installTarget?.Invoke();
                _client = client;
                // The new connection never acks a seq of the old one.
                _latency?.DropOpen();
            }
            finally
            {
                _retargetAdmission.Release();
                _sendAdmission.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _retargeting);
        }
    }

    public bool IsFaulted => Volatile.Read(ref _fault) is not null;

    public Exception? Fault => Volatile.Read(ref _fault);

    public int RejectedBatches => Volatile.Read(ref _rejected);

    public int UndeliverableBytes => Volatile.Read(ref _undeliverable);

    /// <summary>Bytes buffered and not yet taken by the sender.</summary>
    public int QueuedBytes
    {
        get
        {
            lock (_queueLock)
                return _pendingCount;
        }
    }

    /// <summary>
    /// Non-waiting enqueue. Fails when forwarding is blocked, the sender is
    /// faulted, a retarget is in progress, or the bytes do not fit.
    /// </summary>
    /// <param name="keyboard">False for paste bytes. See <see cref="EnqueueAsync"/>.</param>
    public bool TryEnqueue(ReadOnlySpan<byte> bytes, AttachKeyStamp stamp = default, bool keyboard = true)
    {
        if (bytes.IsEmpty)
            return true;
        if (!CanAccept())
            return false;
        var generation = Volatile.Read(ref _sendGeneration);
        if (RetargetPending())
            return false;
        GenerationReadForTests?.Invoke();
        _retargetAdmission.Wait();
        if (RetiredWhileWaiting(generation))
        {
            _retargetAdmission.Release();
            return false;
        }

        try
        {
            lock (_queueLock)
            {
                if (_pendingCount + bytes.Length > _maxQueuedBytes)
                {
                    Interlocked.Increment(ref _rejected);
                    return false;
                }

                AppendUnderLock(bytes, keyboard);
                StampUnderLock(stamp);
            }
        }
        finally
        {
            _retargetAdmission.Release();
        }

        _doorbell.Writer.TryWrite(true);
        return true;
    }

    /// <summary>
    /// Ordered enqueue that waits for buffer space instead of dropping.
    /// Large input is admitted in slices as the sender drains. Returns false
    /// only when forwarding is blocked, the sender faulted or was disposed,
    /// or a retarget retired this generation; the unsent tail is then
    /// discarded, as it would have been by the retarget drain.
    /// </summary>
    /// <remarks>
    /// <paramref name="stamp"/> goes with the last slice that the buffer admits,
    /// so its keys open only when all of their bytes are queued.
    /// <paramref name="keyboard"/> is false for paste bytes. A batch that
    /// holds only such bytes goes out with <c>keyboard: false</c>, so the mux
    /// opens no echo window. A batch with any key bytes is keyboard input.
    /// </remarks>
    public async ValueTask<bool> EnqueueAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct,
        AttachKeyStamp stamp = default,
        bool keyboard = true)
    {
        if (bytes.IsEmpty)
            return true;

        // Fixed at entry: a retarget that starts and completes while this
        // call waits retires these bytes even though no retarget is pending
        // by the time admission is acquired.
        var generation = Volatile.Read(ref _sendGeneration);
        while (true)
        {
            if (!CanAccept())
                return false;
            if (RetargetPending())
                return false;
            await _retargetAdmission.WaitAsync(ct).ConfigureAwait(false);
            if (RetiredWhileWaiting(generation))
            {
                _retargetAdmission.Release();
                return false;
            }

            Task? waitForSpace = null;
            try
            {
                lock (_queueLock)
                {
                    var space = _maxQueuedBytes - _pendingCount;
                    if (space > 0)
                    {
                        var take = Math.Min(space, bytes.Length);
                        AppendUnderLock(bytes.Span[..take], keyboard);
                        bytes = bytes[take..];
                        // The keys open with the last slice. An earlier slice
                        // can reach the pane while the rest still waits.
                        if (bytes.IsEmpty)
                            StampUnderLock(stamp);
                    }

                    if (!bytes.IsEmpty)
                    {
                        _spaceWaiter ??= new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        waitForSpace = _spaceWaiter.Task;
                    }
                }
            }
            finally
            {
                // Never hold retarget admission while waiting: Retarget
                // drains the buffer under it and wakes this waiter.
                _retargetAdmission.Release();
            }

            _doorbell.Writer.TryWrite(true);
            if (waitForSpace is null)
                return true;

            await waitForSpace.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void DiscardQueued()
    {
        Interlocked.Increment(ref _retargeting);
        try
        {
            _retargetAdmission.Wait();
            try
            {
                DrainQueuedInternal();
                Interlocked.Increment(ref _sendGeneration);
                // A reconnect or a freeze discards input. Keys already sent
                // may never get an ack, so the meter stops waiting for them.
                _latency?.DropOpen();
            }
            finally
            {
                _retargetAdmission.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _retargeting);
        }
    }

    /// <summary>Test seam: generation bump without drain for reverse-interleaving tests.</summary>
    internal void BumpGenerationForTests() =>
        Interlocked.Increment(ref _sendGeneration);

    /// <summary>
    /// Test seam: bump generation under admission without draining, matching the
    /// pre-fix retarget window between increment and drain.
    /// </summary>
    internal void IncrementGenerationUnderAdmissionForTests()
    {
        _retargetAdmission.Wait();
        try
        {
            Interlocked.Increment(ref _sendGeneration);
        }
        finally
        {
            _retargetAdmission.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _doorbell.Writer.TryComplete();
        lock (_queueLock)
            WakeSpaceWaiterUnderLock();
        // The attach makes a new sender for each attempt. The meter outlives
        // this sender, so it stops waiting for the keys that this one sent.
        _latency?.DropOpen();
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* already */ }
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (IOException) { /* disconnect */ }
        catch (ObjectDisposedException) { /* client gone */ }
        catch (ControlPlaneException) { /* admission failed */ }
        _sendAdmission.Dispose();
        _retargetAdmission.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// A retarget or discard is quiescing the queue. Input arriving now
    /// belongs to the retired generation and is refused. The sender and
    /// enqueuer only hold admission briefly, so they wait on each other
    /// rather than refusing.
    /// </summary>
    private bool RetargetPending()
    {
        if (Volatile.Read(ref _retargeting) == 0)
            return false;
        Interlocked.Increment(ref _rejected);
        return true;
    }

    /// <summary>
    /// Checked after acquiring admission. A retarget may be pending, or may
    /// have started and completed while the caller waited: either way the
    /// bytes belong to a retired generation.
    /// </summary>
    private bool RetiredWhileWaiting(long generation)
    {
        if (RetargetPending())
            return true;
        if (Volatile.Read(ref _sendGeneration) == generation)
            return false;
        Interlocked.Increment(ref _rejected);
        return true;
    }

    private bool CanAccept() =>
        Volatile.Read(ref _disposed) == 0
        && _blocksForward?.Invoke() != true
        && Volatile.Read(ref _fault) is null;

    private void AppendUnderLock(ReadOnlySpan<byte> bytes, bool keyboard)
    {
        if (!bytes.IsEmpty && keyboard)
            _pendingKeyboard = true;
        var needed = _pendingCount + bytes.Length;
        if (needed > _pending.Length)
        {
            var size = _pending.Length;
            while (size < needed)
                size *= 2;
            Array.Resize(ref _pending, Math.Min(size, Math.Max(needed, _maxQueuedBytes)));
        }

        bytes.CopyTo(_pending.AsSpan(_pendingCount));
        _pendingCount = needed;
    }

    /// <summary>Call after the append of the stamped bytes.</summary>
    private void StampUnderLock(AttachKeyStamp stamp)
    {
        if (_latency is null || stamp.IsEmpty)
            return;
        _pendingStamps.Add(stamp);
        _pendingStampEnds.Add(_pendingCount);
    }

    /// <summary>
    /// The stamps whose last byte is in the batch
    /// [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>).
    /// A key closes only when its whole byte run is on the wire.
    /// </summary>
    private ReadOnlySpan<AttachKeyStamp> SendingStampsIn(int offset, int length)
    {
        var ends = CollectionsMarshal.AsSpan(_sendingStampEnds);
        var start = 0;
        while (start < ends.Length && ends[start] <= offset)
            start++;
        var end = start;
        while (end < ends.Length && ends[end] <= offset + length)
            end++;
        return CollectionsMarshal.AsSpan(_sendingStamps)[start..end];
    }

    private void WakeSpaceWaiterUnderLock()
    {
        var waiter = _spaceWaiter;
        _spaceWaiter = null;
        waiter?.TrySetResult();
    }

    private void DrainQueuedInternal()
    {
        lock (_queueLock)
        {
            _pendingCount = 0;
            _pendingKeyboard = false;
            _pendingStamps.Clear();
            _pendingStampEnds.Clear();
            WakeSpaceWaiterUnderLock();
        }
    }

    /// <summary>
    /// Swap the pending buffer out for sending. The enqueuer keeps appending
    /// to the other buffer while this batch is on the wire.
    /// </summary>
    private int TakePending()
    {
        lock (_queueLock)
        {
            var count = _pendingCount;
            if (count == 0)
                return 0;
            (_pending, _sending) = (_sending, _pending);
            (_pendingStamps, _sendingStamps) = (_sendingStamps, _pendingStamps);
            (_pendingStampEnds, _sendingStampEnds) = (_sendingStampEnds, _pendingStampEnds);
            _sendingKeyboard = _pendingKeyboard;
            _pendingKeyboard = false;
            _pendingStamps.Clear();
            _pendingStampEnds.Clear();
            _pendingCount = 0;
            WakeSpaceWaiterUnderLock();
            return count;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _doorbell.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                _doorbell.Reader.TryRead(out _);
                var flushing = TakeFlushWaiters();
                try
                {
                    var hold = HoldRoundForTests;
                    if (hold is not null)
                        await hold.WaitAsync(ct).ConfigureAwait(false);
                    while (await SendPendingAsync(ct).ConfigureAwait(false))
                    {
                    }
                }
                finally
                {
                    CompleteFlushWaiters(flushing);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RaiseFault(new AttachInputFault(AttachInputFaultKind.SenderFault, 0, ex.Message, ex));
        }
        finally
        {
            Interlocked.Exchange(ref _loopEnded, 1);
            CompleteFlushWaiters(TakeFlushWaiters());
        }
    }

    // Null when no test waits, so a normal round allocates nothing.
    private List<TaskCompletionSource>? TakeFlushWaiters()
    {
        if (_flushWaiters.IsEmpty)
            return null;
        var taken = new List<TaskCompletionSource>();
        while (_flushWaiters.TryDequeue(out var waiter))
            taken.Add(waiter);
        return taken;
    }

    private static void CompleteFlushWaiters(List<TaskCompletionSource>? waiters)
    {
        if (waiters is null)
            return;
        foreach (var waiter in waiters)
            waiter.TrySetResult();
    }

    /// <summary>Send one buffer swap. Returns false once nothing was pending.</summary>
    private async Task<bool> SendPendingAsync(CancellationToken ct)
    {
        await _sendAdmission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batchGeneration = Volatile.Read(ref _sendGeneration);
            var count = TakePending();
            if (count == 0)
                return false;

            var batch = _sending;
            for (var offset = 0; offset < count; offset += MaxBatchBytes)
            {
                var length = Math.Min(MaxBatchBytes, count - offset);
                if (!await SendBatchAsync(batch, offset, length, batchGeneration, ct)
                        .ConfigureAwait(false))
                {
                    break;
                }
            }

            _sendingStamps.Clear();
            _sendingStampEnds.Clear();
            return true;
        }
        finally
        {
            _sendAdmission.Release();
        }
    }

    private async Task<bool> SendBatchAsync(
        byte[] buffer,
        int offset,
        int length,
        long batchGeneration,
        CancellationToken ct)
    {
        if (!CanDeliverBatch(batchGeneration))
            return false;

        // Retarget holds send admission before this one, so only a brief
        // enqueue or discard can hold it here. Waiting cannot deadlock, and
        // refusing would silently drop this batch.
        await _retargetAdmission.WaitAsync(ct).ConfigureAwait(false);

        ControlPlaneClient client;
        string paneId;
        string leaseId;
        try
        {
            var deliverGeneration = Volatile.Read(ref _sendGeneration);
            if (deliverGeneration != batchGeneration)
                return false;

            (paneId, leaseId) = _target();
            if (string.IsNullOrWhiteSpace(paneId))
            {
                Interlocked.Add(ref _undeliverable, length);
                RaiseFault(new AttachInputFault(
                    AttachInputFaultKind.Undeliverable,
                    length,
                    "input target pane is empty"));
                return false;
            }

            if (!CanDeliverBatch(batchGeneration)
                || Volatile.Read(ref _sendGeneration) != deliverGeneration)
            {
                return false;
            }

            client = _client;
            if (Volatile.Read(ref _sendGeneration) != deliverGeneration)
                return false;
        }
        finally
        {
            _retargetAdmission.Release();
        }

        if (BlockNotifyForTests)
        {
            NotifyEnteredForTests.Release();
            await NotifyHoldForTests.WaitAsync(ct).ConfigureAwait(false);
        }

        long? seq = null;
        try
        {
            var body = new JsonObject
            {
                ["pane_id"] = paneId,
                ["encoding"] = "base64",
                ["data"] = Convert.ToBase64String(buffer, offset, length),
            };
            if (!string.IsNullOrWhiteSpace(leaseId))
                body["lease_id"] = leaseId;
            // Only a batch of paste bytes alone says so. A missing field
            // is keyboard input, so an old mux sees the same body as before.
            if (!_sendingKeyboard)
                body["keyboard"] = false;
            // A seq goes on the wire only when this connection's welcome
            // listed the capability. An old mux gets no new field.
            if (_latency is not null
                && client.HasPeerCapability(AttachEndpointProtocol.InputAckCapability))
            {
                seq = _latency.NextSeq();
                body["seq"] = seq.Value;
            }

            // Open the keys of this batch under its own seq before the send,
            // so an ack that the render loop reads before this call returns
            // still finds them.
            _latency?.NoteSent(paneId, seq, SendingStampsIn(offset, length));
            var wireStart = _latency?.Now() ?? 0;
            await client.NotifyAsync(
                    ProtocolMethods.PaneSendKeys,
                    body,
                    ct)
                .ConfigureAwait(false);
            _latency?.RecordHop(AttachLatencyHop.InputWire, wireStart);
        }
        catch (Exception)
        {
            if (seq is { } sent)
                _latency?.DropOpen(paneId, sent);
            if (!CanDeliverBatch(batchGeneration))
                return false;
            throw;
        }

        return true;
    }

    private bool CanDeliverBatch(long batchGeneration) =>
        _blocksForward?.Invoke() != true
        && Volatile.Read(ref _sendGeneration) == batchGeneration;

    private void RaiseFault(AttachInputFault fault)
    {
        Volatile.Write(ref _fault, fault);
        lock (_queueLock)
            WakeSpaceWaiterUnderLock();
        try { _onFault?.Invoke(fault); }
        catch
        {
            // Status callback must not kill the sender loop.
        }
    }
}

public enum AttachInputFaultKind
{
    Undeliverable = 0,
    AdmissionRejected = 1,
    SenderFault = 2,
}

public sealed class AttachInputFault : Exception
{
    public AttachInputFault(
        AttachInputFaultKind kind,
        int undeliverableBytes,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        UndeliverableBytes = undeliverableBytes;
    }

    public AttachInputFaultKind Kind { get; }

    public int UndeliverableBytes { get; }
}
