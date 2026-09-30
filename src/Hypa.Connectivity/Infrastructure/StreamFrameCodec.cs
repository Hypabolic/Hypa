using System.Buffers.Binary;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Length-prefixed framed TCP codec. Equal to WebSocket text plus binary.
/// Control is NDJSON. Binary is terminal input, output, and resize.
/// </summary>
public static class StreamFrameCodec
{
    public static byte[] Encode(StreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var payload = frame.Payload.Span;
        var buffer = new byte[StreamLimits.HeaderBytes + payload.Length];
        WriteHeader(buffer.AsSpan(0, StreamLimits.HeaderBytes), frame);
        payload.CopyTo(buffer.AsSpan(StreamLimits.HeaderBytes));
        return buffer;
    }

    public static void WriteHeader(Span<byte> dest, StreamFrame frame)
    {
        if (dest.Length < StreamLimits.HeaderBytes)
            throw new ArgumentException("header buffer is too small", nameof(dest));

        dest[0] = StreamLimits.FrameVersion;
        dest[1] = (byte)frame.Kind;
        dest[2] = (byte)frame.Direction;
        dest[3] = StreamLimits.ReservedByte;
        BinaryPrimitives.WriteUInt32BigEndian(dest[4..], frame.ChannelId);
        BinaryPrimitives.WriteUInt64BigEndian(dest[8..], frame.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(dest[16..], (uint)frame.PayloadLength);
    }

    public static ConnectivityOutcome<StreamFrameHeader> ReadHeader(
        ReadOnlySpan<byte> source,
        StreamBudget? budget = null)
    {
        budget ??= StreamBudget.Default;
        if (source.Length < StreamLimits.HeaderBytes)
        {
            return ConnectivityOutcome<StreamFrameHeader>.Failure(
                ConnectivityReasons.StreamReset,
                "frame header is incomplete");
        }

        if (source[0] != StreamLimits.FrameVersion)
        {
            return ConnectivityOutcome<StreamFrameHeader>.Failure(
                ConnectivityReasons.StreamReset,
                "frame version is unsupported");
        }

        if (source[3] != StreamLimits.ReservedByte)
        {
            return ConnectivityOutcome<StreamFrameHeader>.Failure(
                ConnectivityReasons.StreamReset,
                "frame reserved byte must be 0");
        }

        var kind = (StreamFrameKind)source[1];
        var direction = (StreamDirection)source[2];
        var channelId = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(source[8..]);
        var length = BinaryPrimitives.ReadUInt32BigEndian(source[16..]);
        if (length > int.MaxValue)
        {
            return ConnectivityOutcome<StreamFrameHeader>.Failure(
                ConnectivityReasons.StreamReset,
                "frame length exceeds bound");
        }

        var header = new StreamFrameHeader
        {
            Kind = kind,
            ChannelId = channelId,
            Sequence = sequence,
            Direction = direction,
            Length = (int)length,
        };
        var valid = StreamFrameRules.ValidateHeader(header, budget);
        if (!valid.Ok)
        {
            return ConnectivityOutcome<StreamFrameHeader>.Failure(
                valid.Reason ?? ConnectivityReasons.StreamReset,
                valid.Detail ?? "frame header is invalid");
        }

        return ConnectivityOutcome<StreamFrameHeader>.Success(header);
    }

    public static StreamFrame ToFrame(StreamFrameHeader header, ReadOnlyMemory<byte> payload) =>
        new()
        {
            Kind = header.Kind,
            ChannelId = header.ChannelId,
            Sequence = header.Sequence,
            Direction = header.Direction,
            Payload = payload,
        };

    public static async Task WriteAsync(
        Stream stream,
        StreamFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        var encoded = Encode(frame);
        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public static async Task<ConnectivityOutcome<byte[]>> ReadExactAsync(
        Stream stream,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (count < 0)
        {
            return ConnectivityOutcome<byte[]>.Failure(
                ConnectivityReasons.StreamReset,
                "frame length is invalid");
        }

        if (count == 0)
            return ConnectivityOutcome<byte[]>.Success([]);

        var buffer = new byte[count];
        var n = 0;
        while (n < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(n, count - n), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return ConnectivityOutcome<byte[]>.Failure(
                    ConnectivityReasons.PeerUnavailable,
                    n == 0 ? "peer closed" : "frame is incomplete");
            }

            n += read;
        }

        return ConnectivityOutcome<byte[]>.Success(buffer);
    }

    public static async Task<ConnectivityOutcome<StreamFrameHeader>> ReadHeaderAsync(
        Stream stream,
        StreamBudget budget,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadExactAsync(stream, StreamLimits.HeaderBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!bytes.Ok || bytes.Value is null)
        {
            return ConnectivityOutcome<StreamFrameHeader>.Failure(
                bytes.Reason ?? ConnectivityReasons.PeerUnavailable,
                bytes.Detail ?? "frame header is incomplete");
        }

        return ReadHeader(bytes.Value, budget);
    }

    public static async Task<ConnectivityOutcome<StreamFrame>> ReadAsync(
        Stream stream,
        StreamBudget budget,
        CancellationToken cancellationToken)
    {
        var header = await ReadHeaderAsync(stream, budget, cancellationToken).ConfigureAwait(false);
        if (!header.Ok)
        {
            return ConnectivityOutcome<StreamFrame>.Failure(
                header.Reason ?? ConnectivityReasons.StreamReset,
                header.Detail ?? "frame header is invalid");
        }

        var payload = await ReadExactAsync(stream, header.Value.Length, cancellationToken)
            .ConfigureAwait(false);
        if (!payload.Ok || payload.Value is null)
        {
            return ConnectivityOutcome<StreamFrame>.Failure(
                payload.Reason ?? ConnectivityReasons.PeerUnavailable,
                payload.Detail ?? "frame payload is incomplete");
        }

        return ConnectivityOutcome<StreamFrame>.Success(ToFrame(header.Value, payload.Value));
    }
}
