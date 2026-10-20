namespace Hypa.Cli.Attach;

/// <summary>The decision of one liveness tick.</summary>
internal enum LocalLinkStep
{
    /// <summary>The link is alive, or a ping is in flight and has time left.</summary>
    Wait,

    /// <summary>The link was quiet for the quiet interval. Send one ping.</summary>
    Ping,

    /// <summary>The ping got no reply and no line came in the timeout. The link is stalled.</summary>
    Expired,
}

/// <summary>
/// The liveness rule of the local mux link. It is the rule of
/// <see cref="AttachEndpointHealthMonitor"/> with local intervals.
/// <list type="bullet">
/// <item>Any complete line that the client receives proves that the link is
/// alive, and clears a ping in flight.</item>
/// <item>A ping goes out only after the link was quiet for
/// <see cref="QuietInterval"/>.</item>
/// <item>The link is stalled only when that ping got no reply and no other
/// line came for <see cref="PingTimeout"/>.</item>
/// <item>A long gap between two ticks is a wake from sleep. The ping in
/// flight is then dropped and a new one goes out.</item>
/// </list>
/// The caller samples the received-line count of the client on each tick.
/// All times come from the injected <see cref="TimeProvider"/>.
/// </summary>
internal sealed class LocalLinkLiveness
{
    public static readonly TimeSpan DefaultQuietInterval = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan DefaultPingTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private long _lines = -1;
    private long _lastReceived;
    private long _lastPoll;
    private DateTimeOffset? _lastPollWall;
    private long _pingSince;
    private int _pingId;
    private int _serial;

    public LocalLinkLiveness(TimeProvider? time = null, TimeSpan? quietInterval = null, TimeSpan? pingTimeout = null)
    {
        _time = time ?? TimeProvider.System;
        QuietInterval = quietInterval ?? DefaultQuietInterval;
        PingTimeout = pingTimeout ?? DefaultPingTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(QuietInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PingTimeout, TimeSpan.Zero);
    }

    public TimeSpan QuietInterval { get; }

    public TimeSpan PingTimeout { get; }

    /// <summary>A gap this long between two ticks is a wake from sleep.</summary>
    public TimeSpan WakeGap => QuietInterval * 3;

    /// <summary>The id of the ping in flight, or zero.</summary>
    public int OutstandingPing
    {
        get { lock (_gate) return _pingId; }
    }

    /// <summary>Starts again for a new client. The first tick counts as a received line.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _lines = -1;
            _pingId = 0;
            _lastPollWall = null;
        }
    }

    /// <summary>
    /// One tick. <paramref name="linesReceived"/> is the received-line count of
    /// the client now. On <see cref="LocalLinkStep.Ping"/>,
    /// <paramref name="pingId"/> names the ping to send.
    /// </summary>
    public LocalLinkStep Poll(long linesReceived, out int pingId)
    {
        pingId = 0;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var wall = _time.GetUtcNow();
            var wake = _lastPollWall is { } previousWall
                && (wall - previousWall >= WakeGap || _time.GetElapsedTime(_lastPoll, now) >= WakeGap);
            _lastPoll = now;
            _lastPollWall = wall;
            if (linesReceived != _lines)
            {
                _lines = linesReceived;
                _lastReceived = now;
                _pingId = 0;
            }

            if (wake)
            {
                // The ping in flight and the quiet time do not measure this
                // link. Probe it again now.
                _pingId = 0;
                _lastReceived = now - (long)(QuietInterval.TotalSeconds * _time.TimestampFrequency);
            }

            if (_pingId != 0)
            {
                return _time.GetElapsedTime(_pingSince, now) >= PingTimeout
                    ? LocalLinkStep.Expired
                    : LocalLinkStep.Wait;
            }

            if (_time.GetElapsedTime(_lastReceived, now) < QuietInterval)
                return LocalLinkStep.Wait;

            _pingId = ++_serial;
            _pingSince = now;
            pingId = _pingId;
            return LocalLinkStep.Ping;
        }
    }

    /// <summary>The reply of ping <paramref name="pingId"/> came.</summary>
    public void Answered(int pingId)
    {
        lock (_gate)
        {
            if (_pingId != pingId)
                return;
            _pingId = 0;
            _lastReceived = _time.GetTimestamp();
        }
    }
}
