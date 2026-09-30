using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// In-memory observer/controller attachment registry (F1, connection-bound).
/// Attachments are session-capped (health.limits.max_attachments) and must be
/// dropped on disconnect <em>and</em> on <c>events.unsubscribe</c>.
/// </summary>
public interface IAttachmentRegistry
{
    /// <summary>Create an attachment. Fails when max attachments is reached.</summary>
    AttachmentCreateResult Create(
        string paneId,
        string connectionId,
        string subscriptionId,
        string mode,
        string? leaseId);

    /// <summary>
    /// Return an existing attachment for the same (connection, subscription, pane, mode)
    /// when present; otherwise create. Prevents re-observe quota exhaustion.
    /// </summary>
    AttachmentCreateResult GetOrCreate(
        string paneId,
        string connectionId,
        string subscriptionId,
        string mode,
        string? leaseId);

    /// <summary>Drop a single attachment by id (rollback / explicit detach).</summary>
    AttachmentState? Drop(string attachmentId);

    /// <summary>Drop all attachments bound to a subscription (events.unsubscribe).</summary>
    IReadOnlyList<AttachmentState> DropBySubscription(string subscriptionId);

    /// <summary>Drop all attachments for a connection (disconnect).</summary>
    IReadOnlyList<AttachmentState> DropConnection(string connectionId);

    /// <summary>
    /// Drop all attachments bound to a pane (<c>pane.close</c> / spawn rollback).
    /// Frees <c>max_attachments</c> quota without requiring events.unsubscribe.
    /// </summary>
    IReadOnlyList<AttachmentState> DropByPane(string paneId);

    AttachmentState? Get(string attachmentId);

    /// <summary>Snapshot of live attachments. Used to count observe/control foreground clients.</summary>
    IReadOnlyList<AttachmentState> ListAll();

    int Count { get; }
}

/// <summary>Result of <see cref="IAttachmentRegistry.Create"/> / <see cref="IAttachmentRegistry.GetOrCreate"/>.</summary>
public sealed record AttachmentCreateResult
{
    public bool Ok { get; init; }
    public AttachmentState? Attachment { get; init; }
    public bool LimitExceeded { get; init; }
    public bool Invalid { get; init; }
    /// <summary>True when an existing attachment was returned (idempotent re-attach).</summary>
    public bool Reused { get; init; }
}
