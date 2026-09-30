namespace Hypa.Terminal.Pty;

/// <summary>
/// One length-prefixed helper IPC frame (type + payload, without the length prefix).
/// </summary>
public sealed record PtyHostFrame(PtyHostFrameType Type, byte[] Payload)
{
    public static PtyHostFrame Empty(PtyHostFrameType type) => new(type, Array.Empty<byte>());

    public int BodyLength => 1 + Payload.Length;
}
