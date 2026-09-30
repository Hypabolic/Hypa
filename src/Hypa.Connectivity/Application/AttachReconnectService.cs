using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// In-memory reconnect coordinator. The destination mux stays up across stall.
/// </summary>
public sealed class AttachReconnectService : IAttachReconnect
{
    private readonly object _gate = new();
    private readonly ChannelSequenceTracker _cursors = new();
    private readonly List<ObservedMuxFrame> _observed = [];
    private readonly HashSet<string> _usedNonces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedAttempts = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private AttachReconnectState _state = AttachReconnectState.Detached;
    private AttachAttemptId _attempt;
    private JoinCapability? _capability;
    private string? _leaseId;
    private bool _observeAdmitted;
    private bool _muxRunning;
    private bool _dumpedToShell;
    private bool _serverStopRequested;
    private int _paintCount;
    private int _inputApplied;

    public AttachReconnectService(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public AttachReconnectState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public AttachAttemptId AttemptId
    {
        get
        {
            lock (_gate)
                return _attempt;
        }
    }

    public JoinCapability? Capability
    {
        get
        {
            lock (_gate)
                return _capability;
        }
    }

    public bool MuxRunning
    {
        get
        {
            lock (_gate)
                return _muxRunning;
        }
    }

    public bool DumpedToShell
    {
        get
        {
            lock (_gate)
                return _dumpedToShell;
        }
    }

    public bool ServerStopRequested
    {
        get
        {
            lock (_gate)
                return _serverStopRequested;
        }
    }

    public string? LeaseId
    {
        get
        {
            lock (_gate)
                return _leaseId;
        }
    }

    public int PaintCount
    {
        get
        {
            lock (_gate)
                return _paintCount;
        }
    }

    public int InputAppliedCount
    {
        get
        {
            lock (_gate)
                return _inputApplied;
        }
    }

    public IReadOnlyList<ChannelCursor> LastReceived
    {
        get
        {
            lock (_gate)
                return _cursors.Snapshot();
        }
    }

    public ConnectivityOutcome<AttachAttemptId> Attach(JoinCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (_gate)
        {
            RememberNonce(capability.Nonce.Value);
            _muxRunning = true;
            _serverStopRequested = false;
            _dumpedToShell = false;
            _observed.Clear();
            _cursors.Clear();
            _attempt = NextUnusedAttemptUnderLock(commit: true);
            _capability = capability;
            _cursors.Bind(
                _attempt,
                capability.PlacementId.Value,
                capability.DeviceId,
                capability.Role);
            _leaseId = "lease_" + _attempt.Value;
            _observeAdmitted = false;
            _paintCount++;
            _state = AttachReconnectState.Connected;
            return ConnectivityOutcome<AttachAttemptId>.Success(_attempt);
        }
    }

    public AttachAttemptId MintAttemptId()
    {
        lock (_gate)
            return NextUnusedAttemptUnderLock(commit: false);
    }

    public ConnectivityOutcome NoteObserved(StreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (_state != AttachReconnectState.Connected
                && _state != AttachReconnectState.Replaying)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "attach is not connected");
            }

            _cursors.Note(frame);
            if (AttachReconnectRules.MayReplay(frame) && _capability is not null)
            {
                _observed.Add(new ObservedMuxFrame
                {
                    Frame = frame,
                    PlacementId = _capability.PlacementId.Value,
                    DeviceId = _capability.DeviceId.Value,
                    Role = _capability.Role,
                });
            }

            return ConnectivityOutcome.Success();
        }
    }

    public ConnectivityOutcome ApplyInput(StreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (frame.Kind != StreamFrameKind.Binary
                || frame.Direction != StreamDirection.ClientToMux)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "input must be client-to-mux binary");
            }

            if (_state != AttachReconnectState.Connected)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "input requires a connected lease");
            }

            if (string.IsNullOrEmpty(_leaseId))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.Unauthorized,
                    "input lease is required");
            }

            _cursors.Note(frame);
            _inputApplied++;
            return ConnectivityOutcome.Success();
        }
    }

    public ConnectivityOutcome Stall(TimeSpan gap)
    {
        lock (_gate)
        {
            if (_state != AttachReconnectState.Connected)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "stall requires a connected attach");
            }

            if (gap < AttachReconnectLimits.StallWindow)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "stall window is two seconds");
            }

            DropLeaseUnderLock();
            _dumpedToShell = false;
            _serverStopRequested = false;
            _muxRunning = true;
            _state = AttachReconnectState.Stalled;
            return ConnectivityOutcome.Success();
        }
    }

    public ConnectivityOutcome Detach()
    {
        lock (_gate)
        {
            DropLeaseUnderLock();
            _dumpedToShell = true;
            _serverStopRequested = false;
            _muxRunning = true;
            _state = AttachReconnectState.Detached;
            return ConnectivityOutcome.Success();
        }
    }

    public ConnectivityOutcome Disconnect()
    {
        lock (_gate)
        {
            DropLeaseUnderLock();
            _dumpedToShell = true;
            _serverStopRequested = false;
            _muxRunning = true;
            _state = AttachReconnectState.Detached;
            return ConnectivityOutcome.Success();
        }
    }

    public ConnectivityOutcome<AttachReconnectOffer> Reconnect(AttachReconnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_state != AttachReconnectState.Stalled
                && _state != AttachReconnectState.Reconnecting)
            {
                return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                    ConnectivityReasons.StreamReset,
                    "reconnect requires a stall");
            }

            var valid = AttachReconnectRules.ValidateRequest(
                request,
                _attempt,
                _capability,
                _time.GetUtcNow());
            if (!valid.Ok)
            {
                return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                    valid.Reason ?? ConnectivityReasons.JoinDenied,
                    valid.Detail ?? "reconnect denied");
            }

            if (!_usedNonces.Add(request.Capability.Nonce.Value))
            {
                return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                    ConnectivityReasons.JoinDenied,
                    "reconnect requires a fresh capability");
            }

            if (!_usedAttempts.Add(request.AttemptId.Value))
            {
                return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                    ConnectivityReasons.JoinDenied,
                    "reconnect requires a new attach attempt id");
            }

            var snapshot = AttachReconnectRules.RequiresSnapshot(request.LastReceived);
            var replay = snapshot
                ? Array.Empty<StreamFrame>()
                : AttachReconnectRules.SelectReplay(
                    SelectLocalObservedUnderLock(request.Capability),
                    request.LastReceived);
            foreach (var frame in replay)
            {
                if (!AttachReconnectRules.MayReplay(frame))
                {
                    return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                        ConnectivityReasons.StreamReset,
                        "terminal input must not replay");
                }
            }

            _attempt = request.AttemptId;
            _capability = request.Capability;
            _cursors.Bind(
                _attempt,
                request.Capability.PlacementId.Value,
                request.Capability.DeviceId,
                request.Capability.Role);
            _dumpedToShell = false;
            _serverStopRequested = false;
            _muxRunning = true;
            _state = AttachReconnectState.Replaying;
            return ConnectivityOutcome<AttachReconnectOffer>.Success(new AttachReconnectOffer
            {
                AttemptId = _attempt,
                ReplayFrames = replay,
                SnapshotPaint = snapshot,
                InputLeaseRequired = true,
            });
        }
    }

    public ConnectivityOutcome ReclaimLease(string leaseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "input lease is required");
        }

        lock (_gate)
        {
            if (_state is not AttachReconnectState.Replaying
                and not AttachReconnectState.Reconnecting)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "lease reclaim requires reconnect");
            }

            _leaseId = leaseId.Trim();
            _observeAdmitted = false;
            return ConnectivityOutcome.Success();
        }
    }

    public ConnectivityOutcome AdmitWithoutInputLease()
    {
        lock (_gate)
        {
            if (_state is not AttachReconnectState.Replaying
                and not AttachReconnectState.Reconnecting)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "admit without a lease requires reconnect");
            }

            _leaseId = null;
            _observeAdmitted = true;
            return ConnectivityOutcome.Success();
        }
    }

    public ConnectivityOutcome CompleteReplay(bool painted)
    {
        lock (_gate)
        {
            if (_state != AttachReconnectState.Replaying)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "replay is not in progress");
            }

            if (string.IsNullOrEmpty(_leaseId) && !_observeAdmitted)
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.Unauthorized,
                    "input lease is required");
            }

            if (painted)
                _paintCount++;
            _state = AttachReconnectState.Connected;
            _dumpedToShell = false;
            return ConnectivityOutcome.Success();
        }
    }

    private List<StreamFrame> SelectLocalObservedUnderLock(JoinCapability capability)
    {
        var selected = new List<StreamFrame>();
        foreach (var observed in _observed)
        {
            if (!AttachReconnectRules.ObservedBelongsTo(
                    observed.PlacementId,
                    observed.DeviceId,
                    observed.Role,
                    capability))
            {
                continue;
            }

            selected.Add(observed.Frame);
        }

        return selected;
    }

    private void DropLeaseUnderLock()
    {
        _leaseId = null;
        _observeAdmitted = false;
    }

    private sealed record ObservedMuxFrame
    {
        public required StreamFrame Frame { get; init; }
        public required string PlacementId { get; init; }
        public required string DeviceId { get; init; }
        public required JoinRole Role { get; init; }
    }

    private AttachAttemptId NextUnusedAttemptUnderLock(bool commit)
    {
        AttachAttemptId id;
        do
        {
            id = AttachAttemptId.New();
        } while (_usedAttempts.Contains(id.Value));

        if (commit)
            _usedAttempts.Add(id.Value);
        return id;
    }

    private void RememberNonce(string nonce)
    {
        if (!string.IsNullOrEmpty(nonce))
            _usedNonces.Add(nonce);
    }
}
