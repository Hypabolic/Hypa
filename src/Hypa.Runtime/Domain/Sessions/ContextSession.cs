namespace Hypa.Runtime.Domain.Sessions;

public sealed record ContextSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ProjectRoot { get; init; } = string.Empty;
    public SessionBinding? Binding { get; init; }
    public SessionStats Stats { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CheckpointedAt { get; init; }
}
