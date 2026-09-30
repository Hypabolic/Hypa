using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Test capture of bytes the relay forwards. Production uses a no-op sink.
/// </summary>
public interface IRelayForwardSink
{
    void OnForwardedPayload(StreamFrameKind kind, ReadOnlySpan<byte> payload);
}

public sealed class NullRelayForwardSink : IRelayForwardSink
{
    public static NullRelayForwardSink Instance { get; } = new();

    public void OnForwardedPayload(StreamFrameKind kind, ReadOnlySpan<byte> payload)
    {
    }
}
