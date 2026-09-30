using System.Text;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Transient observer subscriptions (connection-local). Not durable export acks.
/// Live enqueue applies class-aware drop policy: control/lifecycle never drop; output/render may.
/// </summary>
public interface IEventSubscriptionHub
{
    EventSubscription Register(EventSubscriptionRequest request);

    bool Unregister(string subscriptionId);

    /// <summary>Drop all subscriptions bound to a connection (disconnect).</summary>
    void UnregisterConnection(string connectionId);

    EventSubscription? Get(string subscriptionId);

    IReadOnlyList<EventSubscription> ListForConnection(string connectionId);

    /// <summary>
    /// Ordered handoff of a sequenced event onto the live delivery queue.
    /// Control and Lifecycle stay session-ordered (posted under the session emit
    /// gate). Output and Render are per-pane ordered. Does not wait for socket writes.
    /// Returns <c>true</c> when the record was accepted onto the live queue.
    /// Returns <c>false</c> when the hub is disposed or the queue is closed.
    /// </summary>
    bool PostLive(RuntimeEventRecord record);

    /// <summary>
    /// Wait until every live record queued before this call has been delivered
    /// or dropped. Pane close uses this so a flush tail reaches observers
    /// before observe is removed.
    /// </summary>
    ValueTask WhenQueuedDeliveredAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Enqueue every record of a VT snapshot batch onto each matching
    /// subscription, or enqueue none of this batch on that subscription.
    /// Success requires at least one matching observed live subscription.
    /// A zero-match batch is a no-op and returns <c>false</c>.
    /// Dropping an older other-generation droppable is allowed as a unit.
    /// Evicting a sibling of this batch is failure. On failure, already
    /// enqueued siblings of this batch are rolled back under the deliver
    /// gate so an in-flight drain cannot tear the generation.
    /// Drain is queued on the ordered live channel after every matching
    /// subscription has accepted the batch.
    /// Returns <c>false</c> when the hub is disposed or any matching
    /// subscription cannot keep the full batch.
    /// Does not wait for socket writes.
    /// </summary>
    bool PostLiveBatch(IReadOnlyList<RuntimeEventRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            if (!PostLive(record))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Fan out a sequenced event to matching subscriptions and wait until delivery
    /// work for this record completes (per-sub enqueue / drain). Safe to await outside
    /// the session emit gate so slow observers do not stall journal appends.
    /// Reliable classes backpressure; output/render drop when queues are full.
    /// </summary>
    ValueTask FanoutLiveAsync(RuntimeEventRecord record, CancellationToken ct = default);

    /// <summary>
    /// Deliver one live record only to subscriptions on
    // / <paramref name="connectionId"/>.
    /// keeps Input failure on that client. Default fans out to every matcher.
    /// </summary>
    ValueTask FanoutLiveToConnectionAsync(
        RuntimeEventRecord record,
        string connectionId,
        CancellationToken ct = default) =>
        FanoutLiveAsync(record, ct);

    /// <summary>Bind a pane observation filter onto an existing subscription.</summary>
    bool AttachObservePane(string subscriptionId, string paneId, string attachmentId);

    /// <summary>
    /// Bind a controller attachment (same output filter as observe) after lease check.
    /// </summary>
    bool AttachControlPane(string subscriptionId, string paneId, string attachmentId);

    /// <summary>
    /// Remove a closed pane from every subscription's observe/control filter set
    /// so <c>pane.close</c> does not leave ghost routing entries.
    /// </summary>
    void DetachPane(string paneId);

    /// <summary>
    /// Drop already-queued and later <c>terminal.render</c> events for
    /// <paramref name="paneId"/> with seq below <paramref name="minRenderSeq"/>
    /// on one subscription. The attach snapshot uses this seq as first paint.
    /// </summary>
    void FenceAttachRender(string subscriptionId, string paneId, long minRenderSeq);

    /// <summary>
    /// Raise the first-paint render fence on every live subscription.
    /// Occupant-replace uses this so an older same-pane render cannot paint first.
    /// </summary>
    void FenceAttachedPaneRender(string paneId, long minRenderSeq);

    /// <summary>
    /// Opt-in queue snapshot for attach diagnostics. Does not change drop policy.
    /// </summary>
    EventHubQueueSnapshot ReadQueueSnapshot() => EventHubQueueSnapshot.Empty;

    /// <summary>Retained pane frames and cursors. Diagnostic counter.</summary>
    EventHubFrameRetention ReadFrameRetention() => EventHubFrameRetention.Empty;

    /// <summary>
    /// True when every live observer of <paramref name="paneId"/> already
    /// accepted <paramref name="identity"/>. Also true when no live observer
    /// exists: nothing needs the frame, and attach captures fresh.
    /// </summary>
    bool AllLiveHaveFrame(string paneId, string identity) => false;

    /// <summary>
    /// Enqueue a snapshot batch only for live observers whose baseline differs
    /// from <paramref name="identity"/>. Commits the identity after admission.
    /// Returns true when every targeted observer accepted, or when none need it.
    /// </summary>
    bool PostLiveBatch(
        IReadOnlyList<RuntimeEventRecord> records,
        string paneId,
        string identity)
        => PostLiveBatch(records);

    /// <summary>Drop the accepted frame baseline for one pane on every subscription.</summary>
    void ClearPaneFrameIdentities(string paneId)
    {
    }

    /// <summary>Live observers of <paramref name="paneId"/> in subscription-id order.</summary>
    IReadOnlyList<EventSubscription> ListLiveObservers(string paneId) => [];

    /// <summary>
    /// True when at least one live observer watches <paramref name="paneId"/>.
    /// <c>pty_sources_visible_to_any_render_target</c> returns false at once
    /// when no render target exists. The default classifies through
    /// <see cref="ListLiveObservers"/>; the hub overrides it without a list.
    /// </summary>
    bool HasLiveObservers(string paneId) => ListLiveObservers(paneId).Count > 0;

    /// <summary>
    /// True when any subscription lists <paramref name="paneId"/> in its
    /// observe set, whether or not the subscription is live. A subscription
    /// that observes no pane cannot receive <c>terminal.output</c>.
    /// </summary>
    bool MayObserveOutput(string paneId) => HasLiveObservers(paneId);

    /// <summary>
    /// Redrive live-queue drain after a writer barrier is free so a requeued
    /// JSON remainder does not wait on unrelated PTY output.
    /// </summary>
    bool RequestLiveDrain(string connectionId) => false;
}

/// <summary>Live queue depth and encoded bytes. Separate from item counts.</summary>
public readonly record struct EventHubQueueSnapshot(
    int SubscriptionCount,
    int LiveItemCount,
    long LiveQueuedBytes,
    long OutputDrops)
{
    public static EventHubQueueSnapshot Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>Retained pane frames and per-subscription cursors. Not a wire field.</summary>
public readonly record struct EventHubFrameRetention(
    int PaneRecordCount,
    int RetainedFrameCount,
    int CursorCount,
    int DeferredFrameCount)
{
    public static EventHubFrameRetention Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Per-pane handle on one subscription. Holds generation and identity, not a
/// frame. Frame id zero means no frame.
/// </summary>
internal readonly record struct PaneFrameCursor(
    long AdmittedFrameId,
    long AdmittedGeneration,
    string? AdmittedIdentity,
    string? QueuedIdentity,
    long DeferredFrameId,
    DeferredJsonFull? DeferredJson,
    AttachSurfaceEmitBinding? DeferredEmitBinding = null);

/// <summary>Params for creating a transient subscription.</summary>
public sealed record EventSubscriptionRequest
{
    public required string SubscriptionId { get; init; }
    public required string ConnectionId { get; init; }
    public required long FromSeq { get; init; }
    public required IReadOnlySet<EventClass> Classes { get; init; }
    public required int ReplayBudget { get; init; }
    public required bool Live { get; init; }
    /// <summary>Push sink for this connection (writer-gated NDJSON lines).</summary>
    public required IEventPushSink Sink { get; init; }

    /// <summary>Named type tokens. Empty means no named-type restriction.</summary>
    public IReadOnlySet<string> NamedTypes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>True when the caller listed at least one class token.</summary>
    public bool HasClassTokens { get; init; }

    /// <summary>Optional named-type pane filter.</summary>
    public string? FilterPaneId { get; init; }

    /// <summary>Optional <c>pane.agent_status_changed</c> status filter.</summary>
    public string? FilterAgentStatus { get; init; }
}

/// <summary>Mutable connection-local subscription state.</summary>
public sealed class EventSubscription
{
    private readonly object _gate = new();
    private readonly Queue<RuntimeEventRecord> _liveQueue = new();
    private readonly SemaphoreSlim _queueSpace = new(0, 1);
    private long _lastEmittedSeq;
    private Dictionary<string, long>? _lastEmittedRenderByPane;
    private Dictionary<string, long>? _lastEmittedOutputByPane;
    private bool _liveEnabled;
    private bool _closed;
    private long _outputDrops;
    private long _queuedBytes;
    private HashSet<string>? _observePaneIds;
    private Dictionary<string, long>? _attachRenderMinSeq;
    private List<(string AttachmentId, string PaneId, string Mode)>? _attachments;
    private Dictionary<string, PaneFrameCursor>? _paneCursors;
    private Dictionary<string, long>? _reanchorGenerationByPane;
    private readonly PaneFrameStore _frames;

    public EventSubscription(EventSubscriptionRequest request)
        : this(request, frames: null)
    {
    }

    internal EventSubscription(EventSubscriptionRequest request, PaneFrameStore? frames)
    {
        SubscriptionId = request.SubscriptionId;
        ConnectionId = request.ConnectionId;
        FromSeq = request.FromSeq;
        Classes = request.Classes;
        ReplayBudget = request.ReplayBudget;
        Live = request.Live;
        Sink = request.Sink;
        NamedTypes = request.NamedTypes;
        HasClassTokens = request.HasClassTokens;
        FilterPaneId = request.FilterPaneId;
        FilterAgentStatus = request.FilterAgentStatus;
        _lastEmittedSeq = request.FromSeq;
        _liveEnabled = false;
        _frames = frames ?? new PaneFrameStore();
    }

    public string SubscriptionId { get; }
    public string ConnectionId { get; }
    public long FromSeq { get; }
    public IReadOnlySet<EventClass> Classes { get; }
    public IReadOnlySet<string> NamedTypes { get; }
    public bool HasClassTokens { get; }
    public string? FilterPaneId { get; }
    public string? FilterAgentStatus { get; }
    public int ReplayBudget { get; }
    public bool Live { get; }
    public IEventPushSink Sink { get; }

    /// <summary>
    /// Serializes live enqueue→drain→write for this subscription so concurrent fanouts
    /// cannot interleave runtime.event lines out of seq order on the same connection.
    /// </summary>
    internal SemaphoreSlim DeliverGate { get; } = new(1, 1);

    /// <summary>Max live messages buffered per subscription before drop policy applies.</summary>
    public const int LiveQueueCapacity = 256;

    /// <summary>
    /// Pre-live replay buffer eviction budget (same order as the NDJSON line cap).
    /// Overflow drops/coalesces oldest output/render. Control/lifecycle never drop
    /// and may exceed this budget so the shared fanout worker is not stalled.
    /// </summary>
    public const int PreLiveMaxBytes = 1 * 1024 * 1024;

    public long LastEmittedSeq
    {
        get { lock (_gate) return _lastEmittedSeq; }
    }

    public bool LiveEnabled
    {
        get { lock (_gate) return _liveEnabled; }
    }

    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    public long OutputDrops
    {
        get { lock (_gate) return _outputDrops; }
    }

    public void MarkEmitted(long seq)
    {
        lock (_gate)
        {
            if (seq > _lastEmittedSeq)
                _lastEmittedSeq = seq;
        }
    }

    public void MarkEmitted(RuntimeEventRecord record)
    {
        lock (_gate)
            MarkEmittedUnlocked(record);
    }

    /// <summary>
    /// True when this record's seq domain has already delivered this seq or a later one.
    /// Render uses a per-pane live ordinal. Output uses a per-pane live ordinal.
    /// <c>config.changed</c> and input-undeliverable <c>notification.shown</c> use live keys.
    /// Other control and lifecycle events use the session journal seq.
    /// </summary>
    public bool HasAlreadyEmitted(RuntimeEventRecord record)
    {
        lock (_gate)
            return AlreadyEmittedUnlocked(record);
    }

    public void EnableLive()
    {
        lock (_gate)
            _liveEnabled = true;
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            _liveEnabled = false;
            _liveQueue.Clear();
            _queuedBytes = 0;
            // connection record and its render baseline with it.
            ReleaseAllCursorsUnlocked();
        }

        SignalQueueSpace();

        // Release wait handles so long-lived servers do not leak on unsub cycles.
        try { DeliverGate.Dispose(); }
        catch (ObjectDisposedException) { /* already disposed */ }
        try { _queueSpace.Dispose(); }
        catch (ObjectDisposedException) { /* already disposed */ }
    }

    public void AttachPane(string paneId, string? attachmentId = null, string mode = "observe")
    {
        lock (_gate)
        {
            _observePaneIds ??= new HashSet<string>(StringComparer.Ordinal);
            _observePaneIds.Add(paneId);
            if (!string.IsNullOrWhiteSpace(attachmentId))
            {
                _attachments ??= new List<(string, string, string)>();
                _attachments.Add((attachmentId, paneId, mode));
            }
        }
    }

    internal bool ObservesPane(string paneId)
    {
        lock (_gate)
            return _observePaneIds is not null && _observePaneIds.Contains(paneId);
    }

    internal bool HasFrameIdentity(string paneId, string identity)
    {
        lock (_gate)
        {
            if (_paneCursors is null || !_paneCursors.TryGetValue(paneId, out var cursor))
                return false;
            if (string.Equals(cursor.AdmittedIdentity, identity, StringComparison.Ordinal))
                return true;
            return string.Equals(cursor.QueuedIdentity, identity, StringComparison.Ordinal);
        }
    }

    internal void NoteQueuedFrameIdentity(string paneId, string identity)
    {
        if (string.IsNullOrWhiteSpace(paneId) || string.IsNullOrWhiteSpace(identity))
            return;
        lock (_gate)
        {
            var cursor = CursorUnlocked(paneId) with { QueuedIdentity = identity };
            WriteCursorUnlocked(paneId, cursor);
        }
    }

    internal void ClearQueuedFrameIdentity(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (_paneCursors is null || !_paneCursors.TryGetValue(paneId, out var cursor))
                return;
            WriteCursorUnlocked(paneId, cursor with { QueuedIdentity = null });
        }
    }

    internal void CommitFrameIdentity(string paneId, string identity)
    {
        if (string.IsNullOrWhiteSpace(paneId) || string.IsNullOrWhiteSpace(identity))
            return;
        lock (_gate)
        {
            var cursor = CursorUnlocked(paneId) with { AdmittedIdentity = identity };
            WriteCursorUnlocked(paneId, cursor);
        }
    }

    internal void ClearFrameIdentity(string? paneId = null)
    {
        lock (_gate)
        {
            if (paneId is null)
                ReleaseAllCursorsUnlocked();
            else
                ReleaseCursorUnlocked(paneId);
        }
    }

    internal VtFrame? LastAdmittedFrame(string paneId)
    {
        lock (_gate)
        {
            if (_paneCursors is null || !_paneCursors.TryGetValue(paneId, out var cursor))
                return null;
            return _frames.Frame(paneId, cursor.AdmittedFrameId);
        }
    }

    internal long AdmittedGeneration(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return 0;
        lock (_gate)
        {
            if (_paneCursors is null || !_paneCursors.TryGetValue(paneId, out var cursor))
                return 0;
            return cursor.AdmittedGeneration;
        }
    }

    /// <summary>
    /// Last cells payload UTF-8 admitted for this pane. The array is
    // / immutable. The slot is per pane, not per subscription.
    /// <c>src/server/headless.rs:1563-1578</c> sends one framed value to
    /// every client.
    /// </summary>
    internal byte[]? LastAdmittedPayloadUtf8(string paneId) =>
        _frames.LastPayloadUtf8(paneId);

    internal void NoteAdmittedPayloadUtf8(string paneId, byte[] payloadUtf8)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        ArgumentNullException.ThrowIfNull(payloadUtf8);
        _frames.NotePayloadUtf8(paneId, payloadUtf8);
    }

    internal void CommitLastAdmittedFrame(string paneId, VtFrame frame, long reanchorGeneration)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
            SetAdmittedUnlocked(paneId, frame, VtFrameIdentity.Token(frame), reanchorGeneration);
    }

    /// <summary>
    /// True when the next live cells payload must be a full reanchor.
    /// <c>repaint_pending</c> so the next encode cannot skip.
    /// </summary>
    internal bool ReanchorPending(string paneId) => ReanchorGeneration(paneId) != 0;

    /// <summary>
    /// Generation of the pending re-anchor for this observer and pane.
    /// Zero means no mark. Each mark increases it. A commit clears the
    /// mark only when the generation still matches the value read for
    /// that encode.
    /// </summary>
    internal long ReanchorGeneration(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return 0;
        lock (_gate)
        {
            if (_reanchorGenerationByPane is not null
                && _reanchorGenerationByPane.TryGetValue(paneId, out var generation))
            {
                return generation;
            }

            return 0;
        }
    }

    /// <summary>
    // / Force a full reanchor before the next delta.
    /// <c>src/server/render_stream.rs:58-64</c> <c>request_repaint</c>.
    /// </summary>
    internal void MarkReanchorPending(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            _reanchorGenerationByPane ??= new Dictionary<string, long>(StringComparer.Ordinal);
            _reanchorGenerationByPane.TryGetValue(paneId, out var generation);
            _reanchorGenerationByPane[paneId] = generation + 1;
        }
    }

    internal void DeferLatestFrame(
        string paneId,
        VtFrame frame,
        AttachSurfaceEmitBinding? emitBinding = null)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
            SetDeferredUnlocked(paneId, frame, emitBinding);
    }

    internal List<(string PaneId, VtFrame Frame, AttachSurfaceEmitBinding? EmitBinding)> TakeDeferredFrames()
    {
        lock (_gate)
        {
            if (_paneCursors is null || _paneCursors.Count == 0)
                return [];
            List<(string PaneId, long FrameId, AttachSurfaceEmitBinding? EmitBinding)>? pending = null;
            foreach (var (paneId, cursor) in _paneCursors)
            {
                if (cursor.DeferredFrameId == 0)
                    continue;
                pending ??= new List<(string, long, AttachSurfaceEmitBinding?)>();
                pending.Add((paneId, cursor.DeferredFrameId, cursor.DeferredEmitBinding));
            }

            if (pending is null)
                return [];

            var taken = new List<(string, VtFrame, AttachSurfaceEmitBinding?)>(pending.Count);
            foreach (var (paneId, frameId, emitBinding) in pending)
            {
                var frame = _frames.Frame(paneId, frameId);
                var cursor = CursorUnlocked(paneId) with
                {
                    DeferredFrameId = 0,
                    DeferredEmitBinding = null,
                };
                WriteCursorUnlocked(paneId, cursor);
                _frames.Release(paneId, frameId);
                if (frame is not null)
                    taken.Add((paneId, frame, emitBinding));
            }

            return taken;
        }
    }

    internal void DeferJsonFull(string paneId, DeferredJsonFull batch)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Lines);
        lock (_gate)
        {
            var cursor = CursorUnlocked(paneId) with { DeferredJson = batch };
            WriteCursorUnlocked(paneId, cursor);
        }
    }

    internal List<(string PaneId, DeferredJsonFull Batch)> TakeDeferredJsonFulls()
    {
        lock (_gate)
        {
            if (_paneCursors is null || _paneCursors.Count == 0)
                return [];
            List<(string PaneId, DeferredJsonFull Batch)>? taken = null;
            foreach (var (paneId, cursor) in _paneCursors)
            {
                if (cursor.DeferredJson is null)
                    continue;
                taken ??= new List<(string, DeferredJsonFull)>();
                taken.Add((paneId, cursor.DeferredJson));
            }

            if (taken is null)
                return [];

            foreach (var (paneId, _) in taken)
                WriteCursorUnlocked(paneId, CursorUnlocked(paneId) with { DeferredJson = null });

            return taken;
        }
    }

    /// <summary>
    /// Ignore <c>terminal.render</c> for <paramref name="paneId"/> below
    /// <paramref name="minRenderSeq"/>. The min seq only rises.
    /// Drops matching records already in the live queue.
    /// </summary>
    public void FenceAttachRender(string paneId, long minRenderSeq)
    {
        if (string.IsNullOrWhiteSpace(paneId) || minRenderSeq <= 0)
            return;

        lock (_gate)
        {
            _attachRenderMinSeq ??= new Dictionary<string, long>(StringComparer.Ordinal);
            if (!_attachRenderMinSeq.TryGetValue(paneId, out var current) || minRenderSeq > current)
                _attachRenderMinSeq[paneId] = minRenderSeq;
            DropFencedRendersUnlocked(paneId, _attachRenderMinSeq[paneId]);
            ReleaseCursorUnlocked(paneId);
        }
    }

    /// <summary>Drop observe/control filter entries for a closed pane.</summary>
    public void DetachPane(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;

        lock (_gate)
        {
            _observePaneIds?.Remove(paneId);
            _attachRenderMinSeq?.Remove(paneId);
            ReleaseCursorUnlocked(paneId);
            if (_attachments is not null)
            {
                _attachments.RemoveAll(a =>
                    string.Equals(a.PaneId, paneId, StringComparison.Ordinal));
                if (_attachments.Count == 0)
                    _attachments = null;
            }

            if (_observePaneIds is { Count: 0 })
                _observePaneIds = null;
            if (_attachRenderMinSeq is { Count: 0 })
                _attachRenderMinSeq = null;
        }
    }

    /// <summary>
    /// Drop one attachment id. When <paramref name="dropObserveFilter"/> is set
    /// and no other attachment remains for that pane, drop the observe filter.
    /// </summary>
    public void DetachAttachment(string attachmentId, bool dropObserveFilter)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
            return;

        lock (_gate)
        {
            if (_attachments is null)
                return;
            var removed = _attachments.FindAll(a =>
                string.Equals(a.AttachmentId, attachmentId, StringComparison.Ordinal));
            _attachments.RemoveAll(a =>
                string.Equals(a.AttachmentId, attachmentId, StringComparison.Ordinal));
            if (_attachments.Count == 0)
                _attachments = null;
            if (!dropObserveFilter)
                return;
            foreach (var att in removed)
            {
                var still = _attachments is not null
                    && _attachments.Exists(a =>
                        string.Equals(a.PaneId, att.PaneId, StringComparison.Ordinal)
                        && string.Equals(a.Mode, att.Mode, StringComparison.Ordinal));
                if (still)
                    continue;
                _observePaneIds?.Remove(att.PaneId);
                // client changes what it observes.
                ReleaseCursorUnlocked(att.PaneId);
            }
        }
    }

    /// <summary>Diagnostic: attachment ids bound to this subscription.</summary>
    public IReadOnlyList<(string AttachmentId, string PaneId, string Mode)> ListAttachments()
    {
        lock (_gate)
            return _attachments is null ? [] : _attachments.ToList();
    }

    internal int ReadCursorCount()
    {
        lock (_gate)
            return _paneCursors?.Count ?? 0;
    }

    internal int ReadDeferredFrameCount()
    {
        lock (_gate)
        {
            if (_paneCursors is null || _paneCursors.Count == 0)
                return 0;
            var n = 0;
            foreach (var cursor in _paneCursors.Values)
            {
                if (cursor.DeferredFrameId != 0)
                    n++;
            }

            return n;
        }
    }

    internal bool HasPaneCursor(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        lock (_gate)
            return _paneCursors is not null && _paneCursors.ContainsKey(paneId);
    }

    /// <summary>
    /// Retain the new frame before the old frame is released so a
    /// same-instance replace cannot hit zero and free the grid.
    /// </summary>
    private void SetAdmittedUnlocked(
        string paneId,
        VtFrame frame,
        string identity,
        long reanchorGeneration)
    {
        var newId = _frames.Retain(paneId, frame);
        var old = CursorUnlocked(paneId);
        WriteCursorUnlocked(
            paneId,
            old with
            {
                AdmittedFrameId = newId,
                AdmittedGeneration = frame.Generation,
                AdmittedIdentity = identity,
                DeferredFrameId = 0,
            });
        // A newer mark that landed after the encode read must stay.
        ClearReanchorIfUnchangedUnlocked(paneId, reanchorGeneration);
        if (old.AdmittedFrameId != 0)
            _frames.Release(paneId, old.AdmittedFrameId);
        if (old.DeferredFrameId != 0)
            _frames.Release(paneId, old.DeferredFrameId);
    }

    private void ClearReanchorIfUnchangedUnlocked(string paneId, long reanchorGeneration)
    {
        if (_reanchorGenerationByPane is null
            || !_reanchorGenerationByPane.TryGetValue(paneId, out var current)
            || current != reanchorGeneration)
        {
            return;
        }

        _reanchorGenerationByPane.Remove(paneId);
        if (_reanchorGenerationByPane.Count == 0)
            _reanchorGenerationByPane = null;
    }

    private void ClearReanchorUnlocked(string paneId)
    {
        if (_reanchorGenerationByPane is null)
            return;
        _reanchorGenerationByPane.Remove(paneId);
        if (_reanchorGenerationByPane.Count == 0)
            _reanchorGenerationByPane = null;
    }

    /// <summary>
    // / One deferred value per pane.
    /// <c>src/server/headless.rs:2495-2500</c> reads that flag on drain.
    /// Hypa holds the value by reference into the shared pane record.
    /// </summary>
    private void SetDeferredUnlocked(
        string paneId,
        VtFrame frame,
        AttachSurfaceEmitBinding? emitBinding = null)
    {
        var newId = _frames.Retain(paneId, frame);
        var old = CursorUnlocked(paneId);
        WriteCursorUnlocked(paneId, old with
        {
            DeferredFrameId = newId,
            DeferredEmitBinding = emitBinding,
        });
        if (old.DeferredFrameId != 0)
            _frames.Release(paneId, old.DeferredFrameId);
    }

    private void ReleaseCursorUnlocked(string paneId)
    {
        if (_paneCursors is null || !_paneCursors.Remove(paneId, out var cursor))
        {
            ClearReanchorUnlocked(paneId);
            return;
        }

        if (cursor.AdmittedFrameId != 0)
            _frames.Release(paneId, cursor.AdmittedFrameId);
        if (cursor.DeferredFrameId != 0)
            _frames.Release(paneId, cursor.DeferredFrameId);
        ClearReanchorUnlocked(paneId);
        if (_paneCursors.Count == 0)
            _paneCursors = null;
    }

    private void ReleaseAllCursorsUnlocked()
    {
        if (_paneCursors is not null)
        {
            foreach (var (paneId, cursor) in _paneCursors)
            {
                if (cursor.AdmittedFrameId != 0)
                    _frames.Release(paneId, cursor.AdmittedFrameId);
                if (cursor.DeferredFrameId != 0)
                    _frames.Release(paneId, cursor.DeferredFrameId);
            }

            _paneCursors = null;
        }

        _reanchorGenerationByPane?.Clear();
        _reanchorGenerationByPane = null;
    }

    private PaneFrameCursor CursorUnlocked(string paneId)
    {
        if (_paneCursors is not null && _paneCursors.TryGetValue(paneId, out var cursor))
            return cursor;
        return default;
    }

    private void WriteCursorUnlocked(string paneId, PaneFrameCursor cursor)
    {
        if (IsEmptyCursor(cursor))
        {
            if (_paneCursors is null)
                return;
            _paneCursors.Remove(paneId);
            if (_paneCursors.Count == 0)
                _paneCursors = null;
            return;
        }

        _paneCursors ??= new Dictionary<string, PaneFrameCursor>(StringComparer.Ordinal);
        _paneCursors[paneId] = cursor;
    }

    private static bool IsEmptyCursor(in PaneFrameCursor cursor) =>
        cursor.AdmittedFrameId == 0
        && cursor.AdmittedGeneration == 0
        && cursor.AdmittedIdentity is null
        && cursor.QueuedIdentity is null
        && cursor.DeferredFrameId == 0
        && cursor.DeferredJson is null;

    public bool MatchesClass(EventClass c)
    {
        // Empty set means all classes.
        return Classes.Count == 0 || Classes.Contains(c);
    }

    /// <summary>
    /// Class tokens match every event of that class. Named-only subscribe
    /// matches only those types. Filters apply to named types.
    /// </summary>
    public bool MatchesSubscription(RuntimeEventRecord record)
    {
        if (!MatchesClass(record.Class))
            return false;

        if (NamedTypes.Count > 0 && !HasClassTokens && !NamedTypes.Contains(record.Type))
            return false;

        return MatchesNamedFilters(record);
    }

    private bool MatchesNamedFilters(RuntimeEventRecord record)
    {
        if (string.IsNullOrEmpty(FilterPaneId) && string.IsNullOrEmpty(FilterAgentStatus))
            return true;

        if (record.Type is not (
            ProtocolEventTypes.PaneAgentStatusChanged
            or ProtocolEventTypes.PaneScrollChanged))
        {
            return true;
        }

        var paneId = HyjrPayloadJson.TryGetPaneId(record.PayloadJson);
        if (!string.IsNullOrEmpty(FilterPaneId)
            && !string.Equals(paneId, FilterPaneId, StringComparison.Ordinal))
        {
            return false;
        }

        if (record.Type == ProtocolEventTypes.PaneAgentStatusChanged
            && !string.IsNullOrEmpty(FilterAgentStatus))
        {
            var status = HyjrPayloadJson.TryGetStringField(record.PayloadJson, "agent_status");
            if (!string.Equals(status, FilterAgentStatus, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Output and Render events deliver only when this subscription has an
    /// observe/control attachment for the payload pane (fail closed). Other
    /// classes use the type filter only.
    /// </summary>
    public bool MatchesObserveFilter(RuntimeEventRecord record)
    {
        lock (_gate)
        {
            if (record.Class != EventClass.Output && record.Class != EventClass.Render)
                return true;

            // Live attach exception: popup renders have no pane id. RPC-only
            // connections (no observe/control attachment) stay fail-closed.
            if (record.Class == EventClass.Render
                && HyjrPayloadJson.IsPopupTarget(record.PayloadJson))
            {
                if (!Live || _observePaneIds is null || _observePaneIds.Count == 0)
                    return false;
                return !IsFencedRenderUnlocked(record);
            }

            // Fail closed: terminal.output / terminal.render require observe or control.
            if (_observePaneIds is null || _observePaneIds.Count == 0)
                return false;

            var paneId = PaneKeyOf(record);
            if (paneId is null || !_observePaneIds.Contains(paneId))
                return false;

            return record.Class != EventClass.Render || !IsFencedRenderUnlocked(record);
        }
    }

    /// <summary>
    /// Enqueue every record of a snapshot batch under this lock, or enqueue none
    /// of this batch. Older droppable records may be evicted to make room.
    /// Evicting a sibling of this batch is failure.
    /// </summary>
    internal bool TryEnqueueLiveBatch(IReadOnlyList<RuntimeEventRecord> records, out bool backpressure)
    {
        backpressure = false;
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            return true;

        lock (_gate)
        {
            if (_closed || (!_liveEnabled && !Live))
                return false;

            var keep = new List<RuntimeEventRecord>(records.Count);
            var incomingBytes = 0;
            foreach (var record in records)
            {
                if (AlreadyEmittedUnlocked(record))
                    continue;
                keep.Add(record);
                incomingBytes += EstimateQueuedBytes(record);
            }

            if (keep.Count == 0)
                return true;

            var pinGroup = SharedPinGroup(keep);
            if (!_liveEnabled && Live)
                return EnqueueBatchPreLiveUnderLock(keep, incomingBytes, pinGroup);

            if (keep.Count > LiveQueueCapacity)
                return false;

            while (_liveQueue.Count + keep.Count > LiveQueueCapacity)
            {
                if (!TryDropOldestDroppableUnderLock(pinGroup, evictOtherPinGroups: true))
                    return false;
            }

            foreach (var record in keep)
                EnqueueTracked(record, EstimateQueuedBytes(record));
            return true;
        }
    }

    private bool EnqueueBatchPreLiveUnderLock(
        IReadOnlyList<RuntimeEventRecord> records, int incomingBytes, string? pinGroup)
    {
        while (_queuedBytes + incomingBytes > PreLiveMaxBytes && _liveQueue.Count > 0)
        {
            if (!TryDropOldestDroppableUnderLock(pinGroup, evictOtherPinGroups: true))
                break;
        }

        if (_queuedBytes + incomingBytes <= PreLiveMaxBytes)
        {
            foreach (var record in records)
                EnqueueTracked(record, EstimateQueuedBytes(record));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Try to enqueue a live event. Returns false if dropped (output/render full queue)
    /// or if the subscription is closed / not live-enabled. Live reliable events that
    /// cannot be queued signal backpressure via <paramref name="backpressure"/>.
    /// Pre-live reliable events enqueue even when over the byte eviction budget
    /// (no backpressure) so the shared fanout worker is not stalled.
    /// Re-reads <see cref="LiveEnabled"/> under the lock: an EnableLive flip
    /// switches this call onto the 256 live cap. Fanout that sampled
    /// <c>!LiveEnabled</c> must use <see cref="TryEnqueuePreLive"/> instead.
    /// </summary>
    public bool TryEnqueueLive(RuntimeEventRecord record, out bool backpressure)
    {
        backpressure = false;
        lock (_gate)
        {
            if (_closed || !_liveEnabled)
            {
                // During replay (live not enabled yet), still buffer when Live was requested.
                if (_closed || !Live)
                    return false;

                return EnqueueUnderLock(record, out backpressure);
            }

            return EnqueueUnderLock(record, out backpressure);
        }
    }

    /// <summary>
    /// Enqueue with the pre-live policy only: byte eviction for output/render,
    /// reliable events always accepted. Does not switch to the 256 live cap
    /// if EnableLive races this call. Used by the fanout arm that already
    /// sampled <c>!LiveEnabled</c>.
    /// </summary>
    internal bool TryEnqueuePreLive(RuntimeEventRecord record, out bool backpressure)
    {
        backpressure = false;
        lock (_gate)
        {
            if (_closed || !Live)
                return false;
            if (AlreadyEmittedUnlocked(record))
                return false;

            return EnqueuePreLiveUnderLock(record, EstimateQueuedBytes(record), out backpressure);
        }
    }

    /// <summary>
    /// Drain buffered live events that are newer than the last emitted seq,
    /// ordered by increasing seq (replay buffer may have enqueued concurrently).
    /// </summary>
    public List<RuntimeEventRecord> DrainLiveQueue()
    {
        lock (_gate)
        {
            var list = new List<RuntimeEventRecord>(_liveQueue.Count);
            while (_liveQueue.Count > 0)
            {
                var r = _liveQueue.Dequeue();
                _queuedBytes -= EstimateQueuedBytes(r);
                if (!AlreadyEmittedUnlocked(r))
                    list.Add(r);
            }

            if (_queuedBytes < 0)
                _queuedBytes = 0;

            if (list.Count > 1)
                SortBySeqDomain(list);
            return list;
        }
    }

    /// <summary>Pulse waiters blocked on reliable backpressure after a drain/write.</summary>
    internal void SignalQueueSpace()
    {
        try
        {
            if (_queueSpace.CurrentCount == 0)
                _queueSpace.Release();
        }
        catch (ObjectDisposedException)
        {
            // subscription closed
        }
        catch (SemaphoreFullException)
        {
            // already signaled
        }
    }

    /// <summary>Wait until a drain pulse or cancellation/subscription close.</summary>
    internal async Task WaitForQueueSpaceAsync(CancellationToken ct)
    {
        while (!IsClosed && !ct.IsCancellationRequested)
        {
            try
            {
                // Poll IsClosed every 50ms so a mid-wait Close() does not hang forever.
                if (await _queueSpace.WaitAsync(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false))
                    return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    private bool EnqueueUnderLock(RuntimeEventRecord record, out bool backpressure)
    {
        backpressure = false;
        if (AlreadyEmittedUnlocked(record))
            return false;

        var incomingBytes = EstimateQueuedBytes(record);

        // Pre-live (subscribe replay window): byte cap is an eviction budget
        // for output/render only. Reliable events may exceed it. Disk journal
        // remains the durable never-drop source (§10.2).
        if (!_liveEnabled && Live)
            return EnqueuePreLiveUnderLock(record, incomingBytes, out backpressure);

        if (_liveQueue.Count < LiveQueueCapacity)
        {
            EnqueueTracked(record, incomingBytes);
            return true;
        }

        // Live queue full: never drop control/lifecycle (reliable) — signal backpressure.
        if (record.Reliability == EventReliability.Reliable)
        {
            backpressure = true;
            return false;
        }

        // Do not tear an in-flight snapshot pin group for a single droppable.
        if (!TryDropOldestDroppableUnderLock(protectPinGroup: null, evictOtherPinGroups: false))
        {
            _outputDrops++;
            return false;
        }

        EnqueueTracked(record, incomingBytes);
        return true;
    }

    private bool EnqueuePreLiveUnderLock(
        RuntimeEventRecord record, int incomingBytes, out bool backpressure)
    {
        backpressure = false;
        while (_queuedBytes + incomingBytes > PreLiveMaxBytes && _liveQueue.Count > 0)
        {
            if (!TryDropOldestDroppableUnderLock(protectPinGroup: null, evictOtherPinGroups: false))
                break;
        }

        if (_queuedBytes + incomingBytes <= PreLiveMaxBytes)
        {
            EnqueueTracked(record, incomingBytes);
            return true;
        }

        // Over budget with only reliable remaining (or incoming is huge).
        // Cap evicts output/render only. Never backpressure the shared fanout
        // worker — journal is the durable never-drop source.
        if (record.Reliability == EventReliability.Reliable)
        {
            EnqueueTracked(record, incomingBytes);
            return true;
        }

        _outputDrops++;
        return false;
    }

    private bool TryDropOldestDroppableUnderLock(string? protectPinGroup, bool evictOtherPinGroups)
    {
        if (_liveQueue.Count == 0)
            return false;

        string? evictGroup = null;
        RuntimeEventRecord? dropSingle = null;
        foreach (var r in _liveQueue)
        {
            if (r.Reliability == EventReliability.Reliable)
                continue;

            var group = r.LivePinGroup;
            if (!string.IsNullOrEmpty(group))
            {
                if (!evictOtherPinGroups)
                    continue;
                if (protectPinGroup is not null
                    && string.Equals(group, protectPinGroup, StringComparison.Ordinal))
                {
                    continue;
                }

                evictGroup = group;
                break;
            }

            dropSingle = r;
            break;
        }

        if (evictGroup is not null)
            return DropPinGroupUnlocked(evictGroup);
        if (dropSingle is not null)
            return DropRecordUnlocked(dropSingle);
        return false;
    }

    private bool DropPinGroupUnlocked(string group)
    {
        var kept = new List<RuntimeEventRecord>(_liveQueue.Count);
        var dropped = 0;
        foreach (var r in _liveQueue)
        {
            if (string.Equals(r.LivePinGroup, group, StringComparison.Ordinal))
            {
                dropped++;
                continue;
            }

            kept.Add(r);
        }

        if (dropped == 0)
            return false;

        RebuildQueueUnlocked(kept);
        _outputDrops += dropped;
        InvalidateFrameFromPinGroupUnlocked(group);
        return true;
    }

    private bool DropRecordUnlocked(RuntimeEventRecord record)
    {
        var kept = new List<RuntimeEventRecord>(_liveQueue.Count);
        var found = false;
        foreach (var r in _liveQueue)
        {
            if (!found && ReferenceEquals(r, record))
            {
                found = true;
                continue;
            }

            kept.Add(r);
        }

        if (!found)
            return false;

        RebuildQueueUnlocked(kept);
        _outputDrops++;
        InvalidateFrameFromRecordUnlocked(record);
        return true;
    }

    private void InvalidateFrameFromRecordUnlocked(RuntimeEventRecord record)
    {
        var paneId = PaneKeyOf(record);
        if (string.IsNullOrEmpty(paneId))
            return;
        if (_paneCursors is null || !_paneCursors.TryGetValue(paneId, out var cursor))
            return;
        WriteCursorUnlocked(paneId, cursor with { AdmittedIdentity = null, QueuedIdentity = null });
    }

    private void InvalidateFrameFromPinGroupUnlocked(string group)
    {
        var colon = group.IndexOf(':');
        var paneId = colon > 0 ? group[..colon] : group;
        if (string.IsNullOrEmpty(paneId))
            return;
        if (_paneCursors is null || !_paneCursors.TryGetValue(paneId, out var cursor))
            return;
        WriteCursorUnlocked(paneId, cursor with { AdmittedIdentity = null });
    }

    private void RebuildQueueUnlocked(List<RuntimeEventRecord> kept)
    {
        _liveQueue.Clear();
        _queuedBytes = 0;
        foreach (var r in kept)
            EnqueueTracked(r, EstimateQueuedBytes(r));
    }

    /// <summary>
    /// Remove these exact records from the live queue. Used to roll back a
    /// batch that another matching subscription could not keep.
    /// </summary>
    internal void RemoveLiveRecords(IReadOnlyList<RuntimeEventRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            return;

        lock (_gate)
        {
            var kept = new List<RuntimeEventRecord>(_liveQueue.Count);
            foreach (var rec in _liveQueue)
            {
                var drop = false;
                for (var i = 0; i < records.Count; i++)
                {
                    if (ReferenceEquals(rec, records[i]))
                    {
                        drop = true;
                        break;
                    }
                }

                if (!drop)
                    kept.Add(rec);
            }

            RebuildQueueUnlocked(kept);
        }
    }

    /// <summary>
    /// Dequeue the next not-yet-emitted live record. Returns false when empty.
    /// </summary>
    internal bool TryDequeueLiveHead(out RuntimeEventRecord record)
    {
        lock (_gate)
        {
            while (_liveQueue.Count > 0)
            {
                record = DequeueTracked();
                if (!AlreadyEmittedUnlocked(record))
                    return true;
            }

            record = null!;
            return false;
        }
    }

    /// <summary>
    /// Put a dequeued record back at the front after a write failure.
    /// </summary>
    internal void RequeueLiveFront(RuntimeEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        RequeueLiveFrontRange([record], 0);
    }

    /// <summary>
    /// Put remaining snapshotted records back at the front after a drain
    /// write failure. Concurrent enqueues stay after this range.
    /// </summary>
    internal void RequeueLiveFrontRange(IReadOnlyList<RuntimeEventRecord> records, int start)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (start < 0 || start > records.Count)
            throw new ArgumentOutOfRangeException(nameof(start));

        lock (_gate)
        {
            var rest = new List<RuntimeEventRecord>(_liveQueue.Count);
            while (_liveQueue.Count > 0)
                rest.Add(DequeueTracked());

            RebuildQueueUnlocked([]);
            for (var i = start; i < records.Count; i++)
                EnqueueTracked(records[i], EstimateQueuedBytes(records[i]));
            foreach (var rec in rest)
                EnqueueTracked(rec, EstimateQueuedBytes(rec));
        }
    }

    internal int LiveQueuedCount
    {
        get { lock (_gate) return _liveQueue.Count; }
    }

    internal long LiveQueuedBytes
    {
        get { lock (_gate) return _queuedBytes; }
    }

    internal long OutputDropCount
    {
        get { lock (_gate) return _outputDrops; }
    }

    internal int CountLivePinGroup(string group)
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var rec in _liveQueue)
            {
                if (string.Equals(rec.LivePinGroup, group, StringComparison.Ordinal))
                    n++;
            }

            return n;
        }
    }

    private static string? SharedPinGroup(IReadOnlyList<RuntimeEventRecord> records)
    {
        if (records.Count == 0)
            return null;
        var group = records[0].LivePinGroup;
        if (string.IsNullOrEmpty(group))
            return null;
        for (var i = 1; i < records.Count; i++)
        {
            if (!string.Equals(records[i].LivePinGroup, group, StringComparison.Ordinal))
                return null;
        }

        return group;
    }

    private void EnqueueTracked(RuntimeEventRecord record, int bytes)
    {
        _liveQueue.Enqueue(record);
        _queuedBytes += bytes;
    }

    private RuntimeEventRecord DequeueTracked()
    {
        var dropped = _liveQueue.Dequeue();
        _queuedBytes -= EstimateQueuedBytes(dropped);
        if (_queuedBytes < 0)
            _queuedBytes = 0;
        return dropped;
    }

    private void DropFencedRendersUnlocked(string paneId, long minSeq)
    {
        if (_liveQueue.Count == 0)
            return;

        var kept = new List<RuntimeEventRecord>(_liveQueue.Count);
        var droppedAny = false;
        foreach (var rec in _liveQueue)
        {
            if (rec.Class == EventClass.Render
                && rec.Seq < minSeq
                && string.Equals(
                    HyjrPayloadJson.TryGetRenderKey(rec.PayloadJson) ?? "",
                    paneId,
                    StringComparison.Ordinal))
            {
                droppedAny = true;
                continue;
            }

            kept.Add(rec);
        }

        if (!droppedAny)
            return;

        _liveQueue.Clear();
        _queuedBytes = 0;
        foreach (var rec in kept)
            EnqueueTracked(rec, EstimateQueuedBytes(rec));
    }

    private bool IsFencedRenderUnlocked(RuntimeEventRecord record)
    {
        if (record.Class != EventClass.Render || _attachRenderMinSeq is null)
            return false;

        var paneId = HyjrPayloadJson.TryGetRenderKey(record.PayloadJson) ?? "";
        return _attachRenderMinSeq.TryGetValue(paneId, out var min) && record.Seq < min;
    }

    private bool AlreadyEmittedUnlocked(RuntimeEventRecord record)
    {
        if (IsFencedRenderUnlocked(record))
            return true;

        if (UsesRenderSeqDomain(record))
        {
            var paneId = RenderSeqKey(record);
            if (_lastEmittedRenderByPane is not null &&
                _lastEmittedRenderByPane.TryGetValue(paneId, out var last))
                return record.Seq <= last;
            return false;
        }

        if (record.Class == EventClass.Output)
        {
            var paneId = PaneKeyOf(record) ?? "";
            if (_lastEmittedOutputByPane is not null &&
                _lastEmittedOutputByPane.TryGetValue(paneId, out var last))
                return record.Seq <= last;
            // Live-only output is numbered per pane, not on the journal cursor,
            // so from_seq says nothing about it.
            return false;
        }

        return record.Seq <= _lastEmittedSeq;
    }

    private void MarkEmittedUnlocked(RuntimeEventRecord record)
    {
        if (UsesRenderSeqDomain(record))
        {
            var paneId = RenderSeqKey(record);
            _lastEmittedRenderByPane ??= new Dictionary<string, long>(StringComparer.Ordinal);
            if (!_lastEmittedRenderByPane.TryGetValue(paneId, out var last) || record.Seq > last)
                _lastEmittedRenderByPane[paneId] = record.Seq;
            return;
        }

        if (record.Class == EventClass.Output)
        {
            var paneId = PaneKeyOf(record) ?? "";
            _lastEmittedOutputByPane ??= new Dictionary<string, long>(StringComparer.Ordinal);
            if (!_lastEmittedOutputByPane.TryGetValue(paneId, out var last) || record.Seq > last)
                _lastEmittedOutputByPane[paneId] = record.Seq;
            return;
        }

        if (record.Seq > _lastEmittedSeq)
            _lastEmittedSeq = record.Seq;
    }

    private static void SortBySeqDomain(List<RuntimeEventRecord> list)
    {
        var indexed = new (int Index, RuntimeEventRecord Record)[list.Count];
        for (var i = 0; i < list.Count; i++)
            indexed[i] = (i, list[i]);

        Array.Sort(indexed, static (a, b) =>
        {
            if (SameSeqDomain(a.Record, b.Record))
            {
                var cmp = a.Record.Seq.CompareTo(b.Record.Seq);
                return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
            }

            return a.Index.CompareTo(b.Index);
        });

        for (var i = 0; i < list.Count; i++)
            list[i] = indexed[i].Record;
    }

    private static bool SameSeqDomain(RuntimeEventRecord a, RuntimeEventRecord b)
    {
        if (UsesRenderSeqDomain(a) && UsesRenderSeqDomain(b))
            return string.Equals(RenderSeqKey(a), RenderSeqKey(b), StringComparison.Ordinal);

        if (IsPerPaneLiveClass(a.Class) && a.Class == b.Class)
        {
            var pa = PaneKeyOf(a) ?? "";
            var pb = PaneKeyOf(b) ?? "";
            return string.Equals(pa, pb, StringComparison.Ordinal);
        }

        return !IsPerPaneLiveClass(a.Class)
            && !IsPerPaneLiveClass(b.Class)
            && !UsesRenderSeqDomain(a)
            && !UsesRenderSeqDomain(b);
    }

    private static bool IsPerPaneLiveClass(EventClass c) =>
        c is EventClass.Output or EventClass.Render;

    private static bool UsesRenderSeqDomain(RuntimeEventRecord record) =>
        record.Class == EventClass.Render
        || string.Equals(record.Type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal)
        || string.Equals(record.Type, ProtocolEventTypes.ConfigReloaded, StringComparison.Ordinal)
        || string.Equals(record.Type, ProtocolEventTypes.WorkspaceMetadataUpdated, StringComparison.Ordinal)
        || string.Equals(record.Type, ProtocolEventTypes.PaneMetadataUpdated, StringComparison.Ordinal)
        || string.Equals(record.Type, ProtocolEventTypes.ResourceChanged, StringComparison.Ordinal)
        || string.Equals(record.Type, ProtocolEventTypes.PaneInputRejected, StringComparison.Ordinal)
        || string.Equals(record.Type, ProtocolEventTypes.ConfigChanged, StringComparison.Ordinal)
        || IsInputUndeliverableNotification(record);

    private const string InputUndeliverableReason = "input_undeliverable";

    private static bool IsInputUndeliverableNotification(RuntimeEventRecord record) =>
        string.Equals(record.Type, ProtocolEventTypes.NotificationShown, StringComparison.Ordinal)
        && string.Equals(
            HyjrPayloadJson.TryGetStringField(record.PayloadJson, "reason"),
            InputUndeliverableReason,
            StringComparison.Ordinal)
        && !string.IsNullOrEmpty(HyjrPayloadJson.TryGetPaneId(record.PayloadJson));

    private static string RenderSeqKey(RuntimeEventRecord record)
    {
        if (string.Equals(record.Type, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal))
            return ProtocolEventTypes.TerminalRenderTargetPopup;
        if (string.Equals(record.Type, ProtocolEventTypes.ConfigReloaded, StringComparison.Ordinal))
            return ProtocolEventTypes.ConfigReloaded;
        if (string.Equals(record.Type, ProtocolEventTypes.WorkspaceMetadataUpdated, StringComparison.Ordinal))
            return ProtocolEventTypes.WorkspaceMetadataUpdated;
        if (string.Equals(record.Type, ProtocolEventTypes.PaneMetadataUpdated, StringComparison.Ordinal))
            return ProtocolEventTypes.PaneMetadataUpdated;
        if (string.Equals(record.Type, ProtocolEventTypes.ResourceChanged, StringComparison.Ordinal))
            return ProtocolEventTypes.ResourceChanged;
        if (string.Equals(record.Type, ProtocolEventTypes.PaneInputRejected, StringComparison.Ordinal))
            return ProtocolEventTypes.PaneInputRejected;
        if (string.Equals(record.Type, ProtocolEventTypes.ConfigChanged, StringComparison.Ordinal))
            return ProtocolEventTypes.ConfigChanged;
        if (IsInputUndeliverableNotification(record))
        {
            var paneId = HyjrPayloadJson.TryGetPaneId(record.PayloadJson) ?? "";
            return paneId + ":" + InputUndeliverableReason;
        }

        return HyjrPayloadJson.TryGetRenderKey(record.PayloadJson) ?? "";
    }

    private static string? PaneKeyOf(RuntimeEventRecord record) =>
        record.PaneKey ?? HyjrPayloadJson.TryGetPaneId(record.PayloadJson);

    internal static int EstimateQueuedBytes(RuntimeEventRecord record)
    {
        var payload = record.PayloadUtf8 is { Length: > 0 } utf8
            ? utf8.Length
            : Encoding.UTF8.GetByteCount(record.PayloadJson ?? "");
        var type = Encoding.UTF8.GetByteCount(record.Type ?? "");
        return payload + type + 64;
    }

    /// <summary>Test helper: queued pre-live / live buffer size in bytes.</summary>
    internal long QueuedBytes
    {
        get { lock (_gate) return _queuedBytes; }
    }
}


/// <summary>
/// Per-connection NDJSON push sink. Implementations must serialize writes under a writer gate.
/// Typed lane methods classify by <see cref="EventClass"/> / <see cref="EventReliability"/>;
/// they must not reparse payload JSON.
/// </summary>
public interface IEventPushSink
{
    string ConnectionId { get; }

    Task WriteLineAsync(string jsonLine, CancellationToken ct = default);

    /// <summary>
    /// Connection NDJSON line cap. Live formatters must fit to this budget.
    /// without panicking.
    /// </summary>
    int MaxLineBytes => AttachSnapshotPacker.MaxNdjsonLineBytes;

    bool UsesWriterLanes => false;

    void DiscardPendingRender()
    {
    }

    /// <summary>True when attach surface admission is required for render lanes.</summary>
    bool RequiresAttachRenderBinding => false;

    /// <summary>
    /// Resolve attach emit binding for one render record. Returns false to drop the render.
    /// </summary>
    bool TryResolveAttachRenderBinding(
        RuntimeEventRecord record,
        out AttachSurfaceEmitBinding? emitBinding)
    {
        emitBinding = null;
        return true;
    }

    WriterLaneResult EnqueueResponse(string jsonLine) =>
        AdmitDefault(jsonLine);

    WriterLaneResult EnqueueReliableEvent(string jsonLine) =>
        AdmitDefault(jsonLine);

    WriterLaneResult TryEnqueueRender(string jsonLine) =>
        AdmitDefault(jsonLine);

    WriterLaneResult EnqueueOrderedRender(string jsonLine) =>
        AdmitDefault(jsonLine);

    WriterLaneResult EnqueueOrderedRenderBatch(IReadOnlyList<string> jsonLines)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        for (var i = 0; i < jsonLines.Count; i++)
        {
            var admitted = EnqueueOrderedRender(jsonLines[i]);
            if (!admitted.IsOk)
                return admitted;
        }

        return WriterLaneResult.Ok;
    }

    WriterLaneResult ReplaceWithCleanup(string jsonLine) =>
        AdmitDefault(jsonLine);

    WriterLaneResult EnqueueResponse(byte[] utf8Json) =>
        EnqueueResponse(Encoding.UTF8.GetString(utf8Json));

    WriterLaneResult EnqueueReliableEvent(byte[] utf8Json) =>
        EnqueueReliableEvent(Encoding.UTF8.GetString(utf8Json));

    WriterLaneResult TryEnqueueRender(byte[] utf8Json) =>
        TryEnqueueRender(Encoding.UTF8.GetString(utf8Json));

    WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) =>
        EnqueueOrderedRender(Encoding.UTF8.GetString(utf8Json));

    WriterLaneResult EnqueueOrderedRenderBatch(IReadOnlyList<byte[]> utf8JsonLines)
    {
        ArgumentNullException.ThrowIfNull(utf8JsonLines);
        var lines = new string[utf8JsonLines.Count];
        for (var i = 0; i < utf8JsonLines.Count; i++)
            lines[i] = Encoding.UTF8.GetString(utf8JsonLines[i]);
        return EnqueueOrderedRenderBatch(lines);
    }

    WriterLaneResult ReplaceWithCleanup(byte[] utf8Json) =>
        ReplaceWithCleanup(Encoding.UTF8.GetString(utf8Json));

    /// <summary>
    /// Admit one NDJSON line, trailing <c>\n</c> included, without a copy.
    /// The default strips the newline and falls back to the
    /// <c>byte[]</c> overload, never the <c>string</c> overload, so test
    /// fakes that override the <c>byte[]</c> overload keep capturing the
    /// same shape.
    /// </summary>
    WriterLaneResult TryEnqueueRender(ReadOnlyMemory<byte> ndjsonLine) =>
        TryEnqueueRender(StripNewline(ndjsonLine));

    WriterLaneResult TryEnqueueRender(
        ReadOnlyMemory<byte> ndjsonLine,
        AttachSurfaceEmitBinding? emitBinding) =>
        TryEnqueueRender(ndjsonLine);

    WriterLaneResult EnqueueReliableEvent(ReadOnlyMemory<byte> ndjsonLine) =>
        EnqueueReliableEvent(StripNewline(ndjsonLine));

    WriterLaneResult EnqueueOrderedRender(ReadOnlyMemory<byte> ndjsonLine) =>
        EnqueueOrderedRender(StripNewline(ndjsonLine));

    WriterLaneResult EnqueueOrderedRender(
        ReadOnlyMemory<byte> ndjsonLine,
        AttachSurfaceEmitBinding? emitBinding) =>
        EnqueueOrderedRender(ndjsonLine);

    WriterLaneResult EnqueueOrderedRenderBatch(IReadOnlyList<ReadOnlyMemory<byte>> ndjsonLines) =>
        EnqueueOrderedRenderBatch(ndjsonLines, emitBinding: null);

    WriterLaneResult EnqueueOrderedRenderBatch(
        IReadOnlyList<ReadOnlyMemory<byte>> ndjsonLines,
        AttachSurfaceEmitBinding? emitBinding)
    {
        ArgumentNullException.ThrowIfNull(ndjsonLines);
        for (var i = 0; i < ndjsonLines.Count; i++)
        {
            var admitted = EnqueueOrderedRender(ndjsonLines[i], emitBinding);
            if (!admitted.IsOk)
                return admitted;
        }

        return WriterLaneResult.Ok;
    }

    private static byte[] StripNewline(ReadOnlyMemory<byte> ndjsonLine)
    {
        var span = ndjsonLine.Span;
        if (span.Length > 0 && span[^1] == (byte)'\n')
            span = span[..^1];
        return span.ToArray();
    }

    private WriterLaneResult AdmitDefault(string jsonLine)
    {
        var task = WriteLineAsync(jsonLine);
        if (task.IsCompletedSuccessfully)
            return WriterLaneResult.Ok;
        if (task.IsFaulted)
            return WriterLaneResult.Closed;
        return WriterLaneResult.Ok;
    }

    void CompleteWriter()
    {
    }
}

/// <summary>One JSON Full generation deferred until the ordered barrier is free.</summary>
internal sealed record DeferredJsonFull(
    IReadOnlyList<ReadOnlyMemory<byte>> Lines,
    string Identity,
    long FeedGeneration,
    AttachSurfaceEmitBinding? EmitBinding = null);
