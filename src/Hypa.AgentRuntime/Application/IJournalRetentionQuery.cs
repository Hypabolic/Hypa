namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Pins that stop HYJR compaction. A null ack means no export row.
/// A null barrier means no active handoff or checkpoint barrier.
/// </summary>
public interface IJournalRetentionQuery
{
    Task<RuntimeResult<JournalRetentionHold>> ReadHoldAsync(
        string sessionId, CancellationToken ct = default);
}

/// <summary>Compaction pins for one session.</summary>
public readonly record struct JournalRetentionHold(long? LowestExportAck, long? BarrierSeq);

/// <summary>No export cursor and no barrier. Compaction may delete closed segments.</summary>
public sealed class EmptyJournalRetentionQuery : IJournalRetentionQuery
{
    public static readonly EmptyJournalRetentionQuery Instance = new();

    public Task<RuntimeResult<JournalRetentionHold>> ReadHoldAsync(
        string sessionId, CancellationToken ct = default) =>
        Task.FromResult(RuntimeResult<JournalRetentionHold>.Ok(default));
}
