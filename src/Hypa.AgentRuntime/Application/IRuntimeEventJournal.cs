using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Durable monotonic event journal (HYJR segments + SQLite manifests).
/// Live fanout does not wait for the journal write.
/// </summary>
public interface IRuntimeEventJournal
{
    /// <summary>
    /// Scan segments, truncate incomplete tails, restore next_seq and replay flags.
    /// Call once at host start before admitting clients.
    /// </summary>
    Task<RuntimeResult<JournalHealth>> RecoverAsync(CancellationToken ct = default);

    /// <summary>
    /// Allocate the next reliable sequence and queue the disk write.
    /// The record can be published before <see cref="CommitReservedAsync"/> finishes.
    /// Returns null when recovery has not finished. A disk failure later consumes
    /// this sequence and raises the retention floor.
    /// Default writes through <see cref="AppendAsync"/> before it returns.
    /// </summary>
    RuntimeEventRecord? ReserveReliable(
        EventClass @class,
        string type,
        string payloadJson,
        DateTimeOffset occurredAt)
    {
        var append = AppendAsync(
                @class,
                EventReliability.Reliable,
                type,
                payloadJson,
                occurredAt)
            .GetAwaiter()
            .GetResult();
        if (append.IsOk)
            return append.Value;
        return append.Error.LiveRecord;
    }

    /// <summary>
    /// Wait for the disk write of a record from <see cref="ReserveReliable"/>.
    /// Default is already durable. The file journal completes this when the
    /// queued write finishes. A failure has already raised the retention floor.
    /// </summary>
    Task<RuntimeResult<RuntimeUnit>> CommitReservedAsync(
        RuntimeEventRecord record,
        CancellationToken ct = default) =>
        Task.FromResult(RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value));

    /// <summary>
    /// Allocate the next seq, append a record, update manifests/cursor.
    /// Reliable (Control, Lifecycle) fsyncs the open file then the directory.
    /// Output flushes without durable sync, including on segment rotation.
    /// Render is refused (live-only).
    /// Returns the sequenced record. May fail with persistence errors or journal full.
    /// </summary>
    Task<RuntimeResult<RuntimeEventRecord>> AppendAsync(
        EventClass @class,
        EventReliability reliability,
        string type,
        string payloadJson,
        DateTimeOffset? occurredAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Append a payload already encoded as UTF-8 JSON. Default decodes to a
    /// string and calls <see cref="AppendAsync"/>. File journal writes the
    /// bytes without a managed payload string.
    /// </summary>
    Task<RuntimeResult<RuntimeEventRecord>> AppendUtf8Async(
        EventClass @class,
        EventReliability reliability,
        string type,
        ReadOnlyMemory<byte> payloadUtf8,
        DateTimeOffset? occurredAt = null,
        CancellationToken ct = default) =>
        AppendAsync(
            @class,
            reliability,
            type,
            System.Text.Encoding.UTF8.GetString(payloadUtf8.Span),
            occurredAt,
            ct);

    /// <summary>
    /// Flush a pending Output manifest and cursor. Default is a no-op so
    /// test fakes keep compiling. File journal writes both rows on one
    /// connection. Call before a stop report.
    /// </summary>
    Task<RuntimeResult<RuntimeUnit>> FlushManifestAsync(CancellationToken ct = default) =>
        Task.FromResult(RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value));

    /// <summary>
    /// Read records with seq greater than <paramref name="fromSeqExclusive"/>,
    /// optionally filtered by event classes, up to <paramref name="budget"/> records.
    /// </summary>
    Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> ReadRangeAsync(
        long fromSeqExclusive,
        IReadOnlySet<EventClass>? classes,
        int budget,
        CancellationToken ct = default);

    /// <summary>Close the open segment (footer + fsync + manifest closed=1).</summary>
    Task<RuntimeResult<RuntimeUnit>> CloseOpenSegmentAsync(CancellationToken ct = default);

    JournalHealth GetHealth();

    /// <summary>Next sequence the allocator will assign.</summary>
    long NextSeq { get; }

    /// <summary>
    /// Run <paramref name="action"/> while holding the journal writer lock.
    /// Default runs the action with no extra lock so test fakes stay unchanged.
    /// File journal holds the HYJR writer gate so Output cannot move
    /// <see cref="NextSeq"/> between a checkpoint barrier sample and freeze.
    /// </summary>
    void RunExclusive(Action action) => action();
}
