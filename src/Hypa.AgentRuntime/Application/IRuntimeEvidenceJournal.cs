namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Runtime export cursor journal (event_exports). Not Atomic's evidence ledger
/// and not <c>Hypa.Runtime</c> <c>IEvidenceLedger</c>.
/// </summary>
public interface IRuntimeEvidenceJournal
{
    /// <summary>
    /// Upsert an export ack cursor. One row per (session_id, consumer); create-on-ack.
    /// <paramref name="lastSeq"/> is applied as a non-decreasing high-water mark
    /// (<c>MAX(existing, lastSeq)</c>). A new <paramref name="exportId"/> for the same
    /// session+consumer updates the row's export_id.
    /// </summary>
    Task<RuntimeResult<ExportAckRecord>> AckAsync(
        string sessionId,
        string exportId,
        long lastSeq,
        string consumer,
        CancellationToken ct = default);

    /// <summary>Lookup by export_id, or null when absent.</summary>
    Task<RuntimeResult<ExportAckRecord?>> GetAsync(string exportId, CancellationToken ct = default);
}

/// <summary>Persisted export ack row.</summary>
public sealed record ExportAckRecord
{
    public required string ExportId { get; init; }
    public required string SessionId { get; init; }
    public required string Consumer { get; init; }
    public required long LastAckedSeq { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
