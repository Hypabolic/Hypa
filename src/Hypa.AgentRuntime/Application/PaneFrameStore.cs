namespace Hypa.AgentRuntime.Application;

/// <summary>
/// One retained frame set per pane. Subscriptions hold a frame id, not a
// / copy of the grid.
/// one <c>ClientRenderState</c> per connection.
/// connection record. Hypa keeps a per-pane axis because the mux sends
/// pane grids. The store is that axis. It does not duplicate the payload
/// per observer.
/// </summary>
/// <remarks>
/// Lock order, outer to inner:
/// <c>EventSubscriptionHub._gate</c> →
/// <c>EventSubscription._gate</c> →
/// <c>PaneFrameStore._gate</c>.
/// No method on this store may call a subscription method.
/// </remarks>
internal sealed class PaneFrameStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PaneFrameSlot> _panes = new(StringComparer.Ordinal);
    private long _nextFrameId;

    /// <summary>
    /// Add one reference to <paramref name="frame"/> for
    /// <paramref name="paneId"/>. Same instance shares one held record.
    /// </summary>
    internal long Retain(string paneId, VtFrame frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (!_panes.TryGetValue(paneId, out var slot))
            {
                slot = new PaneFrameSlot();
                _panes[paneId] = slot;
            }

            foreach (var (id, held) in slot.Held)
            {
                if (ReferenceEquals(held.Frame, frame))
                {
                    held.RefCount++;
                    return id;
                }
            }

            var frameId = ++_nextFrameId;
            slot.Held[frameId] = new HeldFrame
            {
                Frame = frame,
                Identity = VtFrameIdentity.Token(frame),
                Generation = frame.Generation,
                RefCount = 1,
            };
            return frameId;
        }
    }

    /// <summary>
    /// Drop one reference. The held frame leaves at zero. The slot leaves
    /// when it holds no frame and no payload.
    /// </summary>
    internal void Release(string paneId, long frameId)
    {
        if (string.IsNullOrWhiteSpace(paneId) || frameId == 0)
            return;
        lock (_gate)
        {
            if (!_panes.TryGetValue(paneId, out var slot))
                return;
            if (!slot.Held.TryGetValue(frameId, out var held))
                return;
            held.RefCount--;
            if (held.RefCount <= 0)
                slot.Held.Remove(frameId);
            if (slot.Held.Count == 0 && slot.LastPayloadUtf8 is null)
                _panes.Remove(paneId);
        }
    }

    internal VtFrame? Frame(string paneId, long frameId)
    {
        if (string.IsNullOrWhiteSpace(paneId) || frameId == 0)
            return null;
        lock (_gate)
        {
            if (!_panes.TryGetValue(paneId, out var slot))
                return null;
            return slot.Held.TryGetValue(frameId, out var held) ? held.Frame : null;
        }
    }

    /// <summary>
    /// Last payload admitted for this pane. The array is immutable. The
    /// slot outlives any one subscription.
    /// </summary>
    internal byte[]? LastPayloadUtf8(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return null;
        lock (_gate)
        {
            if (!_panes.TryGetValue(paneId, out var slot))
                return null;
            return slot.LastPayloadUtf8;
        }
    }

    internal void NotePayloadUtf8(string paneId, byte[] utf8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        ArgumentNullException.ThrowIfNull(utf8);
        lock (_gate)
        {
            if (!_panes.TryGetValue(paneId, out var slot))
            {
                slot = new PaneFrameSlot();
                _panes[paneId] = slot;
            }

            slot.LastPayloadUtf8 = utf8;
        }
    }

    internal void DropPane(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
            _panes.Remove(paneId);
    }

    internal (int PaneRecordCount, int RetainedFrameCount) ReadCounts()
    {
        lock (_gate)
        {
            var frames = 0;
            foreach (var slot in _panes.Values)
                frames += slot.Held.Count;
            return (_panes.Count, frames);
        }
    }

    private sealed class PaneFrameSlot
    {
        public Dictionary<long, HeldFrame> Held { get; } = new();
        public byte[]? LastPayloadUtf8 { get; set; }
    }

    private sealed class HeldFrame
    {
        public required VtFrame Frame { get; init; }
        public required string Identity { get; init; }
        public required long Generation { get; init; }
        public int RefCount;
    }
}
