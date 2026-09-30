namespace Hypa.Connectivity.Domain;

/// <summary>
/// Framed multiplex limits after join. Control max matches local NDJSON 1 MiB.
/// </summary>
public static class StreamLimits
{
    public const byte FrameVersion = 1;
    public const int HeaderBytes = 20;

    /// <summary>
    /// Same bound as <c>UnixSocketServerOptions.DefaultMaxLineBytes</c> (1 MiB).
    /// </summary>
    public const int MaxControlLineBytes = 1_048_576;

    public const int MaxBinaryPayloadBytes = 1_048_576;
    public const int BinaryHighWaterBytes = 1_048_576;
    public const int BinaryLowWaterBytes = 262_144;
    public const int ControlReservedBytes = 1_048_576;
    public const int MaxControlFrames = 1_024;
    public const int MaxHeartbeatPayloadBytes = 32;
    public const int MaxHeartbeatFrames = 8;
    public const byte ReservedByte = 0;
    public const int MaxFlowControlPayloadBytes = 1;
    public const int ResizePayloadBytes = 4;
    public const int ApplicationAeadNonceBytes = 12;
    public const int ApplicationAeadTagBytes = 16;
    public const int HighWaterTimeoutSeconds = 5;

    public static TimeSpan HighWaterTimeout { get; } = TimeSpan.FromSeconds(HighWaterTimeoutSeconds);
}

/// <summary>Per-session budget. Tests may shrink the queues. Product uses <see cref="Default"/>.</summary>
public sealed record StreamBudget
{
    public int MaxControlLineBytes { get; init; } = StreamLimits.MaxControlLineBytes;
    public int MaxBinaryPayloadBytes { get; init; } = StreamLimits.MaxBinaryPayloadBytes;
    public int BinaryHighWaterBytes { get; init; } = StreamLimits.BinaryHighWaterBytes;
    public int BinaryLowWaterBytes { get; init; } = StreamLimits.BinaryLowWaterBytes;
    public int ControlReservedBytes { get; init; } = StreamLimits.ControlReservedBytes;
    public int MaxControlFrames { get; init; } = StreamLimits.MaxControlFrames;
    public int MaxHeartbeatPayloadBytes { get; init; } = StreamLimits.MaxHeartbeatPayloadBytes;
    public int MaxHeartbeatFrames { get; init; } = StreamLimits.MaxHeartbeatFrames;
    public int MaxFlowControlPayloadBytes { get; init; } = StreamLimits.MaxFlowControlPayloadBytes;
    public TimeSpan HighWaterTimeout { get; init; } = StreamLimits.HighWaterTimeout;

    public static StreamBudget Default { get; } = new();

    public int MaxPayloadBytes(StreamFrameKind kind) =>
        kind switch
        {
            StreamFrameKind.Control => MaxControlLineBytes,
            StreamFrameKind.Binary => MaxBinaryPayloadBytes,
            StreamFrameKind.Heartbeat => MaxHeartbeatPayloadBytes,
            StreamFrameKind.FlowControl => MaxFlowControlPayloadBytes,
            StreamFrameKind.Resize => StreamLimits.ResizePayloadBytes + StreamLimits.ApplicationAeadTagBytes,
            _ => 0,
        };
}
