namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// In-memory controller lease grant (F1). Durable SQLite lease CRUD is deferred.
/// </summary>
public sealed record LeaseState
{
    public required string LeaseId { get; init; }
    public required string PaneId { get; init; }
    public required string Scope { get; init; }
    public required string HolderId { get; init; }
    public required string State { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset GraceEndsAt { get; init; }
    public int TtlMs { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
