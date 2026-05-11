namespace Hypa.Runtime.Domain.Runner;

public sealed record CommandMetricsRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Command { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public long DurationMs { get; init; }
    public int OriginalTokens { get; init; }
    public int CompressedTokens { get; init; }
    public string ReducerId { get; init; } = string.Empty;
    public Guid? TeeArtifactId { get; init; }
}
