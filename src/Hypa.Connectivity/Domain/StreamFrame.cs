namespace Hypa.Connectivity.Domain;

/// <summary>Control maps to WebSocket text. Binary maps to WebSocket binary.</summary>
public enum StreamFrameKind : byte
{
    Control = 1,
    Binary = 2,
    Heartbeat = 3,
    FlowControl = 4,
    Resize = 5,
}

/// <summary>Application direction. Relay checks it against the sender role.</summary>
public enum StreamDirection : byte
{
    MuxToClient = 1,
    ClientToMux = 2,
}

/// <summary>Hop-by-hop pause for terminal bytes. Control and heartbeat stay open.</summary>
public enum StreamFlowCommand : byte
{
    Resume = 0,
    Pause = 1,
}

/// <summary>
/// One multiplex frame. Channel id, length, sequence, and direction travel in the header.
/// </summary>
public sealed record StreamFrame
{
    public required StreamFrameKind Kind { get; init; }
    public required uint ChannelId { get; init; }
    public required ulong Sequence { get; init; }
    public required StreamDirection Direction { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }

    public int PayloadLength => Payload.Length;

    public bool CountsAsBinary =>
        Kind is StreamFrameKind.Binary or StreamFrameKind.Resize;

    public bool IsHeartbeat => Kind == StreamFrameKind.Heartbeat;

    public bool IsControl => Kind == StreamFrameKind.Control;

    public bool IsFlowControl => Kind == StreamFrameKind.FlowControl;

    public static StreamFrame Control(
        StreamDirection direction,
        ulong sequence,
        ReadOnlyMemory<byte> payload,
        uint channelId = 0) =>
        new()
        {
            Kind = StreamFrameKind.Control,
            ChannelId = channelId,
            Sequence = sequence,
            Direction = direction,
            Payload = payload,
        };

    public static StreamFrame Binary(
        StreamDirection direction,
        uint channelId,
        ulong sequence,
        ReadOnlyMemory<byte> payload) =>
        new()
        {
            Kind = StreamFrameKind.Binary,
            ChannelId = channelId,
            Sequence = sequence,
            Direction = direction,
            Payload = payload,
        };

    public static StreamFrame Heartbeat(StreamDirection direction, ulong sequence) =>
        new()
        {
            Kind = StreamFrameKind.Heartbeat,
            ChannelId = 0,
            Sequence = sequence,
            Direction = direction,
            Payload = ReadOnlyMemory<byte>.Empty,
        };

    public static StreamFrame FlowControl(
        StreamDirection pausedDirection,
        StreamFlowCommand command,
        ulong sequence) =>
        new()
        {
            Kind = StreamFrameKind.FlowControl,
            ChannelId = 0,
            Sequence = sequence,
            Direction = pausedDirection,
            Payload = new byte[] { (byte)command },
        };

    public static StreamFrame Resize(
        StreamDirection direction,
        uint channelId,
        ulong sequence,
        ushort columns,
        ushort rows)
    {
        var payload = new byte[StreamLimits.ResizePayloadBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(payload, columns);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), rows);
        return new StreamFrame
        {
            Kind = StreamFrameKind.Resize,
            ChannelId = channelId,
            Sequence = sequence,
            Direction = direction,
            Payload = payload,
        };
    }
}

public readonly record struct StreamFrameHeader
{
    public required StreamFrameKind Kind { get; init; }
    public required uint ChannelId { get; init; }
    public required ulong Sequence { get; init; }
    public required StreamDirection Direction { get; init; }
    public required int Length { get; init; }
}

public static class StreamFrameRules
{
    public static bool IsDefined(StreamFrameKind kind) =>
        kind is StreamFrameKind.Control
            or StreamFrameKind.Binary
            or StreamFrameKind.Heartbeat
            or StreamFrameKind.FlowControl
            or StreamFrameKind.Resize;

    public static bool IsDefined(StreamDirection direction) =>
        direction is StreamDirection.MuxToClient or StreamDirection.ClientToMux;

    public static StreamDirection Outgoing(JoinRole role) =>
        role == JoinRole.Mux ? StreamDirection.MuxToClient : StreamDirection.ClientToMux;

    public static StreamDirection Incoming(JoinRole role) =>
        role == JoinRole.Mux ? StreamDirection.ClientToMux : StreamDirection.MuxToClient;

    public static bool DirectionMatchesSender(StreamDirection direction, JoinRole sender) =>
        direction == Outgoing(sender);

    public static ConnectivityOutcome ValidateHeader(StreamFrameHeader header, StreamBudget budget)
    {
        if (!IsDefined(header.Kind))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.StreamReset,
                "frame kind is invalid");
        }

        if (!IsDefined(header.Direction))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.StreamReset,
                "frame direction is invalid");
        }

        if (header.Length < 0)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.StreamReset,
                "frame length is invalid");
        }

        var max = budget.MaxPayloadBytes(header.Kind);
        if (header.Length > max)
        {
            var detail = header.Kind == StreamFrameKind.Control
                ? "control line exceeds bound"
                : "frame length exceeds bound";
            return ConnectivityOutcome.Failure(ConnectivityReasons.StreamReset, detail);
        }

        if (header.Kind is StreamFrameKind.Control
                or StreamFrameKind.Heartbeat
                or StreamFrameKind.FlowControl
            && header.ChannelId != 0)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.StreamReset,
                "control channel id must be 0");
        }

        if (header.Kind == StreamFrameKind.Resize
            && header.Length != StreamLimits.ResizePayloadBytes
            && header.Length != StreamLimits.ResizePayloadBytes + StreamLimits.ApplicationAeadTagBytes)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.StreamReset,
                "resize payload must be 4 bytes or encrypted");
        }

        if (header.Kind == StreamFrameKind.FlowControl
            && header.Length != budget.MaxFlowControlPayloadBytes)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.StreamReset,
                "flow control payload must be 1 byte");
        }

        return ConnectivityOutcome.Success();
    }

    public static ConnectivityOutcome ValidateFrame(StreamFrame frame, StreamBudget budget)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var header = new StreamFrameHeader
        {
            Kind = frame.Kind,
            ChannelId = frame.ChannelId,
            Sequence = frame.Sequence,
            Direction = frame.Direction,
            Length = frame.PayloadLength,
        };
        var headerValid = ValidateHeader(header, budget);
        if (!headerValid.Ok)
            return headerValid;

        if (ApplicationEncryption.Protects(frame.Kind)
            && frame.PayloadLength > ApplicationEncryption.MaxPlaintextBytes(frame.Kind, budget))
        {
            var detail = frame.Kind == StreamFrameKind.Control
                ? "control line exceeds bound"
                : "frame length exceeds bound";
            return ConnectivityOutcome.Failure(ConnectivityReasons.StreamReset, detail);
        }

        if (frame.IsFlowControl && !TryReadFlowCommand(frame, out _))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.StreamReset,
                "flow control payload is invalid");
        }

        return ConnectivityOutcome.Success();
    }

    public static bool TryReadFlowCommand(StreamFrame frame, out StreamFlowCommand command)
    {
        command = StreamFlowCommand.Resume;
        if (!frame.IsFlowControl || frame.Payload.Length != 1)
            return false;

        var raw = frame.Payload.Span[0];
        if (raw == (byte)StreamFlowCommand.Pause)
        {
            command = StreamFlowCommand.Pause;
            return true;
        }

        if (raw == (byte)StreamFlowCommand.Resume)
        {
            command = StreamFlowCommand.Resume;
            return true;
        }

        return false;
    }
}
