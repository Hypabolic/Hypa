namespace Hypa.Connectivity.Domain;

/// <summary>Last received sequence per channel. Reconnect reports this set.</summary>
public sealed class ChannelSequenceTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, ulong> _last = [];
    private AttachAttemptId _attempt;
    private string _placementId = "";
    private DeviceId _deviceId;
    private JoinRole? _role;
    private bool _bound;

    public void Bind(
        AttachAttemptId attempt,
        string placementId,
        DeviceId deviceId,
        JoinRole role)
    {
        ArgumentException.ThrowIfNullOrEmpty(placementId);
        lock (_gate)
        {
            _attempt = attempt;
            _placementId = placementId;
            _deviceId = deviceId;
            _role = role;
            _bound = true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _last.Clear();
            _attempt = default;
            _placementId = "";
            _deviceId = default;
            _role = null;
            _bound = false;
        }
    }

    public void Note(StreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Kind is StreamFrameKind.Heartbeat or StreamFrameKind.FlowControl)
            return;

        lock (_gate)
        {
            if (_last.TryGetValue(frame.ChannelId, out var current) && frame.Sequence <= current)
                return;
            _last[frame.ChannelId] = frame.Sequence;
        }
    }

    public bool TryGet(uint channelId, out ulong sequence)
    {
        lock (_gate)
            return _last.TryGetValue(channelId, out sequence);
    }

    public IReadOnlyList<ChannelCursor> Snapshot()
    {
        lock (_gate)
        {
            if (_last.Count == 0)
                return [];

            var cursors = new ChannelCursor[_last.Count];
            var i = 0;
            foreach (var pair in _last)
            {
                cursors[i++] = new ChannelCursor
                {
                    ChannelId = pair.Key,
                    LastReceivedSequence = pair.Value,
                    AttemptId = _bound ? _attempt.Value : "",
                    PlacementId = _bound ? _placementId : "",
                    DeviceId = _bound ? _deviceId.Value : "",
                    Role = _bound ? _role : null,
                };
            }

            return cursors;
        }
    }
}
