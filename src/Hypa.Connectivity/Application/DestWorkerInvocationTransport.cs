using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Opaque dest-worker invocation over framed control. Payload is UTF-8.
/// This type does not decode Continuity records. SSH is not this transport.
/// </summary>
public sealed class DestWorkerInvocationTransport
{
    public async ValueTask<ConnectivityOutcome> SendUtf8Async(
        IFramedSession session,
        ReadOnlyMemory<byte> utf8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (utf8.Length == 0)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.StreamReset,
                "dest worker control payload is empty");
        }

        var direction = StreamFrameRules.Outgoing(session.Binding.Role);
        return await session.SendAsync(
                StreamFrame.Control(direction, sequence: 0, utf8),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ConnectivityOutcome<byte[]>> ReceiveUtf8Async(
        IFramedSession session,
        CancellationToken cancellationToken = default)
    {
        var received = await ReceivePayloadAsync(
                session,
                StreamFrameKind.Control,
                "dest worker invocation must use a control frame",
                cancellationToken)
            .ConfigureAwait(false);
        if (!received.Ok)
            return received;
        if (received.Value is null || received.Value.Length == 0)
        {
            return ConnectivityOutcome<byte[]>.Failure(
                ConnectivityReasons.StreamReset,
                "dest worker control payload is empty");
        }

        return received;
    }

    public async ValueTask<ConnectivityOutcome> SendBinaryAsync(
        IFramedSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (payload.Length == 0)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.StreamReset,
                "dest worker pack chunk is empty");
        }

        var direction = StreamFrameRules.Outgoing(session.Binding.Role);
        return await session.SendAsync(
                StreamFrame.Binary(direction, DestPackChannelId, sequence: 0, payload),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ConnectivityOutcome<byte[]>> ReceiveBinaryAsync(
        IFramedSession session,
        CancellationToken cancellationToken = default)
    {
        var received = await ReceivePayloadAsync(
                session,
                StreamFrameKind.Binary,
                "dest worker pack must use a binary frame",
                cancellationToken)
            .ConfigureAwait(false);
        if (!received.Ok)
            return received;
        if (received.Value is null || received.Value.Length == 0)
        {
            return ConnectivityOutcome<byte[]>.Failure(
                ConnectivityReasons.StreamReset,
                "dest worker pack chunk is empty");
        }

        return received;
    }

    public const uint DestPackChannelId = 1;

    private static async ValueTask<ConnectivityOutcome<byte[]>> ReceivePayloadAsync(
        IFramedSession session,
        StreamFrameKind expected,
        string kindDetail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        while (true)
        {
            var received = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (!received.Ok || received.Value is null)
            {
                return ConnectivityOutcome<byte[]>.Failure(
                    received.Reason ?? ConnectivityReasons.PeerUnavailable,
                    received.Detail ?? "dest worker receive failed");
            }

            if (received.Value.IsHeartbeat)
                continue;

            if (received.Value.Kind != expected)
            {
                return ConnectivityOutcome<byte[]>.Failure(
                    ConnectivityReasons.StreamReset,
                    kindDetail);
            }

            return ConnectivityOutcome<byte[]>.Success(received.Value.Payload.ToArray());
        }
    }
}
