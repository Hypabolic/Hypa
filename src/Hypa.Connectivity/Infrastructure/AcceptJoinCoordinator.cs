using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Matches validated client legs with mux legs from the local Unix bridge.
/// </summary>
public sealed class AcceptJoinCoordinator
{
    private readonly RendezvousRelayIdentity _identity;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, AcceptClientSession> _byNonce = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AcceptClientSession>> _byDevice = new(StringComparer.Ordinal);

    public AcceptJoinCoordinator(
        RendezvousRelayIdentity identity,
        TimeProvider? clock = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _clock = clock ?? TimeProvider.System;
    }

    public AcceptClientSession RegisterClient(JoinBootstrap bootstrap, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(stream);
        var key = JoinSlot.Key(bootstrap.Nonce);
        var session = new AcceptClientSession(bootstrap, stream);
        lock (_gate)
        {
            _byNonce[key] = session;
            if (!_byDevice.TryGetValue(bootstrap.Capability.DeviceId.Value, out var deviceSessions))
            {
                deviceSessions = [];
                _byDevice[bootstrap.Capability.DeviceId.Value] = deviceSessions;
            }

            deviceSessions.Add(session);
        }

        return session;
    }

    public void CompleteSession(AcceptClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        RemoveSession(session);
        session.Dispose();
    }

    public void RevokeAllSessions()
    {
        List<AcceptClientSession> sessions;
        lock (_gate)
        {
            sessions = _byNonce.Values.ToList();
            _byNonce.Clear();
            _byDevice.Clear();
        }

        foreach (var session in sessions)
            session.Dispose();
    }

    public bool TryReserveBridge(AcceptClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (session.IsRevoked || session.BridgeReserved)
                return false;

            session.BridgeReserved = true;
            return true;
        }
    }

    public void ReleaseBridgeReservation(AcceptClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
            session.BridgeReserved = false;
    }

    public void CloseJoinsForDevice(DeviceId deviceId)
    {
        if (string.IsNullOrEmpty(deviceId.Value))
            return;

        List<AcceptClientSession> closing;
        lock (_gate)
        {
            if (!_byDevice.TryGetValue(deviceId.Value, out var deviceSessions))
                return;

            closing = deviceSessions.ToList();
        }

        foreach (var session in closing)
            session.Revoke();
    }

    public ConnectivityOutcome<JoinMatch> CompleteMuxLeg(JoinBootstrap muxBootstrap)
    {
        ArgumentNullException.ThrowIfNull(muxBootstrap);
        var key = JoinSlot.Key(muxBootstrap.Nonce);
        AcceptClientSession? session;
        lock (_gate)
        {
            _byNonce.TryGetValue(key, out session);
        }

        if (session is null || session.IsRevoked)
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                session?.IsRevoked == true
                    ? ConnectivityReasons.JoinDenied
                    : ConnectivityReasons.Internal,
                session?.IsRevoked == true
                    ? "device is revoked"
                    : "accept client leg is missing");
        }

        var now = _clock.GetUtcNow();
        var bound = JoinMatcher.Bind(session.Bootstrap, muxBootstrap, _identity, now);
        if (!bound.Ok || bound.Value is null)
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                bound.Reason ?? ConnectivityReasons.JoinDenied,
                bound.Detail ?? "join denied");
        }

        lock (_gate)
        {
            if (!_byNonce.TryGetValue(key, out var current) || !ReferenceEquals(current, session))
            {
                return ConnectivityOutcome<JoinMatch>.Failure(
                    ConnectivityReasons.Internal,
                    "accept client leg changed");
            }

            if (session.IsRevoked)
            {
                return ConnectivityOutcome<JoinMatch>.Failure(
                    ConnectivityReasons.JoinDenied,
                    "device is revoked");
            }

            session.Match = bound.Value;
        }

        return bound;
    }

    public bool TryTakeMatchedClient(AcceptClientSession session, out MatchedClient matched)
    {
        matched = null!;
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (session.IsRevoked || session.Match is not { } match)
                return false;

            session.IsActive = true;
            matched = new MatchedClient(session.Stream, match);
            return true;
        }
    }

    public sealed class MatchedClient(Stream stream, JoinMatch match)
    {
        public Stream Stream { get; } = stream;
        public JoinMatch Match { get; } = match;
    }

    private void RemoveSession(AcceptClientSession session)
    {
        lock (_gate)
        {
            _byNonce.Remove(JoinSlot.Key(session.Bootstrap.Nonce));
            if (_byDevice.TryGetValue(session.Bootstrap.Capability.DeviceId.Value, out var deviceSessions))
            {
                deviceSessions.Remove(session);
                if (deviceSessions.Count == 0)
                    _byDevice.Remove(session.Bootstrap.Capability.DeviceId.Value);
            }
        }
    }
}
