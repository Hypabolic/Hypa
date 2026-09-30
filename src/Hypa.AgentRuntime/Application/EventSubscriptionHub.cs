using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// In-process hub for transient observer subscriptions and live fanout with drop policy.
/// <see cref="PostLive"/> enqueues onto an ordered single-reader channel.
/// Control and Lifecycle are posted under the session emit gate when their
/// sequence is reserved, before the journal write (ADR 0016, 2026-09-28).
/// Output and Render are live-only and posted under a per-pane emit gate.
/// The worker delivers each record to
/// matching subscriptions in parallel so one sub's slow WriteLineAsync cannot
/// block other connections for that record. Pre-live enqueue never waits on the
/// shared fanout worker. Journal append never waits on socket I/O (design §10.2).
/// </summary>
public sealed class EventSubscriptionHub : IEventSubscriptionHub, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EventSubscription> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _byConnection = new(StringComparer.Ordinal);
    private readonly PaneFrameStore _frames = new();

    private readonly Channel<FanoutWork> _liveChannel = Channel.CreateUnbounded<FanoutWork>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private readonly object _postGate = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _fanoutLoop;
    private readonly EventQueueLogProbe? _queueProbe;
    private int _disposed;
    private int _probeQueued;

    public EventSubscriptionHub()
        : this(null, null)
    {
    }

    public EventSubscriptionHub(IProcessLogSink? processLog, string? sessionId = null)
    {
        _queueProbe = processLog is null ? null : new EventQueueLogProbe(processLog, sessionId);
        _fanoutLoop = Task.Run(() => FanoutLoopAsync(_shutdownCts.Token));
    }

    public EventSubscription Register(EventSubscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sub = new EventSubscription(request, _frames);
        lock (_gate)
        {
            _byId[sub.SubscriptionId] = sub;
            if (!_byConnection.TryGetValue(sub.ConnectionId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _byConnection[sub.ConnectionId] = set;
            }

            set.Add(sub.SubscriptionId);
        }

        return sub;
    }

    public bool Unregister(string subscriptionId)
    {
        lock (_gate)
        {
            if (!_byId.Remove(subscriptionId, out var sub))
                return false;
            sub.Close();
            if (_byConnection.TryGetValue(sub.ConnectionId, out var set))
            {
                set.Remove(subscriptionId);
                if (set.Count == 0)
                    _byConnection.Remove(sub.ConnectionId);
            }

            return true;
        }
    }

    public void UnregisterConnection(string connectionId)
    {
        lock (_gate)
        {
            if (!_byConnection.Remove(connectionId, out var set))
                return;
            foreach (var id in set)
            {
                if (_byId.Remove(id, out var sub))
                    sub.Close();
            }
        }
    }

    public EventSubscription? Get(string subscriptionId)
    {
        lock (_gate)
            return _byId.TryGetValue(subscriptionId, out var s) ? s : null;
    }

    public EventHubQueueSnapshot ReadQueueSnapshot()
    {
        EventSubscription[] subs;
        lock (_gate)
            subs = _byId.Values.ToArray();

        var items = 0;
        var bytes = 0L;
        var drops = 0L;
        foreach (var sub in subs)
        {
            items += sub.LiveQueuedCount;
            bytes += sub.LiveQueuedBytes;
            drops += sub.OutputDropCount;
        }

        return new EventHubQueueSnapshot(subs.Length, items, bytes, drops);
    }

    public EventHubFrameRetention ReadFrameRetention()
    {
        lock (_gate)
        {
            var (panes, frames) = _frames.ReadCounts();
            var cursors = 0;
            var deferred = 0;
            foreach (var sub in _byId.Values)
            {
                cursors += sub.ReadCursorCount();
                deferred += sub.ReadDeferredFrameCount();
            }

            return new EventHubFrameRetention(panes, frames, cursors, deferred);
        }
    }

    public IReadOnlyList<EventSubscription> ListForConnection(string connectionId)
    {
        lock (_gate)
        {
            if (!_byConnection.TryGetValue(connectionId, out var set))
                return [];
            return set.Select(id => _byId[id]).ToList();
        }
    }

    /// <inheritdoc />
    public bool RequestLiveDrain(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || Volatile.Read(ref _disposed) != 0)
            return false;
        return EnqueueDrain(ListForConnection(connectionId));
    }

    public bool AttachObservePane(string subscriptionId, string paneId, string attachmentId)
    {
        var sub = Get(subscriptionId);
        if (sub is null || sub.IsClosed)
            return false;
        sub.AttachPane(paneId, attachmentId, mode: "observe");
        return true;
    }

    public bool AttachControlPane(string subscriptionId, string paneId, string attachmentId)
    {
        var sub = Get(subscriptionId);
        if (sub is null || sub.IsClosed)
            return false;
        sub.AttachPane(paneId, attachmentId, mode: "control");
        return true;
    }

    public void DetachPane(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;

        lock (_gate)
        {
            foreach (var sub in _byId.Values)
                sub.DetachPane(paneId);
            _frames.DropPane(paneId);
        }
    }

    public void FenceAttachRender(string subscriptionId, string paneId, long minRenderSeq)
    {
        var sub = Get(subscriptionId);
        sub?.FenceAttachRender(paneId, minRenderSeq);
    }

    public void FenceAttachedPaneRender(string paneId, long minRenderSeq)
    {
        if (string.IsNullOrWhiteSpace(paneId) || minRenderSeq <= 0)
            return;

        lock (_gate)
        {
            foreach (var sub in _byId.Values)
            {
                if (!sub.IsClosed)
                    sub.FenceAttachRender(paneId, minRenderSeq);
            }
        }
    }

    /// <inheritdoc />
    public bool PostLive(RuntimeEventRecord record)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        var work = new FanoutWork(
            record,
            DrainTargets: null,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ConnectionId: null);
        lock (_postGate)
        {
            if (!_liveChannel.Writer.TryWrite(work))
            {
                work.Completion.TrySetCanceled();
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public ValueTask WhenQueuedDeliveredAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return ValueTask.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new FanoutWork(Record: null, DrainTargets: null, tcs, ConnectionId: null);
        lock (_postGate)
        {
            if (!_liveChannel.Writer.TryWrite(work))
            {
                tcs.TrySetCanceled();
                return ValueTask.CompletedTask;
            }
        }

        if (!ct.CanBeCanceled)
            return new ValueTask(tcs.Task);

        return new ValueTask(WaitBarrierAsync(tcs, ct));
    }

    private static async Task WaitBarrierAsync(TaskCompletionSource tcs, CancellationToken ct)
    {
        using var reg = ct.Register(static state =>
        {
            ((TaskCompletionSource)state!).TrySetCanceled();
        }, tcs);
        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <inheritdoc />
    public bool PostLiveBatch(IReadOnlyList<RuntimeEventRecord> records) =>
        PostLiveBatchCore(records, paneId: null, identity: null);

    /// <inheritdoc />
    public bool AllLiveHaveFrame(string paneId, string identity)
    {
        if (string.IsNullOrWhiteSpace(paneId) || string.IsNullOrWhiteSpace(identity))
            return false;

        lock (_gate)
        {
            foreach (var sub in _byId.Values)
            {
                if (sub.IsClosed || !sub.Live || !sub.ObservesPane(paneId))
                    continue;
                if (!sub.HasFrameIdentity(paneId, identity))
                    return false;
            }

            // No live observer needs the frame. Attach takes a fresh full
            // capture, so skipping the capture/pack/post for this frame is
            // safe and avoids packing a full snapshot nobody receives.
            return true;
        }
    }

    /// <inheritdoc />
    public void ClearPaneFrameIdentities(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;

        lock (_gate)
        {
            foreach (var sub in _byId.Values)
                sub.ClearFrameIdentity(paneId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EventSubscription> ListLiveObservers(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return [];

        lock (_gate)
        {
            var list = new List<EventSubscription>();
            foreach (var sub in _byId.Values)
            {
                if (sub.IsClosed || !sub.Live || !sub.ObservesPane(paneId))
                    continue;
                list.Add(sub);
            }

            list.Sort(static (a, b) =>
                string.CompareOrdinal(a.SubscriptionId, b.SubscriptionId));
            return list;
        }
    }

    /// <inheritdoc />
    public bool HasLiveObservers(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;

        lock (_gate)
        {
            foreach (var sub in _byId.Values)
            {
                if (sub.IsClosed || !sub.Live || !sub.ObservesPane(paneId))
                    continue;
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public bool MayObserveOutput(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;

        lock (_gate)
        {
            foreach (var sub in _byId.Values)
            {
                if (sub.IsClosed || !sub.ObservesPane(paneId))
                    continue;
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public bool PostLiveBatch(
        IReadOnlyList<RuntimeEventRecord> records,
        string paneId,
        string identity)
        => PostLiveBatchCore(records, paneId, identity);

    private bool PostLiveBatchCore(
        IReadOnlyList<RuntimeEventRecord> records,
        string? paneId,
        string? identity)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            return true;
        if (paneId is { Length: > 0 } && identity is { Length: > 0 })
        {
            var stamped = new RuntimeEventRecord[records.Count];
            for (var i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                stamped[i] = rec with
                {
                    FramePaneId = rec.FramePaneId ?? paneId,
                    FrameIdentity = rec.FrameIdentity ?? identity,
                    Lane = rec.Lane ?? WriterLaneNames.Ordered,
                };
            }

            records = stamped;
        }
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        List<EventSubscription> targets;
        lock (_gate)
            targets = _byId.Values.Where(s => !s.IsClosed && s.Live).ToList();

        var matching = new List<EventSubscription>();
        var anyObserver = false;
        foreach (var sub in targets)
        {
            if (sub.IsClosed)
                continue;

            var matchCount = CountBatchMatches(sub, records);
            if (matchCount == 0)
                continue;
            if (matchCount != records.Count)
                return false;

            anyObserver = true;
            if (paneId is not null
                && identity is not null
                && sub.HasFrameIdentity(paneId, identity))
                continue;

            matching.Add(sub);
        }

        if (!anyObserver)
            return false;
        if (matching.Count == 0)
            return true;

        matching.Sort(static (a, b) =>
            string.CompareOrdinal(a.SubscriptionId, b.SubscriptionId));

        var acquired = new List<EventSubscription>(matching.Count);
        try
        {
            if (!TryAcquireDeliverGates(matching, acquired))
                return false;

            var enqueue = new List<EventSubscription>(acquired.Count);
            foreach (var sub in acquired)
            {
                if (sub.IsClosed || CountBatchMatches(sub, records) != records.Count)
                    return false;

                // Re-check the equality gate under the delivery gate. The
                // pre-gate check raced with a concurrent identical post:
                // without this re-check both posts could enqueue duplicate
                // frames for the same pane identity.
                if (paneId is not null
                    && identity is not null
                    && sub.HasFrameIdentity(paneId, identity))
                    continue;

                enqueue.Add(sub);
            }

            if (enqueue.Count == 0)
                return true;

            var accepted = new List<EventSubscription>(enqueue.Count);
            foreach (var sub in enqueue)
            {
                if (!sub.TryEnqueueLiveBatch(records, out _))
                {
                    RollbackAccepted(accepted, records);
                    return false;
                }

                accepted.Add(sub);
                if (paneId is { Length: > 0 } && identity is { Length: > 0 })
                    sub.NoteQueuedFrameIdentity(paneId, identity);
            }

            if (!EnqueueDrain(accepted))
            {
                RollbackAccepted(accepted, records);
                return false;
            }

            return true;
        }
        finally
        {
            ReleaseDeliverGates(acquired);
        }
    }

    private static int CountBatchMatches(
        EventSubscription sub, IReadOnlyList<RuntimeEventRecord> records)
    {
        var matchCount = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            if (sub.MatchesSubscription(rec) && sub.MatchesObserveFilter(rec))
                matchCount++;
        }

        return matchCount;
    }

    private static bool TryAcquireDeliverGates(
        List<EventSubscription> matching, List<EventSubscription> acquired)
    {
        foreach (var sub in matching)
        {
            try
            {
                sub.DeliverGate.Wait();
                acquired.Add(sub);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        return true;
    }

    private static void ReleaseDeliverGates(List<EventSubscription> acquired)
    {
        for (var i = acquired.Count - 1; i >= 0; i--)
        {
            try { acquired[i].DeliverGate.Release(); }
            catch (ObjectDisposedException) { /* closed mid-batch */ }
            catch (SemaphoreFullException) { /* released twice */ }
        }
    }

    private bool EnqueueDrain(IReadOnlyList<EventSubscription> accepted)
    {
        List<EventSubscription>? live = null;
        for (var i = 0; i < accepted.Count; i++)
        {
            var sub = accepted[i];
            if (!sub.LiveEnabled || sub.IsClosed)
                continue;
            live ??= new List<EventSubscription>(accepted.Count);
            live.Add(sub);
        }

        if (live is null || live.Count == 0)
            return true;

        var work = new FanoutWork(
            Record: null,
            DrainTargets: live,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ConnectionId: null);
        lock (_postGate)
        {
            if (!_liveChannel.Writer.TryWrite(work))
            {
                work.Completion.TrySetCanceled();
                return false;
            }
        }

        return true;
    }

    private static void RollbackAccepted(
        List<EventSubscription> accepted, IReadOnlyList<RuntimeEventRecord> records)
    {
        foreach (var sub in accepted)
        {
            sub.RemoveLiveRecords(records);
            foreach (var rec in records)
            {
                if (rec.FramePaneId is { Length: > 0 })
                    sub.ClearQueuedFrameIdentity(rec.FramePaneId);
            }
        }
    }

    private static async Task DrainEnqueuedAsync(EventSubscription sub)
    {
        try
        {
            await sub.DeliverGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (sub.IsClosed)
                return;
            await DrainAndWriteAsync(sub, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // closed while draining
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        finally
        {
            try { sub.DeliverGate.Release(); }
            catch (ObjectDisposedException) { /* closed mid-deliver */ }
        }
    }

    /// <summary>
    /// Post and wait until this record has been delivered (or dropped/closed) for every
    /// matching subscription. Await outside the session emit gate so slow observers do not
    /// stall journal appends.
    /// </summary>
    public ValueTask FanoutLiveAsync(RuntimeEventRecord record, CancellationToken ct = default) =>
        EnqueueFanoutAsync(record, connectionId: null, ct);

    public ValueTask FanoutLiveToConnectionAsync(
        RuntimeEventRecord record,
        string connectionId,
        CancellationToken ct = default) =>
        EnqueueFanoutAsync(record, connectionId, ct);

    private async ValueTask EnqueueFanoutAsync(
        RuntimeEventRecord record,
        string? connectionId,
        CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new FanoutWork(record, DrainTargets: null, tcs, connectionId);
        lock (_postGate)
        {
            if (!_liveChannel.Writer.TryWrite(work))
            {
                tcs.TrySetCanceled();
                return;
            }
        }

        using var reg = ct.Register(static state =>
        {
            ((TaskCompletionSource)state!).TrySetCanceled();
        }, tcs);

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Record stays in the channel for best-effort delivery; caller is unblocked.
        }
    }

    private async Task FanoutLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var work in _liveChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (work.DrainTargets is { Count: > 0 } drains)
                    {
                        if (drains.Count == 1)
                        {
                            await DrainEnqueuedAsync(drains[0]).ConfigureAwait(false);
                        }
                        else
                        {
                            var tasks = new Task[drains.Count];
                            for (var i = 0; i < drains.Count; i++)
                                tasks[i] = DrainEnqueuedAsync(drains[i]);
                            await Task.WhenAll(tasks).ConfigureAwait(false);
                        }
                    }
                    else if (work.Record is not null)
                    {
                        await DeliverRecordAsync(work.Record, ct, work.ConnectionId)
                            .ConfigureAwait(false);
                    }

                    work.Completion.TrySetResult();
                    NoteQueueProbe();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    work.Completion.TrySetCanceled(ct);
                    break;
                }
                catch (Exception ex)
                {
                    work.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        finally
        {
            while (_liveChannel.Reader.TryRead(out var leftover))
                leftover.Completion.TrySetCanceled();
        }
    }

    private void NoteQueueProbe()
    {
        var probe = _queueProbe;
        if (probe is null)
            return;
        if (Interlocked.CompareExchange(ref _probeQueued, 1, 0) != 0)
            return;

        ThreadPool.QueueUserWorkItem(static state =>
        {
            var hub = (EventSubscriptionHub)state!;
            try
            {
                var queued = hub._queueProbe;
                if (queued is null)
                    return;
                queued.WriteIfChanged(hub.ReadQueueSnapshot());
            }
            catch
            {
            }
            finally
            {
                Volatile.Write(ref hub._probeQueued, 0);
            }
        }, this);
    }

    private async Task DeliverRecordAsync(
        RuntimeEventRecord record,
        CancellationToken ct,
        string? connectionId = null)
    {
        List<EventSubscription> targets;
        if (!string.IsNullOrEmpty(connectionId))
        {
            targets = ListForConnection(connectionId)
                .Where(static s => !s.IsClosed && s.Live)
                .ToList();
        }
        else
        {
            lock (_gate)
                targets = _byId.Values.Where(s => !s.IsClosed && s.Live).ToList();
        }

        if (targets.Count == 0)
            return;

        // Parallel per-sub: one connection's slow WriteLine must not delay
        // other connections for this record. Pre-live path does not wait.
        if (targets.Count == 1)
        {
            await DeliverToSubscriptionAsync(targets[0], record, ct).ConfigureAwait(false);
            return;
        }

        var tasks = new Task[targets.Count];
        for (var i = 0; i < targets.Count; i++)
            tasks[i] = DeliverToSubscriptionAsync(targets[i], record, ct);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task DeliverToSubscriptionAsync(
        EventSubscription sub, RuntimeEventRecord record, CancellationToken ct)
    {
        if (sub.IsClosed)
            return;
        if (!sub.MatchesSubscription(record))
            return;
        if (!sub.MatchesObserveFilter(record))
            return;

        if (!sub.LiveEnabled)
        {
            // Never await per-sub pre-live backpressure on the shared fanout
            // worker. Reliable events may exceed PreLiveMaxBytes; droppable
            // events are evicted. A wait here stalled every later record for
            // every other connection until EnableLive.
            //
            // Use the pre-live policy only. TryEnqueueLive re-reads
            // LiveEnabled under the lock: EnableLive between this sample
            // and that lock switches onto the 256 live cap while the
            // pre-live queue can already hold more than 256 reliable
            // frames. Discarding a false+backpressure result drops the
            // current lease.changed / pane.lifecycle.
            var enqueued = sub.TryEnqueuePreLive(record, out _);
            if (sub.IsClosed)
                return;

            if (!enqueued)
            {
                // Pre-live policy rejected (already emitted or droppable
                // overflow). If EnableLive flipped, live backpressure can
                // drain-and-retry instead of dropping a reliable event.
                if (!sub.LiveEnabled)
                    return;
            }
            else if (!sub.LiveEnabled)
            {
                return;
            }
            else
            {
                // Subscribe completed (EnableLive) after we enqueued with
                // the pre-live policy. Deliver under the gate so the
                // record cannot sit in _liveQueue after DrainLiveQueue
                // and miss the subscribe flush.
                try
                {
                    await sub.DeliverGate.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    if (sub.IsClosed)
                        return;
                    await DrainAndWriteAsync(sub, ct).ConfigureAwait(false);
                }
                finally
                {
                    try { sub.DeliverGate.Release(); }
                    catch (ObjectDisposedException) { /* closed mid-deliver */ }
                }

                return;
            }
        }

        try
        {
            await sub.DeliverGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (sub.IsClosed)
                return;
            await DeliverUnderGateAsync(sub, record, ct).ConfigureAwait(false);
        }
        finally
        {
            try { sub.DeliverGate.Release(); }
            catch (ObjectDisposedException) { /* closed mid-deliver */ }
        }
    }

    private static async Task DeliverUnderGateAsync(
        EventSubscription sub, RuntimeEventRecord record, CancellationToken ct)
    {
        while (true)
        {
            if (sub.IsClosed || ct.IsCancellationRequested)
                return;

            if (sub.TryEnqueueLive(record, out var backpressure))
            {
                await DrainAndWriteAsync(sub, ct).ConfigureAwait(false);
                return;
            }

            if (!backpressure)
                return;

            await DrainAndWriteAsync(sub, ct).ConfigureAwait(false);

            if (sub.IsClosed)
                return;
            if (sub.TryEnqueueLive(record, out backpressure))
            {
                await DrainAndWriteAsync(sub, ct).ConfigureAwait(false);
                return;
            }

            if (!backpressure)
                return;

            try
            {
                await sub.WaitForQueueSpaceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task DrainAndWriteAsync(EventSubscription sub, CancellationToken ct)
    {
        var list = sub.DrainLiveQueue();
        for (var i = 0; i < list.Count; i++)
        {
            var rec = list[i];
            if (sub.IsClosed || ct.IsCancellationRequested)
            {
                sub.RequeueLiveFrontRange(list, i);
                return;
            }

            try
            {
                if (sub.Sink.UsesWriterLanes
                    && rec.Reliability == EventReliability.Render
                    && IsOrderedRenderRecord(rec))
                {
                    var end = i + 1;
                    while (end < list.Count
                        && list[end].Reliability == EventReliability.Render
                        && IsOrderedRenderRecord(list[end])
                        && SameJsonFullGeneration(rec, list[end]))
                    {
                        end++;
                    }

                    var lines = new ReadOnlyMemory<byte>[end - i];
                    for (var n = i; n < end; n++)
                        lines[n - i] = FormatRuntimeEventNdjsonLine(list[n], sub.SubscriptionId);

                    if (!sub.Sink.TryResolveAttachRenderBinding(rec, out var batchBinding))
                    {
                        for (var n = i; n < end; n++)
                            sub.MarkEmitted(list[n]);
                        i = end - 1;
                        continue;
                    }

                    var admitted = batchBinding is null
                        ? sub.Sink.EnqueueOrderedRenderBatch(lines)
                        : sub.Sink.EnqueueOrderedRenderBatch(lines, batchBinding);
                    if (admitted.IsFull)
                    {
                        sub.RequeueLiveFrontRange(list, i);
                        return;
                    }

                    if (!admitted.IsOk)
                    {
                        sub.RequeueLiveFrontRange(list, i);
                        if (admitted.IsClosed)
                            throw new IOException("Event writer closed.");
                        return;
                    }

                    for (var n = i; n < end; n++)
                    {
                        var done = list[n];
                        if (done.FramePaneId is { Length: > 0 } pane
                            && done.FrameIdentity is { Length: > 0 } identity)
                        {
                            sub.CommitFrameIdentity(pane, identity);
                        }

                        sub.MarkEmitted(done);
                    }

                    i = end - 1;
                    continue;
                }

                var line = FormatBoundedRuntimeEventNdjsonLine(
                    rec, sub.SubscriptionId, LineCapFor(sub.Sink));
                if (line.IsEmpty)
                {
                    if (IsPaneInputRejected(rec))
                    {
                        sub.Sink.CompleteWriter();
                        throw new IOException(
                            "Reject event exceeded the connection NDJSON line cap.");
                    }

                    sub.MarkEmitted(rec);
                    continue;
                }

                if (sub.Sink.UsesWriterLanes)
                {
                    WriterLaneResult admitted;
                    if (rec.Reliability == EventReliability.Render)
                    {
                        if (!sub.Sink.TryResolveAttachRenderBinding(rec, out var emitBinding))
                        {
                            sub.MarkEmitted(rec);
                            continue;
                        }

                        admitted = emitBinding is null
                            ? sub.Sink.TryEnqueueRender(line)
                            : sub.Sink.TryEnqueueRender(line, emitBinding);
                        if (admitted.IsFull)
                        {
                            sub.RequeueLiveFrontRange(list, i);
                            return;
                        }
                    }
                    else
                    {
                        admitted = sub.Sink.EnqueueReliableEvent(line);
                    }

                    if (admitted.IsTooLarge)
                    {
                        if (IsPaneInputRejected(rec))
                        {
                            sub.Sink.CompleteWriter();
                            throw new IOException(
                                "Reject event exceeded the connection NDJSON line cap.");
                        }

                        sub.MarkEmitted(rec);
                        continue;
                    }

                    if (!admitted.IsOk)
                    {
                        sub.RequeueLiveFrontRange(list, i);
                        if (admitted.IsClosed)
                            throw new IOException("Event writer closed.");
                        return;
                    }
                }
                else
                {
                    await sub.Sink.WriteLineAsync(Encoding.UTF8.GetString(line.Span[..^1]), ct)
                        .ConfigureAwait(false);
                }

                if (rec.FramePaneId is { Length: > 0 } paneId
                    && rec.FrameIdentity is { Length: > 0 } frameIdentity)
                {
                    sub.CommitFrameIdentity(paneId, frameIdentity);
                }

                sub.MarkEmitted(rec);
            }
            catch
            {
                sub.RequeueLiveFrontRange(list, i);
                throw;
            }
        }

        sub.SignalQueueSpace();
    }

    /// <summary>
    /// Format a runtime.event NDJSON line. Envelope fields go through
    /// <see cref="Utf8JsonWriter"/>; payload JSON is spliced with
    // / WriteRawValue.
    /// message one time. Control characters in <c>type</c> /
    /// <c>subscription_id</c> cannot split the frame.
    /// </summary>
    public static string FormatRuntimeEventLine(RuntimeEventRecord record, string subscriptionId) =>
        Encoding.UTF8.GetString(FormatRuntimeEventUtf8(record, subscriptionId));

    private static int LineCapFor(IEventPushSink sink)
    {
        var cap = sink.MaxLineBytes;
        return cap > 0 ? cap : AttachSnapshotPacker.MaxNdjsonLineBytes;
    }

    private static bool IsPaneInputRejected(RuntimeEventRecord rec) =>
        string.Equals(rec.Type, ProtocolEventTypes.PaneInputRejected, StringComparison.Ordinal);

    /// <summary>
    /// Format one runtime.event NDJSON line and keep it inside the socket
    /// cap. <c>pane.input_rejected</c> is refit so a safe reject still
    // / ships. Other oversize records are dropped.
    /// <c>src/protocol/wire.rs:1564-1578</c> rejects an oversized frame
    /// without panicking.
    /// </summary>
    internal static byte[] FormatBoundedRuntimeEventUtf8(
        RuntimeEventRecord record,
        string subscriptionId,
        int maxLineBytes = AttachSnapshotPacker.MaxNdjsonLineBytes)
    {
        var line = FormatBoundedRuntimeEventNdjsonLine(record, subscriptionId, maxLineBytes);
        if (line.IsEmpty)
            return [];
        return line.Span[..^1].ToArray();
    }

    /// <summary>
    /// Validating read over one UTF-8 JSON value. Product code passes null
    /// and the formatter uses <see cref="IsCompleteJsonValue"/>. A test
    /// passes a counting delegate. The seam holds no static state.
    /// </summary>
    internal delegate bool Utf8JsonValueValidator(ReadOnlySpan<byte> utf8);

    /// <summary>Length of <c>yyyy-MM-ddTHH:mm:ssZ</c> in ASCII bytes.</summary>
    private const int Rfc3339SecondsLength = 20;

    /// <summary>
    /// Write <paramref name="utc"/> as <c>yyyy-MM-ddTHH:mm:ssZ</c> ASCII
    /// bytes with no allocation. The writer is culture-invariant:
    /// <c>DateTime.ToString</c> reads <c>CurrentCulture</c>, which can
    /// carry a non-Gregorian calendar. Sub-second parts are truncated;
    /// the wire format has one-second resolution.
    /// </summary>
    private static void WriteRfc3339Seconds(DateTime utc, Span<byte> destination)
    {
        if (destination.Length < Rfc3339SecondsLength)
            throw new ArgumentException("Destination too short.", nameof(destination));
        WriteDigits4(destination, 0, utc.Year);
        destination[4] = (byte)'-';
        WriteDigits2(destination, 5, utc.Month);
        destination[7] = (byte)'-';
        WriteDigits2(destination, 8, utc.Day);
        destination[10] = (byte)'T';
        WriteDigits2(destination, 11, utc.Hour);
        destination[13] = (byte)':';
        WriteDigits2(destination, 14, utc.Minute);
        destination[16] = (byte)':';
        WriteDigits2(destination, 17, utc.Second);
        destination[19] = (byte)'Z';
    }

    private static void WriteDigits2(Span<byte> destination, int offset, int value)
    {
        destination[offset] = (byte)('0' + ((value / 10) % 10));
        destination[offset + 1] = (byte)('0' + (value % 10));
    }

    private static void WriteDigits4(Span<byte> destination, int offset, int value)
    {
        destination[offset] = (byte)('0' + ((value / 1000) % 10));
        destination[offset + 1] = (byte)('0' + ((value / 100) % 10));
        destination[offset + 2] = (byte)('0' + ((value / 10) % 10));
        destination[offset + 3] = (byte)('0' + (value % 10));
    }

    /// <summary>
    /// Fixed envelope bytes around the variable fields: JSON punctuation
    /// and keys (130), the 20 <c>occurred_at</c> bytes, up to 20
    /// <c>seq</c> digits, and margin. Each variable string is bounded by
    /// <see cref="JsonStringUpperBound"/>. The bound makes writer growth
    /// impossible, so the line buffer allocates one array.
    /// </summary>
    private const int EnvelopeFixedBytes = 224;

    private static int ComputeNdjsonLineCapacity(
        int payloadLength,
        string subscriptionId,
        string type,
        string reliability,
        string lane)
    {
        var bound = payloadLength + EnvelopeFixedBytes + 1; // +1 is the trailing newline
        bound += JsonStringUpperBound(subscriptionId);
        bound += JsonStringUpperBound(type);
        bound += JsonStringUpperBound(reliability);
        bound += JsonStringUpperBound(lane);
        return bound;
    }

    /// <summary>
    /// Upper bound for one JSON string value in UTF-8 bytes, with no
    /// allocation. Printable ASCII passes through; any other unit budgets
    /// five extra bytes, the worst case for a <c>\uXXXX</c> escape of one
    /// UTF-16 unit.
    /// </summary>
    private static int JsonStringUpperBound(string value)
    {
        var bound = Encoding.UTF8.GetByteCount(value);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c < 0x20 || c > 0x7E
                || c == '"' || c == '\\' || c == '<' || c == '>' || c == '&' || c == '\'')
                bound += 5;
        }

        return bound;
    }

    private static void WriteRuntimeEvent(
        IBufferWriter<byte> destination,
        RuntimeEventRecord record,
        string subscriptionId,
        Utf8JsonValueValidator? validator)
    {
        var reliability = EventReliabilityMap.ToWire(record.Reliability);
        var lane = record.Lane ?? LaneForRecord(record);
        ReadOnlySpan<byte> payloadUtf8 = "null"u8;
        if (record.PayloadUtf8 is { Length: > 0 } owned)
        {
            if (record.PayloadUtf8Trusted || (validator ?? IsCompleteJsonValue)(owned))
                payloadUtf8 = owned;
        }
        else if (TryGetUtf8JsonValue(record.PayloadJson, out var payloadOwned))
            payloadUtf8 = payloadOwned;

        if (TryStampAttachSurfaceIdentity(payloadUtf8, record, out var stampedPayload))
            payloadUtf8 = stampedPayload;

        Span<byte> occurred = stackalloc byte[Rfc3339SecondsLength];
        WriteRfc3339Seconds(record.OccurredAt.UtcDateTime, occurred);

        using (var writer = new Utf8JsonWriter(destination))
        {
            writer.WriteStartObject();
            writer.WriteString("event"u8, "runtime.event"u8);
            writer.WritePropertyName("params"u8);
            writer.WriteStartObject();
            writer.WriteString("subscription_id"u8, subscriptionId);
            writer.WriteNumber("seq"u8, record.Seq);
            writer.WriteString("type"u8, record.Type);
            writer.WriteString("reliability"u8, reliability);
            writer.WriteString("lane"u8, lane);
            writer.WriteString("occurred_at"u8, occurred);
            writer.WritePropertyName("payload"u8);
            writer.WriteRawValue(payloadUtf8, skipInputValidation: true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }

    public static byte[] FormatRuntimeEventUtf8(RuntimeEventRecord record, string subscriptionId)
    {
        var line = FormatRuntimeEventNdjsonLine(record, subscriptionId);
        return line.Span[..^1].ToArray();
    }

    /// <summary>
    /// Format one NDJSON line, trailing <c>\n</c> included, as a view over
    // / one array.
    /// message one time, then writes the length prefix and the payload as
    /// two writes without joining them. The Hypa transport is NDJSON, so
    /// the equivalent is one buffer that the formatter writes one time
    /// and hands to the writer lane. The array belongs to the returned
    /// memory. No pool holds it and no code returns it to a pool, so no
    /// lane can read a buffer that another lane recycled.
    /// </summary>
    internal static ReadOnlyMemory<byte> FormatRuntimeEventNdjsonLine(
        RuntimeEventRecord record,
        string subscriptionId,
        Utf8JsonValueValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(subscriptionId);
        var reliability = EventReliabilityMap.ToWire(record.Reliability);
        var lane = record.Lane ?? LaneForRecord(record);
        var payloadBound = record.PayloadUtf8?.Length ?? 0;
        if (record.PayloadUtf8 is null && !string.IsNullOrEmpty(record.PayloadJson))
            payloadBound = Encoding.UTF8.GetByteCount(record.PayloadJson);

        var buffer = new ArrayBufferWriter<byte>(
            ComputeNdjsonLineCapacity(payloadBound, subscriptionId, record.Type, reliability, lane));
        WriteRuntimeEvent(buffer, record, subscriptionId, validator);
        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
        return buffer.WrittenMemory;
    }

    /// <summary>
    /// Format one NDJSON line, trailing <c>\n</c> included, and keep it
    /// inside the socket cap. The line already holds the newline, so the
    /// bound check compares the whole buffer length against the cap with
    /// no extra <c>+ 1</c>. That is the same arithmetic as
    /// <see cref="AttachCellsLinePacker.FitsNdjsonLine(ReadOnlySpan{byte}, int)"/>,
    // / which adds one for the newline.
    /// <c>src/protocol/wire.rs:1564-1578</c> checks the same claimed
    /// length that it then reads.
    /// </summary>
    internal static ReadOnlyMemory<byte> FormatBoundedRuntimeEventNdjsonLine(
        RuntimeEventRecord record,
        string subscriptionId,
        int maxLineBytes = AttachSnapshotPacker.MaxNdjsonLineBytes,
        Utf8JsonValueValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (maxLineBytes < 1)
            maxLineBytes = AttachSnapshotPacker.MaxNdjsonLineBytes;
        var line = FormatRuntimeEventNdjsonLine(record, subscriptionId, validator);
        if (line.Length <= maxLineBytes)
            return line;

        if (!string.Equals(record.Type, ProtocolEventTypes.PaneInputRejected, StringComparison.Ordinal))
            return ReadOnlyMemory<byte>.Empty;

        var payload = record.PayloadJson;
        if (record.PayloadUtf8 is { Length: > 0 } raw)
            payload = Encoding.UTF8.GetString(raw);
        var fittedPayload = RuntimeEventPayloadJson.FitPaneInputRejectedPayload(
            payload, maxLineBytes, subscriptionId);
        var fitted = record with { PayloadJson = fittedPayload, PayloadUtf8 = null, PayloadUtf8Trusted = false };
        line = FormatRuntimeEventNdjsonLine(fitted, subscriptionId, validator);
        return line.Length <= maxLineBytes ? line : ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    /// Encode <paramref name="payloadJson"/> one time and run the
    /// validating read one time. On failure return false and leave
    /// <paramref name="utf8"/> null; the formatter then takes the string
    /// branch, fails the same check, and writes <c>null</c>.
    /// </summary>
    internal static bool TryStampAttachSurfaceIdentity(
        ReadOnlySpan<byte> payloadUtf8,
        RuntimeEventRecord record,
        out byte[] stamped)
    {
        stamped = null!;
        if (!string.Equals(record.Type, ProtocolEventTypes.TerminalRender, StringComparison.Ordinal))
            return false;
        if (record.AttachEmitBootId is not { Length: > 0 } bootId)
            return false;
        if (record.AttachEmitProjectionRevision is not { } projection
            || record.AttachEmitSurfaceRevision is not { } surface
            || record.AttachEmitColumns is not { } columns
            || record.AttachEmitRows is not { } rows)
            return false;
        if (payloadUtf8.Length == 0 || payloadUtf8.SequenceEqual("null"u8))
            return false;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payloadUtf8);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject obj)
            return false;

        obj["boot_id"] = bootId;
        obj["projection_revision"] = projection;
        obj["surface_revision"] = surface;
        obj["columns"] = columns;
        // Packed cells already own "rows" as TerminalRenderCellRow[].
        // Do not overwrite that array with a height integer.
        if (obj["grid_cols"] is null)
            obj["grid_cols"] = columns;
        if (obj["grid_rows"] is null)
            obj["grid_rows"] = rows;
        if (record.AttachEmitFocused is { } focused)
            obj["focused"] = focused;
        if (record.AttachEmitFocusedPaneId is { Length: > 0 } focusedPane)
            obj["focused_pane_id"] = focusedPane;
        stamped = Encoding.UTF8.GetBytes(obj.ToJsonString());
        return true;
    }

    internal static bool TryEncodeValidatedPayload(string? payloadJson, out byte[]? utf8)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            utf8 = null;
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(payloadJson);
        if (!IsCompleteJsonValue(bytes))
        {
            utf8 = null;
            return false;
        }

        utf8 = bytes;
        return true;
    }

    /// <summary>
    /// Accept a complete JSON value without building a DOM. Invalid or empty
    /// payload JSON becomes <c>null</c> at the call site.
    /// </summary>
    private static bool TryGetUtf8JsonValue(string? payloadJson, out byte[] utf8)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            utf8 = [];
            return false;
        }

        utf8 = Encoding.UTF8.GetBytes(payloadJson);
        return IsCompleteJsonValue(utf8);
    }

    private static bool IsCompleteJsonValue(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
            return false;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read())
                return false;
            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsOrderedRenderRecord(RuntimeEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.Equals(record.Lane, WriterLaneNames.Ordered, StringComparison.Ordinal))
            return true;
        if (string.Equals(record.Lane, WriterLaneNames.Render, StringComparison.Ordinal))
            return false;
        // JSON snapshots are complete Full batches. Never drop them in latest-wins.
        return record.Reliability == EventReliability.Render;
    }

    private static bool SameJsonFullGeneration(RuntimeEventRecord a, RuntimeEventRecord b)
    {
        if (!string.IsNullOrEmpty(a.LivePinGroup)
            && string.Equals(a.LivePinGroup, b.LivePinGroup, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(a.FramePaneId, b.FramePaneId, StringComparison.Ordinal)
            && string.Equals(a.FrameIdentity, b.FrameIdentity, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(a.FrameIdentity);
    }

    private static string LaneForRecord(RuntimeEventRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Lane))
            return record.Lane;
        if (record.Reliability == EventReliability.Render)
            return IsOrderedRenderRecord(record) ? WriterLaneNames.Ordered : WriterLaneNames.Render;
        return WriterLaneNames.Control;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _liveChannel.Writer.TryComplete();
        try
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        try
        {
            await _fanoutLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }

        _shutdownCts.Dispose();
    }

    private readonly record struct FanoutWork(
        RuntimeEventRecord? Record,
        IReadOnlyList<EventSubscription>? DrainTargets,
        TaskCompletionSource Completion,
        string? ConnectionId);
}
