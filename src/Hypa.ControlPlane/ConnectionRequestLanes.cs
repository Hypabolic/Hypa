using System.Threading.Channels;
using Hypa.AgentRuntime.Protocol.Envelopes;

namespace Hypa.ControlPlane;

/// <summary>
/// The two request lanes of one connection: the serial dispatch lane and the
/// input lane for <c>pane.send_keys</c> notifications. The read loop is the
/// only caller of <see cref="PostAsync"/>. It gives each item a read sequence
/// and computes the fences of <see cref="SendKeysOrderRules"/> in read order.
/// A slow request on the dispatch lane thus does not hold a key that it does
/// not fence.
/// </summary>
internal sealed class ConnectionRequestLanes
{
    /// <summary>Runs one request on the dispatch lane. Returns false to stop the lane.</summary>
    public delegate Task<bool> RequestRunner(RpcRequest request, long queuedAt, CancellationToken ct);

    /// <summary>Runs one key on the input lane. Faults of the handler stay inside the runner.</summary>
    public delegate Task KeyRunner(RpcRequest request, long queuedAt, CancellationToken ct);

    private const int PaneFencePruneThreshold = 256;

    private readonly Channel<LaneItem> _dispatch = Channel.CreateUnbounded<LaneItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly Channel<LaneItem> _input = Channel.CreateUnbounded<LaneItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly LaneProgress _dispatchDone = new();
    private readonly LaneProgress _inputDone = new();
    private readonly RequestRunner _runRequest;
    private readonly KeyRunner _runKey;
    private readonly CancellationToken _ct;
    private readonly Task _dispatchLoop;
    private readonly Task _inputLoop;

    // Read loop state. Only PostAsync reads or writes these fields.
    private readonly Dictionary<string, long> _paneFenceSeq = new(StringComparer.Ordinal);
    private long _nextSeq;
    private long _lastKeySeq;
    private long _lastAllFenceSeq;
    private long _lastAnyFenceSeq;
    private bool _serialOnly;

    private long _activeSeq;

    /// <param name="serialOnly">
    /// True when every request must go on the dispatch lane, for example for a plugin peer.
    /// </param>
    public ConnectionRequestLanes(
        RequestRunner runRequest,
        KeyRunner runKey,
        CancellationToken ct,
        bool serialOnly = false)
    {
        ArgumentNullException.ThrowIfNull(runRequest);
        ArgumentNullException.ThrowIfNull(runKey);
        _runRequest = runRequest;
        _runKey = runKey;
        _ct = ct;
        _serialOnly = serialOnly;
        _dispatchLoop = RunDispatchLaneAsync();
        _inputLoop = RunInputLaneAsync();
    }

    /// <summary>The dispatch lane loop. It ends after <see cref="Complete"/> and the drain.</summary>
    public Task DispatchCompletion => _dispatchLoop;

    /// <summary>The input lane loop. It ends after <see cref="Complete"/> and the drain.</summary>
    public Task InputCompletion => _inputLoop;

    /// <summary>
    /// Puts one request on its lane, in read order. Only the read loop calls this method.
    /// </summary>
    public ValueTask PostAsync(RpcRequest request, long queuedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var seq = ++_nextSeq;

        // A grant token marks the connection as a plugin connection when the
        // dispatch lane runs it. Keys after it keep the serial order.
        if (!string.IsNullOrWhiteSpace(request.GrantToken))
            _serialOnly = true;

        if (!_serialOnly && SendKeysOrderRules.UsesInputLane(request))
        {
            var paneId = SendKeysOrderRules.TryReadPaneId(request.Params);
            var waitDispatch = paneId is null
                ? _lastAnyFenceSeq
                : Math.Max(_lastAllFenceSeq, _paneFenceSeq.GetValueOrDefault(paneId));
            _lastKeySeq = seq;
            return _input.Writer.WriteAsync(new LaneItem(request, queuedAt, seq, waitDispatch), ct);
        }

        var fence = SendKeysOrderRules.FenceOf(request.Method, request.Params);
        long waitInput = 0;
        switch (fence.Scope)
        {
            case KeyFenceScope.All:
                waitInput = _lastKeySeq;
                _lastAllFenceSeq = seq;
                _lastAnyFenceSeq = seq;
                break;
            case KeyFenceScope.Pane:
                waitInput = _lastKeySeq;
                _lastAnyFenceSeq = seq;
                NotePaneFence(fence.PaneId!, seq);
                break;
        }

        return _dispatch.Writer.WriteAsync(new LaneItem(request, queuedAt, seq, waitInput), ct);
    }

    /// <summary>
    /// The active request on the dispatch lane has applied its change. Keys
    /// that wait for it can go before its reply is written. Call this only
    /// from inside the handler of the active request.
    /// </summary>
    public void MarkActiveRequestApplied() =>
        _dispatchDone.Advance(Volatile.Read(ref _activeSeq));

    /// <summary>No more requests come. Both lanes drain and end.</summary>
    public void Complete()
    {
        _dispatch.Writer.TryComplete();
        _input.Writer.TryComplete();
    }

    private void NotePaneFence(string paneId, long seq)
    {
        if (_paneFenceSeq.Count >= PaneFencePruneThreshold)
        {
            var done = _dispatchDone.Completed;
            foreach (var (pane, fenceSeq) in _paneFenceSeq)
            {
                if (fenceSeq <= done)
                    _paneFenceSeq.Remove(pane);
            }
        }

        _paneFenceSeq[paneId] = seq;
    }

    private async Task RunDispatchLaneAsync()
    {
        await Task.Yield();
        try
        {
            await foreach (var item in _dispatch.Reader.ReadAllAsync(_ct).ConfigureAwait(false))
            {
                if (item.WaitSeq > 0)
                    await _inputDone.WaitForAsync(item.WaitSeq, _ct).ConfigureAwait(false);

                Volatile.Write(ref _activeSeq, item.Seq);
                bool keepRunning;
                try
                {
                    keepRunning = await _runRequest(item.Request, item.QueuedAt, _ct).ConfigureAwait(false);
                }
                finally
                {
                    _dispatchDone.Advance(item.Seq);
                }

                if (!keepRunning)
                    return;
            }
        }
        finally
        {
            // Keys must not wait for a lane that has stopped.
            _dispatchDone.Close();
        }
    }

    private async Task RunInputLaneAsync()
    {
        await Task.Yield();
        try
        {
            await foreach (var item in _input.Reader.ReadAllAsync(_ct).ConfigureAwait(false))
            {
                try
                {
                    if (item.WaitSeq > 0)
                        await _dispatchDone.WaitForAsync(item.WaitSeq, _ct).ConfigureAwait(false);
                    await _runKey(item.Request, item.QueuedAt, _ct).ConfigureAwait(false);
                }
                finally
                {
                    _inputDone.Advance(item.Seq);
                }
            }
        }
        finally
        {
            // Fence requests must not wait for a lane that has stopped.
            _inputDone.Close();
        }
    }

    /// <summary>
    /// One item on a lane. <see cref="WaitSeq"/> is the read sequence that
    /// the other lane must finish before this item starts, or 0.
    /// </summary>
    private readonly record struct LaneItem(RpcRequest Request, long QueuedAt, long Seq, long WaitSeq);
}

/// <summary>
/// The highest read sequence that one lane has finished. A lane runs its
/// items in read order, so each lower sequence of that lane is also finished.
/// </summary>
internal sealed class LaneProgress
{
    private readonly object _gate = new();
    private long _completed;
    private TaskCompletionSource? _advanced;

    public long Completed => Interlocked.Read(ref _completed);

    /// <summary>Records that the lane finished each item up to <paramref name="seq"/>.</summary>
    public void Advance(long seq)
    {
        TaskCompletionSource? wake;
        lock (_gate)
        {
            if (seq <= _completed)
                return;
            Interlocked.Exchange(ref _completed, seq);
            wake = _advanced;
            _advanced = null;
        }

        wake?.TrySetResult();
    }

    /// <summary>The lane has stopped. No wait blocks after this call.</summary>
    public void Close() => Advance(long.MaxValue);

    /// <summary>Completes when the lane has finished each item up to <paramref name="seq"/>.</summary>
    public ValueTask WaitForAsync(long seq, CancellationToken ct) =>
        Interlocked.Read(ref _completed) >= seq
            ? ValueTask.CompletedTask
            : WaitSlowAsync(seq, ct);

    private async ValueTask WaitSlowAsync(long seq, CancellationToken ct)
    {
        while (true)
        {
            Task advanced;
            lock (_gate)
            {
                if (_completed >= seq)
                    return;
                _advanced ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                advanced = _advanced.Task;
            }

            await advanced.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
