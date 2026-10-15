using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>
/// expires after 10s. <c>health.rs:39-54</c> expires an outstanding probe
/// or an initial snapshot that never becomes ready. <c>health.rs:30-33</c>
/// <c>received</c> clears the outstanding probe. Any complete line on the
/// destination client is that message. A completed health RPC is one more
/// received line. One exception is not expiry.
/// A poll that comes after a long gap is a wake from sleep. That poll
/// drops the old probe and sends a new one, so a sleep alone does not expire
/// a live connection.
/// Probe ages and timeouts use the monotonic clock, so a wall-clock step does
/// not delay a probe or an expiry. The wall clock only helps to find a wake.
/// The monotonic clock stops during sleep on some hosts and runs on others.
/// A wake is a gap of <see cref="WakeGap"/> or more on either clock. The wall
/// clock finds it when the monotonic clock stopped. The monotonic clock finds
/// it when the wall clock stepped back during sleep.
/// </summary>
internal sealed class AttachEndpointHealthMonitor : IAsyncDisposable
{
    private readonly Func<AttachHealthRequest, CancellationToken, Task<AttachHealthResult>> _health;
    private readonly ControlPlaneClient? _client;
    private readonly AttachEndpointTransportEnvelope _envelope;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _ready;
    private readonly object _gate = new();
    private Task? _loop;
    private Task _probe = Task.CompletedTask;
    private string? _outstandingRequestId;
    private long _outstandingSince;
    private long _lastReceived;
    private long _connectedAt;
    private long _lastPollStamp;
    private DateTimeOffset? _lastPollWall;
    private TimeSpan _outstandingTimeout = AttachEndpointProtocol.HealthTimeout;
    private int _serial;
    private int _disposed;
    private bool _failed;

    public AttachEndpointHealthMonitor(
        AttachEndpointRpcClient client,
        AttachEndpointTransportEnvelope envelope,
        TimeProvider? time = null)
        : this(HealthCall(client), envelope, time, ready: true, client.Client)
    {
    }

    internal AttachEndpointHealthMonitor(
        Func<AttachHealthRequest, CancellationToken, Task<AttachHealthResult>> health,
        AttachEndpointTransportEnvelope envelope,
        TimeProvider? time = null,
        bool ready = true,
        ControlPlaneClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(envelope);
        _health = health;
        _client = client;
        _envelope = envelope;
        _time = time ?? TimeProvider.System;
        _ready = ready;
        _connectedAt = _time.GetTimestamp();
        _lastReceived = _connectedAt;
    }

    /// <summary>A clock gap this long between two polls is a wake from sleep.</summary>
    internal static TimeSpan WakeGap => AttachEndpointProtocol.HealthInterval * 3;

    /// <summary>
    /// The probe after a wake expires on this short timeout. A dead transport
    /// after sleep is then reported without the full health timeout.
    /// </summary>
    internal static readonly TimeSpan WakeProbeTimeout = TimeSpan.FromSeconds(3);

    public event Action? ConnectionGenerationInvalidated;

    internal Task ProbeTask => _probe;

    public void Start()
    {
        _client?.SetLineReceived(Received);
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    // One poll decides
    /// expire, probe, or wait. The probe task does not block the next poll.
    internal void Poll(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return;

        string? requestId = null;
        var expired = false;
        lock (_gate)
        {
            if (_failed)
                return;

            // The transport failure already went to the endpoint loss path.
            if (_client is { IsDisposed: true })
            {
                _failed = true;
                return;
            }

            var now = _time.GetTimestamp();
            var wall = _time.GetUtcNow();
            var wake = false;
            if (_lastPollWall is { } previousWall
                && (wall - previousWall >= WakeGap
                    || _time.GetElapsedTime(_lastPollStamp, now) >= WakeGap))
            {
                // Wake from sleep. The old probe and the snapshot wait do not
                // measure this connection. Probe it again now, on the short wake timeout.
                wake = true;
                _outstandingRequestId = null;
                _connectedAt = now;
                _lastReceived = now - (long)(AttachEndpointProtocol.HealthInterval.TotalSeconds * _time.TimestampFrequency);
            }

            _lastPollWall = wall;
            _lastPollStamp = now;
            var probeExpired = _outstandingRequestId is not null
                && _time.GetElapsedTime(_outstandingSince, now) >= _outstandingTimeout;
            var snapshotExpired = !_ready
                && _time.GetElapsedTime(_connectedAt, now) >= AttachEndpointProtocol.HealthTimeout;
            if (probeExpired || snapshotExpired)
            {
                _outstandingRequestId = null;
                _failed = true;
                expired = true;
            }
            else if (_outstandingRequestId is null
                && _time.GetElapsedTime(_lastReceived, now) >= AttachEndpointProtocol.HealthInterval)
            {
                requestId = $"health-{Interlocked.Increment(ref _serial)}";
                _outstandingRequestId = requestId;
                _outstandingSince = now;
                _outstandingTimeout = wake ? WakeProbeTimeout : AttachEndpointProtocol.HealthTimeout;
            }
        }

        if (expired)
        {
            _envelope.Invalidate();
            ConnectionGenerationInvalidated?.Invoke();
            return;
        }

        if (requestId is not null)
            _probe = ObserveProbeAsync(requestId, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _client?.ClearLineReceived();
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(AttachEndpointProtocol.HealthInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            Poll(ct);
    }

    private async Task ObserveProbeAsync(string requestId, CancellationToken ct)
    {
        try
        {
            _ = await _health(new AttachHealthRequest { RequestId = requestId }, ct)
                .ConfigureAwait(false);
            MarkReceived(requestId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Expiry stays on the 10s poll.
        }
    }

    /// Any complete line on this connection clears the outstanding probe.
    internal void Received()
    {
        lock (_gate)
        {
            _outstandingRequestId = null;
            _lastReceived = _time.GetTimestamp();
        }
    }

    private void MarkReceived(string requestId)
    {
        lock (_gate)
        {
            if (!string.Equals(_outstandingRequestId, requestId, StringComparison.Ordinal))
                return;
            _outstandingRequestId = null;
            _lastReceived = _time.GetTimestamp();
        }
    }

    private static Func<AttachHealthRequest, CancellationToken, Task<AttachHealthResult>> HealthCall(
        AttachEndpointRpcClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return (request, ct) => client.HealthAsync(request, ct);
    }
}
