using Hypa.Runtime.Domain.Common;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Ports;

/// <summary>
/// Isolates native tree-sitter parse work from the export parent process.
/// Implementations may spawn a worker process so a SIGSEGV cannot kill the parent.
/// </summary>
public interface INativeParseHost
{
    Task<Result<CodeStructureDocument, NativeParseError>> ParseAsync(
        NativeParseRequest request,
        CancellationToken ct);
}

/// <summary>
/// Request for one isolated native parse. The worker re-reads
/// <see cref="AbsolutePath"/> (first slice does not send content on stdin).
/// </summary>
public sealed record NativeParseRequest
{
    public required CodeFileIdentity File { get; init; }
    public required string Language { get; init; }
    public required string AbsolutePath { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>Expected failure modes for an isolated native parse.</summary>
public enum NativeParseErrorKind
{
    Crashed,
    TimedOut,
    ProtocolError,
    IoError,
    SpawnFailed,
    ParseFailure,
}

/// <summary>Error value for <see cref="INativeParseHost"/> failures.</summary>
public sealed record NativeParseError
{
    public required NativeParseErrorKind Kind { get; init; }
    public int? ExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
}
