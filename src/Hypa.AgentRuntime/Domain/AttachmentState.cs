namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// In-memory observer/controller attachment bound to a connection (F1).
/// </summary>
public sealed record AttachmentState
{
    public required string AttachmentId { get; init; }
    public required string PaneId { get; init; }
    public required string ConnectionId { get; init; }
    public required string SubscriptionId { get; init; }
    public required string Mode { get; init; }
    public string? LeaseId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
