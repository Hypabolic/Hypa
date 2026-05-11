namespace Hypa.Runtime.Domain.Sessions;

public sealed record ArtifactRef
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public string MimeType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string StoragePath { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
