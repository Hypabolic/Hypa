using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// SQLite adapter for <c>journal_segments</c> and <c>event_cursor</c>.
/// Kept separate from <see cref="IRuntimeSessionStore"/> so journal writes do not
/// race graph snapshots over the sequence allocator.
/// </summary>
public interface IJournalManifestStore
{
    Task<RuntimeResult<RuntimeUnit>> UpsertSegmentAsync(
        JournalSegmentManifest segment, CancellationToken ct = default);

    Task<RuntimeResult<IReadOnlyList<JournalSegmentManifest>>> ListSegmentsAsync(
        string sessionId, CancellationToken ct = default);

    Task<RuntimeResult<JournalSegmentManifest?>> GetOpenSegmentAsync(
        string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Mark a segment closed with final bounds, counts, and full-file checksum.
    /// </summary>
    Task<RuntimeResult<RuntimeUnit>> CloseSegmentAsync(
        string segmentId,
        long? lastSeq,
        int recordCount,
        long byteCount,
        string checksumSha256,
        DateTimeOffset closedAt,
        CancellationToken ct = default);

    Task<RuntimeResult<long>> ReadEventCursorAsync(
        string sessionId, CancellationToken ct = default);

    Task<RuntimeResult<RuntimeUnit>> WriteEventCursorAsync(
        string sessionId, long nextSeq, CancellationToken ct = default);

    /// <summary>
    /// Write the open-segment row and the event cursor on one connection
    /// in one transaction. Default calls <see cref="UpsertSegmentAsync"/>
    /// then <see cref="WriteEventCursorAsync"/> so test fakes keep working.
    /// </summary>
    Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
        JournalSegmentManifest segment,
        string sessionId,
        long nextSeq,
        CancellationToken ct = default)
    {
        return FlushSegmentProgressDefaultAsync(segment, sessionId, nextSeq, ct);
    }

    /// <summary>
    /// Write segment progress, <c>next_seq</c>, and the retention floor in one step.
    /// The floor never moves down. Default flushes progress, then raises the floor.
    /// </summary>
    Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
        JournalSegmentManifest segment,
        string sessionId,
        long nextSeq,
        long floorSeq,
        CancellationToken ct = default)
    {
        return FlushSegmentProgressWithFloorDefaultAsync(
            segment, sessionId, nextSeq, floorSeq, ct);
    }

    private async Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressDefaultAsync(
        JournalSegmentManifest segment,
        string sessionId,
        long nextSeq,
        CancellationToken ct)
    {
        var upsert = await UpsertSegmentAsync(segment, ct).ConfigureAwait(false);
        if (!upsert.IsOk)
            return upsert;
        return await WriteEventCursorAsync(sessionId, nextSeq, ct).ConfigureAwait(false);
    }

    private async Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressWithFloorDefaultAsync(
        JournalSegmentManifest segment,
        string sessionId,
        long nextSeq,
        long floorSeq,
        CancellationToken ct)
    {
        var flush = await FlushSegmentProgressAsync(segment, sessionId, nextSeq, ct)
            .ConfigureAwait(false);
        if (!flush.IsOk)
            return flush;
        return await RaiseRetentionFloorAsync(sessionId, floorSeq, nextSeq, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Retention floor stored with the segment cursor. Zero when unset.</summary>
    Task<RuntimeResult<long>> ReadRetentionFloorAsync(
        string sessionId, CancellationToken ct = default) =>
        Task.FromResult(RuntimeResult<long>.Ok(0L));

    /// <summary>
    /// Raise the retention floor and the event cursor. The floor never moves down.
    /// </summary>
    Task<RuntimeResult<RuntimeUnit>> RaiseRetentionFloorAsync(
        string sessionId, long floorSeq, long nextSeq, CancellationToken ct = default) =>
        WriteEventCursorAsync(sessionId, nextSeq, ct);

    /// <summary>
    /// Delete one closed manifest row and raise the floor in one transaction.
    /// The caller deletes the file after this commit.
    /// </summary>
    Task<RuntimeResult<RuntimeUnit>> DeleteClosedSegmentAsync(
        string segmentId, string sessionId, long floorSeq, CancellationToken ct = default) =>
        Task.FromResult(RuntimeResult<RuntimeUnit>.Fail(
            RuntimePersistenceError.Io("segment delete is not supported")));
}
