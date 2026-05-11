namespace Hypa.Runtime.Domain.Sessions;

public abstract record EvidenceRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
    public abstract string Kind { get; }
}
