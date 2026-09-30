using System.Text;
using System.Text.Json;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Attach reconnect handshake on a framed session. Control NDJSON only.
/// </summary>
public static class FramedAttachReconnect
{
    public static async ValueTask<ConnectivityOutcome<AttachReconnectOffer>> RequestAsync(
        IFramedSession session,
        AttachReconnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var json = AttachReconnectCodec.Write(request);
        var sent = await session.SendAsync(
                StreamFrame.Control(
                    StreamDirection.ClientToMux,
                    0,
                    Encoding.UTF8.GetBytes(json)),
                cancellationToken)
            .ConfigureAwait(false);
        if (!sent.Ok)
        {
            return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                sent.Reason ?? ConnectivityReasons.PeerUnavailable,
                sent.Detail ?? "reconnect send failed");
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var received = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!received.Ok || received.Value is null)
                {
                    return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                        received.Reason ?? ConnectivityReasons.PeerUnavailable,
                        received.Detail ?? "reconnect offer missing");
                }

                var frame = received.Value;
                if (frame.Direction == StreamDirection.ClientToMux)
                {
                    return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                        ConnectivityReasons.StreamReset,
                        "terminal input must not replay");
                }

                if (!frame.IsControl)
                    continue;

                var text = Encoding.UTF8.GetString(frame.Payload.Span);
                var offer = AttachReconnectCodec.ReadOffer(text);
                if (offer.Ok && offer.Value is not null)
                {
                    var drained = await DrainReplayAsync(
                            session,
                            offer.Value.ReplayFrames.Count,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!drained.Ok)
                    {
                        return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                            drained.Reason ?? ConnectivityReasons.StreamReset,
                            drained.Detail ?? "replay failed");
                    }

                    return offer;
                }

                var failed = TryReadFailure(text);
                if (failed is not null)
                {
                    return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                        failed.Reason ?? ConnectivityReasons.JoinDenied,
                        failed.Detail ?? "reconnect denied");
                }
            }
        }
        catch (OperationCanceledException)
        {
            return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "reconnect offer missing");
        }
    }

    public static async ValueTask<ConnectivityOutcome> ServeAsync(
        IFramedSession session,
        IAttachReconnect reconnect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reconnect);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var received = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!received.Ok || received.Value is null)
                {
                    return ConnectivityOutcome.Failure(
                        received.Reason ?? ConnectivityReasons.PeerUnavailable,
                        received.Detail ?? "reconnect request missing");
                }

                var frame = received.Value;
                if (!frame.IsControl)
                    continue;

                var text = Encoding.UTF8.GetString(frame.Payload.Span);
                var parsed = AttachReconnectCodec.Read(text);
                if (!parsed.Ok || parsed.Value is null)
                    continue;

                var bound = AttachReconnectRules.ValidateFramedBinding(
                    session.Binding,
                    parsed.Value);
                var offer = bound.Ok
                    ? reconnect.Reconnect(parsed.Value)
                    : ConnectivityOutcome<AttachReconnectOffer>.Failure(
                        bound.Reason ?? ConnectivityReasons.JoinDenied,
                        bound.Detail ?? "reconnect denied");
                if (!offer.Ok || offer.Value is null)
                {
                    var denied = await session.SendAsync(
                            StreamFrame.Control(
                                StreamDirection.MuxToClient,
                                0,
                                Encoding.UTF8.GetBytes(WriteFailure(offer.WithoutValue()))),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return denied.Ok
                        ? ConnectivityOutcome.Failure(
                            offer.Reason ?? ConnectivityReasons.JoinDenied,
                            offer.Detail ?? "reconnect denied")
                        : denied;
                }

                var sent = await session.SendAsync(
                        StreamFrame.Control(
                            StreamDirection.MuxToClient,
                            0,
                            Encoding.UTF8.GetBytes(AttachReconnectCodec.Write(offer.Value))),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!sent.Ok)
                    return sent;

                foreach (var replay in offer.Value.ReplayFrames)
                {
                    if (!AttachReconnectRules.MayReplay(replay))
                    {
                        return ConnectivityOutcome.Failure(
                            ConnectivityReasons.StreamReset,
                            "terminal input must not replay");
                    }

                    var replayed = await session.SendAsync(replay, cancellationToken).ConfigureAwait(false);
                    if (!replayed.Ok)
                        return replayed;
                }

                return ConnectivityOutcome.Success();
            }
        }
        catch (OperationCanceledException)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "reconnect request missing");
        }
    }

    private static async ValueTask<ConnectivityOutcome> DrainReplayAsync(
        IFramedSession session,
        int count,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < count; i++)
        {
            var received = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (!received.Ok || received.Value is null)
            {
                return ConnectivityOutcome.Failure(
                    received.Reason ?? ConnectivityReasons.PeerUnavailable,
                    received.Detail ?? "replay frame missing");
            }

            if (!AttachReconnectRules.MayReplay(received.Value))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.StreamReset,
                    "terminal input must not replay");
            }
        }

        return ConnectivityOutcome.Success();
    }

    private static string WriteFailure(ConnectivityOutcome outcome) =>
        JsonSerializer.Serialize(
            new ConnectivityFailureDocument
            {
                Ok = false,
                Reason = outcome.Reason,
                Detail = outcome.Detail,
            },
            ConnectivityJsonContext.Default.ConnectivityFailureDocument);

    private static ConnectivityFailureDocument? TryReadFailure(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize(
                json,
                ConnectivityJsonContext.Default.ConnectivityFailureDocument);
            if (document is null || document.Ok)
                return null;
            if (string.IsNullOrWhiteSpace(document.Reason)
                && string.IsNullOrWhiteSpace(document.Detail))
            {
                return null;
            }

            return document;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
