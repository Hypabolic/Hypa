namespace Hypa.Connectivity.Domain;

/// <summary>
/// One direction of relay memory. Terminal uses the binary budget.
/// Control and heartbeat use reserved budgets the terminal cannot take.
/// </summary>
public sealed class DirectionFlowControl
{
    private readonly StreamBudget _budget;
    private readonly object _gate = new();
    private readonly Queue<StreamFrame> _heartbeat = new();
    private readonly Queue<StreamFrame> _control = new();
    private readonly Queue<StreamFrame> _binary = new();
    private long _queuedBinaryBytes;
    private long _inFlightBinaryBytes;
    private long _controlBytes;
    private DateTimeOffset? _highWaterSince;
    private bool _pauseSent;
    private TaskCompletionSource _hasData = NewSignal();
    private TaskCompletionSource _hasBinaryCapacity = NewSignal();

    public DirectionFlowControl(StreamDirection direction, StreamBudget? budget = null)
    {
        Direction = direction;
        _budget = budget ?? StreamBudget.Default;
        _hasBinaryCapacity.TrySetResult();
    }

    public StreamDirection Direction { get; }

    public StreamBudget Budget => _budget;

    public long BinaryBytes
    {
        get
        {
            lock (_gate)
                return OccupiedBinaryUnsafe();
        }
    }

    public long ControlBytes
    {
        get
        {
            lock (_gate)
                return _controlBytes;
        }
    }

    public int HeartbeatCount
    {
        get
        {
            lock (_gate)
                return _heartbeat.Count;
        }
    }

    public int QueuedFrameCount
    {
        get
        {
            lock (_gate)
                return _heartbeat.Count + _control.Count + _binary.Count;
        }
    }

    public bool IsBinaryPaused
    {
        get
        {
            lock (_gate)
                return OccupiedBinaryUnsafe() >= _budget.BinaryHighWaterBytes;
        }
    }

    public bool PauseShouldBeSent
    {
        get
        {
            lock (_gate)
                return OccupiedBinaryUnsafe() >= _budget.BinaryHighWaterBytes && !_pauseSent;
        }
    }

    public bool ResumeShouldBeSent
    {
        get
        {
            lock (_gate)
                return _pauseSent && OccupiedBinaryUnsafe() <= _budget.BinaryLowWaterBytes;
        }
    }

    public long QueuedBytes
    {
        get
        {
            lock (_gate)
                return OccupiedBinaryUnsafe() + _controlBytes + HeartbeatByteCountUnsafe();
        }
    }

    public bool HasHighPriority
    {
        get
        {
            lock (_gate)
                return _heartbeat.Count > 0 || _control.Count > 0;
        }
    }

    public bool TryEnqueue(StreamFrame frame, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (frame.IsHeartbeat)
            {
                if (_heartbeat.Count >= _budget.MaxHeartbeatFrames)
                    return false;
                _heartbeat.Enqueue(frame);
                PulseData();
                return true;
            }

            if (frame.IsControl)
            {
                if (_control.Count >= _budget.MaxControlFrames)
                    return false;
                var add = Math.Max(1, frame.PayloadLength);
                var next = _controlBytes + add;
                if (next > _budget.ControlReservedBytes)
                    return false;
                _control.Enqueue(frame);
                _controlBytes = next;
                PulseData();
                return true;
            }

            if (frame.CountsAsBinary)
            {
                var next = OccupiedBinaryUnsafe() + frame.PayloadLength;
                if (next > _budget.BinaryHighWaterBytes)
                    return false;
                _binary.Enqueue(frame);
                _queuedBinaryBytes += frame.PayloadLength;
                if (OccupiedBinaryUnsafe() >= _budget.BinaryHighWaterBytes)
                {
                    _highWaterSince ??= utcNow;
                    if (_hasBinaryCapacity.Task.IsCompleted)
                        _hasBinaryCapacity = NewSignal();
                }

                PulseData();
                return true;
            }

            return false;
        }
    }

    public bool TryDequeue(out StreamFrame frame)
    {
        lock (_gate)
        {
            if (_heartbeat.TryDequeue(out frame!))
                return true;

            if (_control.TryDequeue(out frame!))
            {
                _controlBytes -= Math.Max(1, frame.PayloadLength);
                if (_controlBytes < 0)
                    _controlBytes = 0;
                return true;
            }

            if (_binary.TryDequeue(out frame!))
            {
                _queuedBinaryBytes -= frame.PayloadLength;
                if (_queuedBinaryBytes < 0)
                    _queuedBinaryBytes = 0;
                _inFlightBinaryBytes += frame.PayloadLength;
                return true;
            }

            frame = null!;
            return false;
        }
    }

    public void ReturnBinary(StreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.CountsAsBinary)
            return;

        lock (_gate)
        {
            _inFlightBinaryBytes -= frame.PayloadLength;
            if (_inFlightBinaryBytes < 0)
                _inFlightBinaryBytes = 0;
            _queuedBinaryBytes += frame.PayloadLength;
            var rest = _binary.ToArray();
            _binary.Clear();
            _binary.Enqueue(frame);
            foreach (var item in rest)
                _binary.Enqueue(item);
            PulseData();
        }
    }

    public void CompleteWrite(StreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.CountsAsBinary)
            return;

        lock (_gate)
        {
            _inFlightBinaryBytes -= frame.PayloadLength;
            if (_inFlightBinaryBytes < 0)
                _inFlightBinaryBytes = 0;
            if (OccupiedBinaryUnsafe() <= _budget.BinaryLowWaterBytes)
                _highWaterSince = null;
            PulseBinaryCapacity();
        }
    }

    public void MarkPauseSent()
    {
        lock (_gate)
            _pauseSent = true;
    }

    public void MarkResumeSent()
    {
        lock (_gate)
            _pauseSent = false;
    }

    public bool IsHighWaterTimeout(DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            return _highWaterSince is DateTimeOffset since
                && utcNow - since >= _budget.HighWaterTimeout;
        }
    }

    public async Task WaitForDataAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task task;
            lock (_gate)
            {
                if (_heartbeat.Count > 0 || _control.Count > 0 || _binary.Count > 0)
                    return;
                if (_hasData.Task.IsCompleted)
                    _hasData = NewSignal();
                task = _hasData.Task;
            }

            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WaitForHighPriorityAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task task;
            lock (_gate)
            {
                if (_heartbeat.Count > 0 || _control.Count > 0)
                    return;
                if (_hasData.Task.IsCompleted)
                    _hasData = NewSignal();
                task = _hasData.Task;
            }

            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public bool HasRoomForBinary(int incomingLength)
    {
        lock (_gate)
            return OccupiedBinaryUnsafe() + incomingLength <= _budget.BinaryHighWaterBytes;
    }

    public Task WaitForBinaryCapacityAsync(CancellationToken cancellationToken) =>
        WaitForBinaryRoomAsync(0, cancellationToken);

    public async Task WaitForBinaryRoomAsync(int incomingLength, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task task;
            lock (_gate)
            {
                if (OccupiedBinaryUnsafe() + incomingLength <= _budget.BinaryHighWaterBytes)
                    return;
                task = _hasBinaryCapacity.Task;
            }

            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private long OccupiedBinaryUnsafe() => _queuedBinaryBytes + _inFlightBinaryBytes;

    private void PulseData() => _hasData.TrySetResult();

    private void PulseBinaryCapacity()
    {
        var previous = _hasBinaryCapacity;
        _hasBinaryCapacity = NewSignal();
        previous.TrySetResult();
    }

    private int HeartbeatByteCountUnsafe()
    {
        var total = 0;
        foreach (var frame in _heartbeat)
            total += frame.PayloadLength;
        return total;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
