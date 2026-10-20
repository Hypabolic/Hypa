using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane.Unix;

namespace Hypa.ControlPlane;

/// <summary>
/// The receive rules that an attach gives to its local mux client.
/// Without a policy, the client keeps its first rules: reliable events
/// first, then the ordered render events in arrival order, then the
/// latest-wins render slot.
/// </summary>
internal sealed record AttachReceivePolicy
{
    /// <summary>
    /// The default limit of queued delta bytes for one pane that does not
    /// have the focus. At 200x50, one delta of a busy pane is a few KiB, so
    /// this is some tens of frames.
    /// </summary>
    public const int DefaultUnfocusedPaneDeltaBytes = 128 * 1024;

    /// <summary>
    /// Returns the pane that has the focus now, or null. The client calls it
    /// on the reader thread and on the render thread. It must not block.
    /// </summary>
    public required Func<string?> FocusedPane { get; init; }

    /// <summary>
    /// When the queued chained cells deltas of a pane that does not have the
    /// focus pass this many bytes, the client drops them and asks for one
    /// reanchor of that pane. Zero turns the limit off.
    /// </summary>
    public int UnfocusedPaneDeltaBytes { get; init; } = DefaultUnfocusedPaneDeltaBytes;

    /// <summary>
    /// Runs on the read loop when the queue records a new reanchor request.
    /// The render loop may wait for an event that will not come, so this
    /// wake starts the request. It must not block and must not take the
    /// queue lock: move the work to another task.
    /// </summary>
    public Action? ReanchorRequested { get; init; }
}

/// <summary>Result of one admit into <see cref="ClientEventQueue"/>.</summary>
internal enum ClientEventAdmit
{
    Queued,

    /// <summary>The event replaced an earlier latest-wins render that no one read.</summary>
    ReplacedRender,

    /// <summary>The policy dropped the event. It is not an error.</summary>
    Dropped,

    /// <summary>The policy dropped the deltas of a pane and recorded a new reanchor request.</summary>
    DroppedForReanchor,

    /// <summary>The ordered lane passed its byte budget. The connection must close.</summary>
    OverBudget,

    /// <summary>The reliable lane is closed.</summary>
    Closed,
}

/// <summary>
/// One received event line. The read loop keeps the NDJSON UTF-8 in a buffer
/// from a pool, with the fields that select its queue, until a consumer takes
/// it. The queue that holds an event owns its buffer.
/// </summary>
internal readonly struct ClientEvent
{
    public readonly RentedUtf8Line? Line;
    public readonly JsonElement Element;
    public readonly ReceiveLineFields Fields;
    public readonly int Bytes;

    /// <summary>Monotonic time at which the read loop had the line.</summary>
    public readonly long ReceivedAt;

    private ClientEvent(RentedUtf8Line? line, JsonElement element, ReceiveLineFields fields, int bytes, long receivedAt)
    {
        Line = line;
        Element = element;
        Fields = fields;
        Bytes = bytes;
        ReceivedAt = receivedAt;
    }

    /// <summary>An event that owns <paramref name="line"/>. <paramref name="fields"/> come from the same bytes.</summary>
    public static ClientEvent FromLine(RentedUtf8Line line, ReceiveLineFields fields, int bytes, long receivedAt)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new(line, default, fields, bytes, receivedAt);
    }

    /// <summary>An event that a consumer already parsed. The fields come from its raw bytes.</summary>
    public static ClientEvent FromElement(JsonElement element, int bytes, long receivedAt) =>
        new(null, element, ReceiveLineFields.Read(JsonMarshal.GetRawUtf8Value(element)), bytes, receivedAt);

    /// <summary>
    /// The event as a <see cref="JsonElement"/>. A line is parsed one time,
    /// into an element that owns its own copy, and then its buffer goes back
    /// to the pool. Call this one time for each taken event.
    /// </summary>
    public JsonElement Materialize()
    {
        if (Line is not { } line)
            return Element;
        try
        {
            var reader = new Utf8JsonReader(line.Span);
            return JsonElement.ParseValue(ref reader);
        }
        finally
        {
            line.Return();
        }
    }

    /// <summary>The same event as an element that does not hold a pool buffer.</summary>
    public ClientEvent Detach() =>
        Line is null ? this : new(null, Materialize(), Fields, Bytes, ReceivedAt);

    /// <summary>Gives the buffer back to the pool when nobody will take the event.</summary>
    public void Release() => Line?.Return();
}

/// <summary>
/// The receive queues of one <see cref="ControlPlaneClient"/>. The read loop
/// admits each event line. The attach render loop takes them.
/// <para>
/// With an <see cref="AttachReceivePolicy"/>, three more rules apply:
/// </para>
/// <list type="number">
/// <item>Only the latest <c>pane.scroll_changed</c> of each pane stays in the
/// queue. A newer one removes the older one and goes to the tail, so it never
/// moves ahead of an event that arrived before it.</item>
/// <item>The next render event of the focused pane goes first when no barrier
/// arrived before it. A barrier is a reliable event that is not a state event
/// of another pane (see <see cref="PaneStateEventTypes"/>), or a render event
/// that has no pane. Each pane has its own generation chain, so the frames of
/// one pane never change order. A frame of another pane, or the scroll,
/// status or metadata of another pane, cannot change the cells, the size or
/// the place of the focused pane. A layout, lifecycle or placement event can,
/// so it stays a barrier. An event that arrived after the focused frame was
/// sent after it by the mux, so to paint the frame first is the order of the
/// mux.</item>
/// <item>When the queued chained deltas of a pane without the focus pass the
/// limit, the queue drops the cells events of that pane, records one
/// reanchor request, and drops its later deltas until a full or reanchor
/// frame of that pane arrives. A full frame of such a pane also removes the
/// earlier queued cells events of that pane, because it replaces them.</item>
/// </list>
/// </summary>
internal sealed class ClientEventQueue
{
    /// <summary>
    /// Reliable events that only change the state of one pane: its scroll
    /// offset, its status, its metadata, a match or a bell. The focused frame
    /// may pass such an event of another pane.
    /// </summary>
    internal static readonly FrozenSet<string> PaneStateEventTypes = new[]
    {
        ProtocolEventTypes.PaneScrollChanged,
        ProtocolEventTypes.PaneAgentStatusChanged,
        ProtocolEventTypes.PaneProgramStatusChanged,
        ProtocolEventTypes.PaneProgramAttention,
        ProtocolEventTypes.PaneMetadataUpdated,
        ProtocolEventTypes.PaneOutputMatched,
        ProtocolEventTypes.PaneBell,
    }.ToFrozenSet(StringComparer.Ordinal);

    private enum RenderShape : byte
    {
        Other,
        CellsDelta,
        CellsBaseline,
    }

    private sealed class Entry
    {
        public ClientEvent Event;
        public long Arrival;
        public string? PaneId;
        public bool IsScroll;
        public bool IsPaneState;
        public bool Classified;
        public RenderShape Shape;
        public LinkedListNode<Entry>? QueueNode;
        public LinkedListNode<Entry>? PaneNode;
        public PaneLane? Lane;
    }

    private sealed class PaneLane
    {
        public readonly LinkedList<Entry> Entries = new();
        public long DeltaBytes;
        public bool AwaitingBaseline;
    }

    private readonly object _gate = new();
    private readonly int _maxOrderedBytes;
    private readonly LinkedList<Entry> _reliable = new();
    private readonly LinkedList<Entry> _ordered = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _scrollByPane = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaneLane> _lanes = new(StringComparer.Ordinal);
    private readonly PaneLane _paneless = new();
    private readonly List<string> _reanchorPanes = [];
    private Entry? _pendingRender;
    private long _orderedBytes;
    private long _arrival;
    private bool _reliableClosed;
    private bool _released;
    private AttachReceivePolicy? _policy;
    private long _droppedDeltas;
    private long _coalescedScrolls;

    public ClientEventQueue(int maxOrderedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOrderedBytes, 1);
        _maxOrderedBytes = maxOrderedBytes;
    }

    public AttachReceivePolicy? Policy => Volatile.Read(ref _policy);

    /// <summary>
    /// Sets the rules. Events that are already queued get the new rules too:
    /// their panes and kinds are read again, the pane lanes are built again
    /// in arrival order, and older scroll events of one pane collapse.
    /// </summary>
    public void SetPolicy(AttachReceivePolicy? policy)
    {
        var reanchor = false;
        lock (_gate)
        {
            Volatile.Write(ref _policy, policy);
            if (policy is null)
                return;

            foreach (var lane in _lanes.Values)
            {
                lane.Entries.Clear();
                lane.DeltaBytes = 0;
            }

            _paneless.Entries.Clear();
            foreach (var entry in _ordered)
            {
                if (!entry.Classified)
                    ClassifyRender(entry);
                entry.Lane = null;
                entry.PaneNode = null;
                AddToLane(entry, LaneFor(entry, policy));
            }

            // Events queued before the policy get the delta limit too.
            if (policy.UnfocusedPaneDeltaBytes > 0)
            {
                foreach (var (paneId, lane) in _lanes)
                {
                    if (lane.DeltaBytes <= policy.UnfocusedPaneDeltaBytes || IsFocused(policy, paneId))
                        continue;
                    RemoveCells(lane, before: null);
                    lane.AwaitingBaseline = true;
                    if (_reanchorPanes.Contains(paneId, StringComparer.Ordinal))
                        continue;
                    _reanchorPanes.Add(paneId);
                    reanchor = true;
                }
            }

            _scrollByPane.Clear();
            var node = _reliable.First;
            while (node is not null)
            {
                var next = node.Next;
                if (!node.Value.Classified)
                    ClassifyReliable(node.Value);
                if (node.Value.IsScroll && node.Value.PaneId is { } paneId)
                {
                    if (_scrollByPane.TryGetValue(paneId, out var older))
                    {
                        _reliable.Remove(older);
                        older.Value.Event.Release();
                        Interlocked.Increment(ref _coalescedScrolls);
                    }

                    _scrollByPane[paneId] = node;
                }

                node = next;
            }
        }

        // Off the queue lock, as the admit path does.
        if (reanchor)
            policy.ReanchorRequested?.Invoke();
    }

    public int OrderedCount
    {
        get { lock (_gate) return _ordered.Count; }
    }

    public long OrderedBytes
    {
        get { lock (_gate) return _orderedBytes; }
    }

    public int ReliableCount
    {
        get { lock (_gate) return _reliable.Count; }
    }

    /// <summary>True when the latest-wins render slot holds an event.</summary>
    public bool HasPendingRender
    {
        get { lock (_gate) return _pendingRender is not null; }
    }

    public long DroppedDeltas => Interlocked.Read(ref _droppedDeltas);

    public long CoalescedScrolls => Interlocked.Read(ref _coalescedScrolls);

    /// <summary>Queued chained delta bytes of one pane.</summary>
    public long PaneDeltaBytes(string paneId)
    {
        lock (_gate)
            return _lanes.TryGetValue(paneId, out var lane) ? lane.DeltaBytes : 0;
    }

    public bool HasPaneReanchorRequests
    {
        get { lock (_gate) return _reanchorPanes.Count > 0; }
    }

    /// <summary>Takes the panes whose deltas the queue dropped. Each pane comes once.</summary>
    public IReadOnlyList<string> TakePaneReanchorRequests()
    {
        lock (_gate)
        {
            if (_reanchorPanes.Count == 0)
                return [];
            var panes = _reanchorPanes.ToArray();
            _reanchorPanes.Clear();
            return panes;
        }
    }

    /// <summary>
    /// Stops the drop of the deltas of <paramref name="paneId"/>. The attach
    /// calls it when its reanchor request failed, so no full frame will come.
    /// </summary>
    public void CancelPaneDrop(string paneId)
    {
        lock (_gate)
        {
            if (_lanes.TryGetValue(paneId, out var lane))
                lane.AwaitingBaseline = false;
        }
    }

    /// <summary>
    /// Puts <paramref name="item"/> in the queue that its fields select. The
    /// queue owns the item from this call: an item that the queue does not
    /// keep gives its buffer back to the pool here.
    /// </summary>
    public ClientEventAdmit Admit(ClientEvent item)
    {
        var lane = item.Fields.Lane;
        var policy = Volatile.Read(ref _policy);
        if (lane == WriterLaneNames.Ordered)
            return Released(item, AdmitOrdered(item, policy));
        if (lane == WriterLaneNames.Render)
            return Released(item, AdmitRender(item, policy));
        return Released(item, AdmitReliable(item, policy));
    }

    private static ClientEventAdmit Released(ClientEvent item, ClientEventAdmit result)
    {
        if (result is not (ClientEventAdmit.Queued or ClientEventAdmit.ReplacedRender))
            item.Release();
        return result;
    }

    private ClientEventAdmit AdmitReliable(ClientEvent item, AttachReceivePolicy? policy)
    {
        var entry = new Entry { Event = item };
        if (policy is not null)
            ClassifyReliable(entry);

        lock (_gate)
        {
            if (_reliableClosed)
                return ClientEventAdmit.Closed;

            // A policy set after the read above still sees this event.
            if (!entry.Classified && Volatile.Read(ref _policy) is not null)
                ClassifyReliable(entry);
            entry.Arrival = ++_arrival;
            var node = _reliable.AddLast(entry);
            entry.QueueNode = node;
            if (entry.IsScroll && entry.PaneId is { } scrollPane)
            {
                if (_scrollByPane.TryGetValue(scrollPane, out var older))
                {
                    _reliable.Remove(older);
                    older.Value.Event.Release();
                    Interlocked.Increment(ref _coalescedScrolls);
                }

                _scrollByPane[scrollPane] = node;
            }

            return ClientEventAdmit.Queued;
        }
    }

    private ClientEventAdmit AdmitRender(ClientEvent item, AttachReceivePolicy? policy)
    {
        var entry = NewRenderEntry(item, policy);
        Entry? replaced;
        lock (_gate)
        {
            if (_released)
                return ClientEventAdmit.Dropped;
            replaced = _pendingRender;
            entry.Arrival = ++_arrival;
            _pendingRender = entry;
        }

        if (replaced is null)
            return ClientEventAdmit.Queued;
        // Nobody will take the replaced event.
        replaced.Event.Release();
        return ClientEventAdmit.ReplacedRender;
    }

    private ClientEventAdmit AdmitOrdered(ClientEvent item, AttachReceivePolicy? policy)
    {
        var entry = NewRenderEntry(item, policy);
        lock (_gate)
        {
            if (_released)
                return ClientEventAdmit.Dropped;
            // A policy set after the read above applies to this event too.
            policy = Volatile.Read(ref _policy);
            if (policy is not null && !entry.Classified)
                ClassifyRender(entry);
            var lane = LaneFor(entry, policy);
            if (lane is not null && lane.AwaitingBaseline)
            {
                if (entry.Shape == RenderShape.CellsDelta)
                {
                    Interlocked.Increment(ref _droppedDeltas);
                    return ClientEventAdmit.Dropped;
                }

                if (entry.Shape == RenderShape.CellsBaseline)
                    lane.AwaitingBaseline = false;
            }

            // The pane rules run before the byte budget, so a drop or a full
            // frame frees the bytes that it replaces first.
            if (policy is not null && lane is not null && entry.PaneId is { } paneId
                && !IsFocused(policy, paneId))
            {
                if (entry.Shape == RenderShape.CellsBaseline)
                {
                    // A full frame replaces every earlier cells frame of the pane.
                    RemoveCells(lane, before: null);
                }
                // One delta on an empty lane is not a backlog. It stays, so a
                // large continuation slice after a reanchor still paints.
                else if (entry.Shape == RenderShape.CellsDelta
                    && policy.UnfocusedPaneDeltaBytes > 0
                    && lane.DeltaBytes > 0
                    && lane.DeltaBytes + item.Bytes > policy.UnfocusedPaneDeltaBytes)
                {
                    RemoveCells(lane, before: null);
                    Interlocked.Increment(ref _droppedDeltas);
                    lane.AwaitingBaseline = true;
                    if (_reanchorPanes.Contains(paneId, StringComparer.Ordinal))
                        return ClientEventAdmit.Dropped;
                    _reanchorPanes.Add(paneId);
                    return ClientEventAdmit.DroppedForReanchor;
                }
            }

            var extra = (long)item.Bytes;
            if (_pendingRender is { } pending)
                extra += pending.Event.Bytes;
            if (_orderedBytes + extra > _maxOrderedBytes)
                return ClientEventAdmit.OverBudget;

            if (_pendingRender is { } promote)
            {
                _pendingRender = null;
                if (policy is not null && !promote.Classified)
                    ClassifyRender(promote);
                EnqueueOrdered(promote, LaneFor(promote, policy));
            }

            entry.Arrival = ++_arrival;
            EnqueueOrdered(entry, lane);
            return ClientEventAdmit.Queued;
        }
    }

    /// <summary>Takes the next event under the rules of the class summary.</summary>
    public bool TryTake(out ClientEvent ev)
    {
        var policy = Volatile.Read(ref _policy);
        lock (_gate)
        {
            if (policy is not null && TryTakeFocusedUnlocked(policy, out ev))
                return true;

            if (_reliable.First is { } reliable)
            {
                RemoveReliable(reliable);
                ev = reliable.Value.Event;
                return true;
            }

            if (_ordered.First is { } ordered)
            {
                RemoveOrdered(ordered.Value);
                ev = ordered.Value.Event;
                return true;
            }

            if (_pendingRender is { } render)
            {
                _pendingRender = null;
                ev = render.Event;
                return true;
            }
        }

        ev = default;
        return false;
    }

    /// <summary>Takes every queued reliable event in order. Render events stay.</summary>
    public List<ClientEvent> DrainReliable()
    {
        lock (_gate)
        {
            var list = new List<ClientEvent>(_reliable.Count);
            foreach (var entry in _reliable)
                list.Add(entry.Event);
            _reliable.Clear();
            _scrollByPane.Clear();
            return list;
        }
    }

    /// <summary>The reliable lane takes no more events. Queued events stay readable.</summary>
    public void CloseReliable()
    {
        lock (_gate)
            _reliableClosed = true;
    }

    /// <summary>
    /// Used when the client is disposed. Drops the render lanes and gives
    /// their buffers back to the pool. Queued reliable events stay readable,
    /// but each one gets its own copy, so its buffer goes back to the pool
    /// too. Later events go back to the pool at once.
    /// </summary>
    public void Release()
    {
        List<ClientEvent> dropped;
        lock (_gate)
        {
            _reliableClosed = true;
            _released = true;
            dropped = new List<ClientEvent>(_ordered.Count + 1);
            foreach (var entry in _ordered)
                dropped.Add(entry.Event);
            if (_pendingRender is { } pending)
                dropped.Add(pending.Event);
            _ordered.Clear();
            _orderedBytes = 0;
            _pendingRender = null;
            _lanes.Clear();
            _paneless.Entries.Clear();
            _reanchorPanes.Clear();
            foreach (var entry in _reliable)
                entry.Event = entry.Event.Detach();
        }

        foreach (var ev in dropped)
            ev.Release();
    }

    private bool TryTakeFocusedUnlocked(AttachReceivePolicy policy, out ClientEvent ev)
    {
        ev = default;
        var focused = policy.FocusedPane();
        if (string.IsNullOrEmpty(focused)
            || !_lanes.TryGetValue(focused, out var lane)
            || lane.Entries.First is not { } first)
        {
            return false;
        }

        var arrival = first.Value.Arrival;
        foreach (var reliable in _reliable)
        {
            if (reliable.Arrival > arrival)
                break;
            var otherPaneState = reliable.IsPaneState
                && !string.Equals(reliable.PaneId, focused, StringComparison.Ordinal);
            if (!otherPaneState)
                return false;
        }

        if (_paneless.Entries.First is { } paneless && paneless.Value.Arrival < arrival)
            return false;

        var entry = first.Value;
        RemoveOrdered(entry);
        ev = entry.Event;
        return true;
    }

    private void EnqueueOrdered(Entry entry, PaneLane? lane)
    {
        entry.QueueNode = _ordered.AddLast(entry);
        _orderedBytes += entry.Event.Bytes;
        AddToLane(entry, lane);
    }

    private static void AddToLane(Entry entry, PaneLane? lane)
    {
        if (lane is null)
            return;
        entry.Lane = lane;
        entry.PaneNode = lane.Entries.AddLast(entry);
        if (entry.Shape == RenderShape.CellsDelta)
            lane.DeltaBytes += entry.Event.Bytes;
    }

    private void RemoveOrdered(Entry entry)
    {
        if (entry.QueueNode is { List: not null } node)
            _ordered.Remove(node);
        entry.QueueNode = null;
        _orderedBytes = Math.Max(0, _orderedBytes - entry.Event.Bytes);
        if (entry.Lane is not { } lane)
            return;
        if (entry.PaneNode is { List: not null } paneNode)
            lane.Entries.Remove(paneNode);
        entry.PaneNode = null;
        if (entry.Shape == RenderShape.CellsDelta)
            lane.DeltaBytes = Math.Max(0, lane.DeltaBytes - entry.Event.Bytes);
    }

    private void RemoveReliable(LinkedListNode<Entry> node)
    {
        _reliable.Remove(node);
        if (node.Value.IsScroll
            && node.Value.PaneId is { } paneId
            && _scrollByPane.TryGetValue(paneId, out var latest)
            && ReferenceEquals(latest, node))
        {
            _scrollByPane.Remove(paneId);
        }
    }

    /// <summary>
    /// Removes the queued cells events of <paramref name="lane"/>. With
    /// <paramref name="before"/>, only the ones that arrived before it.
    /// Snapshot slices and other render kinds stay.
    /// </summary>
    private void RemoveCells(PaneLane lane, Entry? before)
    {
        var node = lane.Entries.First;
        while (node is not null)
        {
            var next = node.Next;
            var entry = node.Value;
            if (before is not null && ReferenceEquals(entry, before))
                break;
            if (entry.Shape != RenderShape.Other)
            {
                RemoveOrdered(entry);
                entry.Event.Release();
                if (entry.Shape == RenderShape.CellsDelta)
                    Interlocked.Increment(ref _droppedDeltas);
            }

            node = next;
        }
    }

    private PaneLane? LaneFor(Entry entry, AttachReceivePolicy? policy)
    {
        if (policy is null)
            return null;
        if (entry.PaneId is not { Length: > 0 } paneId)
            return _paneless;
        if (!_lanes.TryGetValue(paneId, out var lane))
        {
            lane = new PaneLane();
            _lanes[paneId] = lane;
        }

        return lane;
    }

    private static bool IsFocused(AttachReceivePolicy policy, string paneId) =>
        string.Equals(policy.FocusedPane(), paneId, StringComparison.Ordinal);

    private static Entry NewRenderEntry(ClientEvent item, AttachReceivePolicy? policy)
    {
        var entry = new Entry { Event = item };
        if (policy is not null)
            ClassifyRender(entry);
        return entry;
    }

    /// <summary>The pane and the cells shape of a render event, from the fields of its line.</summary>
    private static void ClassifyRender(Entry entry)
    {
        entry.Classified = true;
        var fields = entry.Event.Fields;
        if (!fields.HasPayload)
            return;

        entry.PaneId = fields.PaneId;
        if (fields.Kind == ReceivePayloadKind.Cells)
        {
            entry.Shape = fields.Full || fields.Reanchor
                ? RenderShape.CellsBaseline
                : fields.Chained ? RenderShape.CellsDelta : RenderShape.Other;
        }
    }

    /// <summary>The pane of a pane state event, from the fields of its line.</summary>
    private static void ClassifyReliable(Entry entry)
    {
        entry.Classified = true;
        var fields = entry.Event.Fields;
        if (!fields.IsPaneState)
            return;

        entry.PaneId = fields.PaneId;
        entry.IsPaneState = true;
        entry.IsScroll = string.Equals(fields.Type, ProtocolEventTypes.PaneScrollChanged, StringComparison.Ordinal);
    }
}
