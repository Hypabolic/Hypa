using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>Shared join admission and established session handling for accept transports.</summary>
internal sealed class AcceptJoinStreamHandler
{
    private readonly RendezvousRelayIdentity _identity;
    private readonly ILocalUnixBridge _unixBridge;
    private readonly AcceptJoinCoordinator _coordinator;
    private readonly Func<JoinBootstrap, ConnectivityOutcome<JoinBootstrap>> _muxBootstrapFactory;
    private readonly IJoinTrustGate? _trustGate;
    private readonly IHostInviteRedeemer? _inviteRedeemer;
    private readonly TimeProvider _clock;
    private readonly Action<DeviceRecord>? _onInviteRedeemed;

    public AcceptJoinStreamHandler(
        ILocalUnixBridge unixBridge,
        AcceptJoinCoordinator coordinator,
        Func<JoinBootstrap, ConnectivityOutcome<JoinBootstrap>> muxBootstrapFactory,
        IJoinTrustGate? trustGate,
        TimeProvider clock,
        Action<DeviceRecord>? onInviteRedeemed = null)
    {
        ArgumentNullException.ThrowIfNull(unixBridge);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(muxBootstrapFactory);
        ArgumentNullException.ThrowIfNull(clock);
        _unixBridge = unixBridge;
        _coordinator = coordinator;
        _muxBootstrapFactory = muxBootstrapFactory;
        _trustGate = trustGate;
        _inviteRedeemer = trustGate as IHostInviteRedeemer;
        _clock = clock;
        _onInviteRedeemed = onInviteRedeemed;
        _identity = RendezvousRelayIdentity.SelfHostedV0;
    }

    public async Task<AcceptClientSession?> AdmitClientAsync(
        Stream stream,
        CancellationToken handshakeToken)
    {
        string lineJson;
        try
        {
            var line = await BoundedJoinLine.ReadAsync(stream, handshakeToken).ConfigureAwait(false);
            if (!line.Ok || line.Value is null)
            {
                await WriteFailureAsync(stream, line.WithoutValue(), handshakeToken).ConfigureAwait(false);
                return null;
            }

            lineJson = line.Value;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        if (HostInviteCodec.LooksLikeRevoke(lineJson))
        {
            await RevokeEnrolledAsync(stream, lineJson, handshakeToken).ConfigureAwait(false);
            return null;
        }

        if (HostInviteCodec.LooksLikeRedeem(lineJson))
        {
            await RedeemInviteAsync(stream, lineJson, handshakeToken).ConfigureAwait(false);
            return null;
        }

        var parsed = JoinBootstrapCodec.Read(lineJson);
        if (!parsed.Ok || parsed.Value is null)
        {
            await WriteFailureAsync(stream, parsed.WithoutValue(), handshakeToken).ConfigureAwait(false);
            return null;
        }

        var bootstrap = parsed.Value;
        if (bootstrap.Role != JoinRole.Client)
        {
            var denied = ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "accept helper expects client role");
            await WriteFailureAsync(stream, denied, handshakeToken).ConfigureAwait(false);
            return null;
        }

        var now = _clock.GetUtcNow();
        var admitted = JoinMatcher.Admit(
            bootstrap,
            _identity,
            now,
            enforceSelfHostedLocalOperator: _trustGate is null,
            enforceCapabilityExpiry: _trustGate is null);
        if (!admitted.Ok)
        {
            await WriteFailureAsync(stream, admitted, handshakeToken).ConfigureAwait(false);
            return null;
        }

        if (_trustGate is not null)
        {
            var trusted = await _trustGate.AdmitAsync(bootstrap, now, handshakeToken)
                .ConfigureAwait(false);
            if (!trusted.Ok)
            {
                await WriteFailureAsync(stream, trusted, handshakeToken).ConfigureAwait(false);
                return null;
            }
        }

        return _coordinator.RegisterClient(bootstrap, stream);
    }

    private async Task RedeemInviteAsync(
        Stream stream,
        string lineJson,
        CancellationToken handshakeToken)
    {
        if (_inviteRedeemer is null)
        {
            await WriteFailureAsync(
                    stream,
                    ConnectivityOutcome.Failure(
                        ConnectivityReasons.InviteInvalid,
                        "invite redeem is not enabled"),
                    handshakeToken)
                .ConfigureAwait(false);
            return;
        }

        var parsed = HostInviteRedeemCodec.Read(lineJson);
        if (!parsed.Ok || parsed.Value is null)
        {
            if (HostInviteRedeemCodec.TryPeekInviteId(lineJson, out var inviteId))
            {
                var recorded = await _inviteRedeemer
                    .RecordFailedRedeemAttemptAsync(inviteId, handshakeToken)
                    .ConfigureAwait(false);
                await WriteFailureAsync(stream, recorded, handshakeToken).ConfigureAwait(false);
                return;
            }

            await WriteFailureAsync(stream, parsed.WithoutValue(), handshakeToken).ConfigureAwait(false);
            return;
        }

        var redeemed = await _inviteRedeemer
            .RedeemAsync(parsed.Value, _clock.GetUtcNow(), handshakeToken)
            .ConfigureAwait(false);
        if (!redeemed.Ok || redeemed.Value is null)
        {
            await WriteFailureAsync(stream, redeemed.WithoutValue(), handshakeToken).ConfigureAwait(false);
            return;
        }

        NotifyInviteRedeemed(redeemed.Value);
        await WriteDocumentAsync(
                stream,
                new JoinResultDocument
                {
                    Ok = true,
                    Detail = redeemed.Value.Id.Value,
                },
                handshakeToken)
            .ConfigureAwait(false);
    }

    private void NotifyInviteRedeemed(DeviceRecord device)
    {
        if (_onInviteRedeemed is null)
            return;
        try
        {
            _onInviteRedeemed(device);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RevokeEnrolledAsync(
        Stream stream,
        string lineJson,
        CancellationToken handshakeToken)
    {
        if (_inviteRedeemer is null)
        {
            await WriteFailureAsync(
                    stream,
                    ConnectivityOutcome.Failure(
                        ConnectivityReasons.InviteInvalid,
                        "invite revoke is not enabled"),
                    handshakeToken)
                .ConfigureAwait(false);
            return;
        }

        var parsed = HostInviteRevokeCodec.Read(lineJson);
        if (!parsed.Ok || parsed.Value is null)
        {
            await WriteFailureAsync(stream, parsed.WithoutValue(), handshakeToken).ConfigureAwait(false);
            return;
        }

        var revoked = await _inviteRedeemer
            .RevokeEnrolledAsync(parsed.Value, handshakeToken)
            .ConfigureAwait(false);
        if (!revoked.Ok)
        {
            await WriteFailureAsync(stream, revoked, handshakeToken).ConfigureAwait(false);
            return;
        }

        await WriteDocumentAsync(
                stream,
                new JoinResultDocument
                {
                    Ok = true,
                    Detail = parsed.Value.DeviceId.Value,
                },
                handshakeToken)
            .ConfigureAwait(false);
    }

    public async Task RunEstablishedSessionAsync(
        AcceptClientSession session,
        CancellationToken shutdownToken)
    {
        var stream = session.Stream;
        var bootstrap = session.Bootstrap;
        using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            shutdownToken,
            session.Token);

        if (_trustGate is not null)
        {
            var stillValid = await _trustGate
                .IsJoinStillValidAsync(bootstrap, _clock.GetUtcNow(), sessionLifetime.Token)
                .ConfigureAwait(false);
            if (!stillValid || session.IsRevoked)
            {
                await WriteFailureAsync(
                        stream,
                        ConnectivityOutcome.Failure(
                            ConnectivityReasons.JoinDenied,
                            "device is revoked"),
                        sessionLifetime.Token)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (session.IsRevoked)
        {
            await WriteFailureAsync(
                    stream,
                    ConnectivityOutcome.Failure(
                        ConnectivityReasons.JoinDenied,
                        "device is revoked"),
                    sessionLifetime.Token)
                .ConfigureAwait(false);
            return;
        }

        var muxBootstrap = _muxBootstrapFactory(bootstrap);
        if (!muxBootstrap.Ok || muxBootstrap.Value is null)
        {
            await WriteFailureAsync(stream, muxBootstrap.WithoutValue(), sessionLifetime.Token)
                .ConfigureAwait(false);
            return;
        }

        using var muxKeys = JoinEphemeralKeyPair.Create();
        JoinBootstrap stampedMux;
        if (muxBootstrap.Value.Capability.Secret.IsEmpty)
        {
            stampedMux = muxBootstrap.Value with { EphPublicKey = muxKeys.PublicKey };
        }
        else
        {
            var stamped = JoinEphAuthenticator.Stamp(muxBootstrap.Value, muxKeys);
            if (!stamped.Ok || stamped.Value is null)
            {
                await WriteFailureAsync(stream, stamped.WithoutValue(), sessionLifetime.Token)
                    .ConfigureAwait(false);
                return;
            }

            stampedMux = stamped.Value;
        }

        if (_trustGate is not null)
        {
            var stillValid = await _trustGate
                .IsJoinStillValidAsync(bootstrap, _clock.GetUtcNow(), sessionLifetime.Token)
                .ConfigureAwait(false);
            if (!stillValid || session.IsRevoked)
            {
                await WriteFailureAsync(
                        stream,
                        ConnectivityOutcome.Failure(
                            ConnectivityReasons.JoinDenied,
                            "device is revoked"),
                        sessionLifetime.Token)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (!_coordinator.TryReserveBridge(session))
        {
            await WriteFailureAsync(
                    stream,
                    ConnectivityOutcome.Failure(
                        ConnectivityReasons.JoinDenied,
                        "device is revoked"),
                    sessionLifetime.Token)
                .ConfigureAwait(false);
            return;
        }

        MuxBridgeReservation? reservation = null;
        var bridgeTaken = false;
        try
        {
            var bound = await _unixBridge.BindMuxAsync(stampedMux, sessionLifetime.Token)
                .ConfigureAwait(false);
            if (!bound.Ok || bound.Value is null || session.IsRevoked)
            {
                if (bound.Value?.Reservation is { } failedReservation)
                    _unixBridge.ReleaseMuxReservation(failedReservation);
                await WriteFailureAsync(
                        stream,
                        bound.Ok && session.IsRevoked
                            ? ConnectivityOutcome.Failure(
                                ConnectivityReasons.JoinDenied,
                                "device is revoked")
                            : bound.WithoutValue(),
                        sessionLifetime.Token)
                    .ConfigureAwait(false);
                return;
            }

            reservation = bound.Value.Reservation;

            if (!_coordinator.TryTakeMatchedClient(session, out var matched))
            {
                _unixBridge.ReleaseMuxReservation(reservation);
                reservation = null;
                await WriteFailureAsync(
                        stream,
                        ConnectivityOutcome.Failure(
                            ConnectivityReasons.Internal,
                            "accept match is missing"),
                        sessionLifetime.Token)
                    .ConfigureAwait(false);
                return;
            }

            var muxStream = _unixBridge.TryTakeMuxStream(reservation);
            reservation = null;
            if (muxStream is null)
            {
                await WriteFailureAsync(
                        stream,
                        ConnectivityOutcome.Failure(
                            ConnectivityReasons.Internal,
                            "mux stream is missing"),
                        sessionLifetime.Token)
                    .ConfigureAwait(false);
                return;
            }

            bridgeTaken = true;
            await using (muxStream)
            {
                var clientBinding = matched.Match.Second.Role == JoinRole.Client
                    ? matched.Match.Second
                    : matched.Match.First;
                var muxBinding = matched.Match.First.Role == JoinRole.Mux
                    ? matched.Match.First
                    : matched.Match.Second;

                var shared = muxKeys.DeriveSharedSecret(bootstrap.EphPublicKey);
                if (!shared.Ok || shared.Value is null)
                {
                    await WriteFailureAsync(stream, shared.WithoutValue(), sessionLifetime.Token)
                        .ConfigureAwait(false);
                    return;
                }

                var cipher = ApplicationFrameCipher.Create(
                    shared.Value,
                    stampedMux.Nonce,
                    stampedMux.PlacementId.Value,
                    JoinRole.Mux);
                if (!cipher.Ok || cipher.Value is null)
                {
                    await WriteFailureAsync(stream, cipher.WithoutValue(), sessionLifetime.Token)
                        .ConfigureAwait(false);
                    return;
                }

                await WriteJoinAsync(
                        stream,
                        ConnectivityOutcome<JoinBinding>.Success(clientBinding),
                        sessionLifetime.Token)
                    .ConfigureAwait(false);

                await using var framed = OutboundFramedSession.FromAccepted(
                    matched.Stream,
                    muxBinding,
                    cipher.Value);
                using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(sessionLifetime.Token);
                var watchTask = WatchJoinTrustAsync(bootstrap, session, watchCts);
                try
                {
                    await AcceptControlNdjsonPump.RunAsync(framed, muxStream, watchCts.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (!watchCts.IsCancellationRequested)
                        watchCts.Cancel();
                    try
                    {
                        await watchTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
        }
        finally
        {
            if (!bridgeTaken && reservation is not null)
                _unixBridge.ReleaseMuxReservation(reservation);
            _coordinator.ReleaseBridgeReservation(session);
        }
    }

    private async Task WatchJoinTrustAsync(
        JoinBootstrap bootstrap,
        AcceptClientSession session,
        CancellationTokenSource lifetime)
    {
        if (_trustGate is null)
            return;

        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token).ConfigureAwait(false);
                var valid = await _trustGate
                    .IsEstablishedJoinStillValidAsync(bootstrap, _clock.GetUtcNow(), lifetime.Token)
                    .ConfigureAwait(false);
                if (valid && !session.IsRevoked)
                    continue;
                session.Revoke();
                if (!lifetime.IsCancellationRequested)
                    lifetime.Cancel();
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WriteJoinAsync(
        Stream stream,
        ConnectivityOutcome<JoinBinding> outcome,
        CancellationToken cancellationToken)
    {
        JoinResultDocument document;
        if (outcome.Ok && outcome.Value is not null)
        {
            document = new JoinResultDocument
            {
                Ok = true,
                PlacementId = outcome.Value.PlacementId.Value,
                StreamClass = outcome.Value.StreamClass,
                Role = outcome.Value.Role,
                PeerEphPublicKey = outcome.Value.PeerEphPublicKey,
                PeerEphPublicMac = outcome.Value.PeerEphPublicMac,
            };
        }
        else
        {
            document = new JoinResultDocument
            {
                Ok = false,
                Reason = outcome.Reason ?? ConnectivityReasons.JoinDenied,
                Detail = outcome.Detail,
                Stage = outcome.Stage,
                AttemptId = outcome.AttemptId,
                Retryable = outcome.Retryable,
            };
        }

        await WriteDocumentAsync(stream, document, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteFailureAsync(
        Stream stream,
        ConnectivityOutcome outcome,
        CancellationToken cancellationToken)
    {
        var document = new JoinResultDocument
        {
            Ok = false,
            Reason = outcome.Reason ?? ConnectivityReasons.JoinDenied,
            Detail = outcome.Detail,
            Stage = outcome.Stage,
            AttemptId = outcome.AttemptId,
            Retryable = outcome.Retryable,
        };
        await WriteDocumentAsync(stream, document, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDocumentAsync(
        Stream stream,
        JoinResultDocument document,
        CancellationToken cancellationToken)
    {
        try
        {
            await BoundedJoinLine.WriteAsync(stream, JoinBootstrapCodec.WriteResult(document), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
