namespace Hypa.Terminal.Pty;

/// <summary>Server↔server handoff port message kinds (not helper stdio frame codes).</summary>
public enum PtyHandoffMessageKind : byte
{
    Hello = 1,
    Prepare = 2,
    HandleMeta = 3,
    Commit = 4,
    Abort = 5,
}

/// <summary>
/// Immutable control message for the private H2 handoff port.
/// Nonce is always 16 bytes when present.
/// </summary>
public sealed record PtyHandoffMessage
{
    public const int NonceLength = 16;

    public required PtyHandoffMessageKind Kind { get; init; }
    public byte[] Nonce { get; init; } = Array.Empty<byte>();
    public int Generation { get; init; }
    public string RuntimeSessionId { get; init; } = string.Empty;
    public string PaneId { get; init; } = string.Empty;
    public string TargetRuntimeId { get; init; } = string.Empty;
    public int ChildPid { get; init; }
    public ushort Cols { get; init; }
    public ushort Rows { get; init; }
    public int AbortReason { get; init; }
    public string? AbortMessage { get; init; }

    public static PtyHandoffMessage CreateHello(
        string runtimeSessionId,
        string paneId,
        int generation,
        ReadOnlySpan<byte> nonce)
    {
        return new PtyHandoffMessage
        {
            Kind = PtyHandoffMessageKind.Hello,
            RuntimeSessionId = runtimeSessionId ?? string.Empty,
            PaneId = paneId ?? string.Empty,
            Generation = generation,
            Nonce = CopyNonce(nonce),
        };
    }

    public static PtyHandoffMessage CreatePrepare(ReadOnlySpan<byte> nonce, string targetRuntimeId) =>
        new()
        {
            Kind = PtyHandoffMessageKind.Prepare,
            Nonce = CopyNonce(nonce),
            TargetRuntimeId = targetRuntimeId ?? string.Empty,
        };

    public static PtyHandoffMessage CreateHandleMeta(
        ReadOnlySpan<byte> nonce,
        int generation,
        int childPid,
        ushort cols = 80,
        ushort rows = 24) =>
        new()
        {
            Kind = PtyHandoffMessageKind.HandleMeta,
            Nonce = CopyNonce(nonce),
            Generation = generation,
            ChildPid = childPid,
            Cols = cols,
            Rows = rows,
        };

    public static PtyHandoffMessage CreateCommit(ReadOnlySpan<byte> nonce) =>
        new()
        {
            Kind = PtyHandoffMessageKind.Commit,
            Nonce = CopyNonce(nonce),
        };

    public static PtyHandoffMessage CreateAbort(int reason, string? message = null) =>
        new()
        {
            Kind = PtyHandoffMessageKind.Abort,
            AbortReason = reason,
            AbortMessage = message,
        };

    private static byte[] CopyNonce(ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceLength)
            throw new ArgumentException($"Nonce must be {NonceLength} bytes.", nameof(nonce));
        return nonce.ToArray();
    }
}
