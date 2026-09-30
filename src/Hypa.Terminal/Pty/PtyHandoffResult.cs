namespace Hypa.Terminal.Pty;

/// <summary>
/// Expected handoff outcomes. Prefer this over exceptions for protocol/capability failures.
/// </summary>
public enum PtyHandoffStatus
{
    Ok = 0,
    CapabilityAbsent = 1,
    NonceMismatch = 2,
    GenerationMismatch = 3,
    Timeout = 4,
    Protocol = 5,
    PlatformUnsupported = 6,
    Failed = 7,
}

/// <summary>
/// Result of an H2 same-host handoff export or adopt attempt.
/// </summary>
public readonly struct PtyHandoffResult
{
    public PtyHandoffStatus Status { get; }
    public string? Message { get; }

    private PtyHandoffResult(PtyHandoffStatus status, string? message)
    {
        Status = status;
        Message = message;
    }

    public bool IsOk => Status == PtyHandoffStatus.Ok;

    public static PtyHandoffResult Ok() => new(PtyHandoffStatus.Ok, null);

    public static PtyHandoffResult CapabilityAbsent(string? message = null) =>
        new(PtyHandoffStatus.CapabilityAbsent, message ?? "PTY provider does not support H2 handoff.");

    public static PtyHandoffResult NonceMismatch(string? message = null) =>
        new(PtyHandoffStatus.NonceMismatch, message ?? "Handoff nonce mismatch.");

    public static PtyHandoffResult GenerationMismatch(string? message = null) =>
        new(PtyHandoffStatus.GenerationMismatch, message ?? "Handoff generation mismatch.");

    public static PtyHandoffResult Timeout(string? message = null) =>
        new(PtyHandoffStatus.Timeout, message ?? "Handoff timed out.");

    public static PtyHandoffResult Protocol(string? message = null) =>
        new(PtyHandoffStatus.Protocol, message ?? "Handoff protocol error.");

    public static PtyHandoffResult PlatformUnsupported(string? message = null) =>
        new(PtyHandoffStatus.PlatformUnsupported, message ?? "H2 handoff is not supported on this platform.");

    public static PtyHandoffResult Failed(string? message = null) =>
        new(PtyHandoffStatus.Failed, message ?? "Handoff failed.");

    public override string ToString() =>
        Message is null ? Status.ToString() : $"{Status}: {Message}";
}

/// <summary>Thrown by import when handoff fails closed with a typed status.</summary>
public sealed class PtyHandoffException : Exception
{
    public PtyHandoffResult Result { get; }

    public PtyHandoffException(PtyHandoffResult result)
        : base(result.Message ?? result.Status.ToString())
    {
        Result = result;
    }
}
