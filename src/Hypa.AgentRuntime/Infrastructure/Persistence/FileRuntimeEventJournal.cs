using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// HYJR file-backed event journal with SQLite manifests and event_cursor.
/// A reliable sequence is allocated before the disk write.
/// Live fanout does not wait for fsync or the floor write.
/// A failed disk write consumes that sequence and raises the retention floor.
/// <c>terminal.output</c> is live-only and is not written here.
/// </summary>
public sealed class FileRuntimeEventJournal : IRuntimeEventJournal, IAsyncDisposable
{
    /// <summary>Rotate open segment near this size.</summary>
    public const long SegmentRotateBytes = 8L * 1024 * 1024;

    /// <summary>Rotate after this many records in one segment.</summary>
    public const int SegmentRotateRecords = 10_000;

    /// <summary>Control budget. Compaction deletes oldest closed segments above this.</summary>
    public const long MaxJournalBytes = 32L * 1024 * 1024;

    /// <summary>Minimum gap between journal-refusal lines in the process log.</summary>
    public static readonly TimeSpan RefusalLogInterval = TimeSpan.FromSeconds(60);

    /// <summary>Max payload size for output events (fits base64 of 64 KiB raw).</summary>
    public const int MaxPayloadBytes = HyjrPayloadJson.MaxPayloadBytes;

    private readonly RuntimeStatePaths _paths;
    private readonly IJournalManifestStore _manifests;
    private readonly string _sessionId;
    private readonly ILogger _logger;
    private readonly IDurableFileSync _durableSync;
    private readonly long _rotateBytes;
    private readonly int _rotateRecords;
    private readonly int _manifestFlushRecords;
    private readonly TimeSpan _manifestFlushInterval;
    private readonly long _maxJournalBytes;
    private readonly IJournalRetentionQuery _retention;
    private readonly IProcessLogSink _processLog;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _manifestFlushGate = new(1, 1);
    private readonly object _allocLock = new();
    private readonly Channel<ReservedCommit> _commitQueue = Channel.CreateUnbounded<ReservedCommit>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<long, ReservedCommit> _pendingCommits = new();

    // A waiter can look up its commit after the worker removed it. Keep each
    // failed result until its waiter reads it, so a failure never reads as success.
    private readonly ConcurrentDictionary<long, RuntimeResult<RuntimeUnit>> _failedCommits = new();

    // A reserved sequence can be published before its write. The durable cursor
    // is kept one block ahead, so a restart after a crash never reuses a
    // sequence that a client already saw. A clean dispose writes the exact value.
    private const long SequenceLeaseBlock = 4096;
    private long _sequenceLeaseHigh;
    private DateTimeOffset _leaseRetryAfter;
    private static readonly TimeSpan LeaseRetryInterval = TimeSpan.FromSeconds(5);
    private bool _stopping;
    private Task? _commitWorker;
    private int _commitWorkerStarted;
    private bool _floorDirty;

    private JournalSegmentManifest? _pendingManifest;
    private long _pendingNextSeq;
    private bool _manifestDirty;
    private int _recordsSinceFlush;
    private long _lastFlushTicks;
    private bool _flushTimerArmed;
    private CancellationTokenSource _flushTimerCts = new();

    private long _nextSeq;
    private bool _replayComplete = true;
    private string? _replayError;
    private long _totalBytes;
    private long _floorSeq;
    private int _refusalCount;
    private int _refusalsSinceLog;
    private string? _lastRefusalReason;
    private DateTimeOffset _lastRefusalLogAt;
    private bool _refusalLogged;
    private bool _recovered;

    private Func<string, FileMode, FileStream>? _segmentStreamFactory;
    private FileStream? _openStream;
    private string? _openSegmentId;
    private string? _openRelativePath;
    private long _openFirstSeq;
    private int _openRecordCount;
    private long _openBytes;
    private long? _openLastSeq;
    /// <summary>
    /// A write landed in the open stream and could not be truncated.
    /// The next append rotates. Close must not publish a footer or checksum.
    /// </summary>
    private bool _openSegmentUnusable;

    public FileRuntimeEventJournal(
        RuntimeStatePaths paths,
        IJournalManifestStore manifests,
        string sessionId,
        ILogger? logger = null,
        IDurableFileSync? durableSync = null,
        long? rotateBytes = null,
        int? rotateRecords = null,
        int? manifestFlushRecords = null,
        TimeSpan? manifestFlushInterval = null,
        long? maxJournalBytes = null,
        IJournalRetentionQuery? retention = null,
        IProcessLogSink? processLog = null,
        TimeProvider? time = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _logger = logger ?? NullLogger.Instance;
        _durableSync = durableSync ?? DurableFileSync.Production;
        if (rotateBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(rotateBytes));
        if (rotateRecords is <= 0)
            throw new ArgumentOutOfRangeException(nameof(rotateRecords));
        if (manifestFlushRecords is <= 0)
            throw new ArgumentOutOfRangeException(nameof(manifestFlushRecords));
        if (manifestFlushInterval is { Ticks: <= 0 })
            throw new ArgumentOutOfRangeException(nameof(manifestFlushInterval));
        if (maxJournalBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxJournalBytes));
        _rotateBytes = rotateBytes ?? SegmentRotateBytes;
        _rotateRecords = rotateRecords ?? SegmentRotateRecords;
        _manifestFlushRecords = manifestFlushRecords ?? 512;
        _manifestFlushInterval = manifestFlushInterval ?? TimeSpan.FromMilliseconds(250);
        _maxJournalBytes = maxJournalBytes ?? MaxJournalBytes;
        _retention = retention ?? EmptyJournalRetentionQuery.Instance;
        _processLog = processLog ?? NullProcessLogSink.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Tests open segment files through this factory so a write can leave a
    /// complete record when truncation fails.
    /// </summary>
    internal void SetSegmentStreamFactory(Func<string, FileMode, FileStream> factory) =>
        _segmentStreamFactory = factory ?? throw new ArgumentNullException(nameof(factory));

    public long NextSeq
    {
        get { return Volatile.Read(ref _nextSeq); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The caller must hold the session emit gate. Otherwise new reserves can keep
    /// the retry loop below from reaching a moment with no pending commit.
    /// </remarks>
    public void RunExclusive(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        while (true)
        {
            // A barrier must not include a live event whose write may still fail.
            // Wait outside _gate: the commit worker holds _gate while it writes.
            WaitForPendingCommitsAsync(CancellationToken.None).GetAwaiter().GetResult();
            FlushManifestAsync(CancellationToken.None).GetAwaiter().GetResult();
            _gate.Wait();
            var allocHeld = false;
            try
            {
                Monitor.Enter(_allocLock, ref allocHeld);
                // A reserve that landed after the wait is still unwritten: retry.
                if (!_pendingCommits.IsEmpty)
                    continue;
                action();
                return;
            }
            finally
            {
                if (allocHeld)
                    Monitor.Exit(_allocLock);
                _gate.Release();
            }
        }
    }

    public JournalHealth GetHealth() => new()
    {
        NextSeq = NextSeq,
        ReplayComplete = _replayComplete,
        Bytes = _totalBytes,
        ReplayError = _replayError,
        FloorSeq = _floorSeq,
        RefusalCount = _refusalCount,
        LastRefusalReason = _lastRefusalReason,
    };

    public async Task<RuntimeResult<JournalHealth>> RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectory();
            _paths.EnsureJournalDirectory();

            var list = await _manifests.ListSegmentsAsync(_sessionId, ct).ConfigureAwait(false);
            if (!list.IsOk)
                return RuntimeResult<JournalHealth>.Fail(list.Error);

            var cursor = await _manifests.ReadEventCursorAsync(_sessionId, ct).ConfigureAwait(false);
            if (!cursor.IsOk)
                return RuntimeResult<JournalHealth>.Fail(cursor.Error);

            var floor = await _manifests.ReadRetentionFloorAsync(_sessionId, ct).ConfigureAwait(false);
            if (!floor.IsOk)
                return RuntimeResult<JournalHealth>.Fail(floor.Error);
            _floorSeq = floor.Value;

            // Allocator is 1-based so exclusive from_seq=0 means "all events" (design fixtures).
            long maxSeq = cursor.Value > 0 ? cursor.Value : 1;
            // The next sequence that the segments themselves prove.
            long segmentNext = 1;
            long totalBytes = 0;
            // Bytes of orphan files that could not be deleted. No manifest row
            // lists them, so compaction must add them to its own total.
            long orphanBytes = 0;
            var replayComplete = true;
            string? replayError = null;

            // Also scan journal directory for orphan files not yet in manifest.
            var segments = list.Value.ToList();
            foreach (var file in Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr")
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var rel = Path.GetRelativePath(_paths.StateDirectory, file)
                    .Replace('\\', '/');
                if (segments.All(s => !string.Equals(s.RelativePath, rel, StringComparison.Ordinal)))
                {
                    // A file left after the manifest delete is below the floor.
                    // Delete it. Do not import it. Recovery stays complete.
                    if (OrphanFileIsBelowFloor(file))
                    {
                        try { File.Delete(file); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Still on disk, so it still counts against the budget.
                            var orphan = new FileInfo(file).Length;
                            totalBytes += orphan;
                            orphanBytes += orphan;
                            _logger.LogWarning(ex, "Journal could not delete compacted segment {Path}", rel);
                        }

                        continue;
                    }

                    segments.Add(new JournalSegmentManifest
                    {
                        SegmentId = "orphan_" + Path.GetFileNameWithoutExtension(file),
                        SessionId = _sessionId,
                        RelativePath = rel,
                        FirstSeq = 0,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Closed = false,
                    });
                }
            }

            await CompactListedAsync(segments, ct, orphanBytes).ConfigureAwait(false);

            // At most one reopen candidate (highest first_seq / latest relative_path).
            JournalSegmentManifest? reopenSeg = null;
            HyjrRecoveryResult? reopenRecovery = null;

            foreach (var seg in segments.OrderBy(s => s.FirstSeq)
                         .ThenBy(s => s.CreatedAt)
                         .ThenBy(s => s.RelativePath, StringComparer.Ordinal))
            {
                var abs = Path.Combine(_paths.StateDirectory, seg.RelativePath);
                if (!File.Exists(abs))
                {
                    if (!seg.Closed)
                    {
                        replayComplete = false;
                        replayError ??= $"missing open segment file {seg.RelativePath}";
                    }
                    continue;
                }

                HyjrRecoveryResult recovery;
                try
                {
                    // Closed checksum is the SHA-256 of the complete file (design §10.1).
                    // Match it instead of parsing every record. Any miss uses full recovery.
                    if (seg.Closed
                        && !string.IsNullOrEmpty(seg.ChecksumSha256)
                        && HyjrSegmentFormat.TryVerifyClosedFile(
                            abs, seg.ChecksumSha256, seg.ByteCount, out var verified))
                    {
                        recovery = verified;
                    }
                    else
                    {
                        recovery = HyjrSegmentFormat.RecoverFile(abs);
                    }
                }
                catch (Exception ex)
                {
                    replayComplete = false;
                    replayError ??= ex.Message;
                    continue;
                }

                totalBytes += recovery.Bytes;
                if (recovery.LastValidSeq is { } last)
                {
                    maxSeq = Math.Max(maxSeq, last + 1);
                    segmentNext = Math.Max(segmentNext, last + 1);
                }
                else if (recovery.FirstSeq > 0)
                    maxSeq = Math.Max(maxSeq, recovery.FirstSeq);

                var isLast = IsChronologicallyLast(seg, segments);
                if (recovery.Closed && !seg.Closed)
                {
                    var checksum = HyjrSegmentFormat.ComputeFileChecksum(abs);
                    await _manifests.CloseSegmentAsync(
                        seg.SegmentId,
                        recovery.LastValidSeq,
                        recovery.RecordCount,
                        recovery.Bytes,
                        checksum,
                        DateTimeOffset.UtcNow,
                        ct).ConfigureAwait(false);
                }
                else if (!recovery.Closed && !HeaderIsUsable(recovery))
                {
                    // A crash before the header write leaves an empty file. It holds no
                    // events. A bad header is corruption. Never append to either.
                    if (recovery.Bytes > 0)
                    {
                        replayComplete = false;
                        replayError ??= recovery.ReplayError;
                    }
                }
                else if (!recovery.Closed && !isLast && !seg.Closed && TailIsClean(recovery))
                {
                    // An older segment with a valid record prefix and no footer is not
                    // the append target. Seal it so a later closed segment stays last.
                    if (await TrySealRecoveredSegmentAsync(seg, recovery, ct).ConfigureAwait(false))
                        totalBytes += HyjrSegmentFormat.FooterSize;
                    else
                    {
                        // A failed seal rolls its footer back. Count any bytes a failed
                        // rollback left behind so the budget matches the disk.
                        var leftover = SealLeftoverBytes(abs, recovery.Bytes);
                        totalBytes += leftover;
                        replayComplete = false;
                        replayError ??= recovery.ReplayError ?? "could not seal recovered segment";
                        await UpsertOpenSegmentAsync(
                                seg, recovery with { Bytes = recovery.Bytes + leftover }, ct)
                            .ConfigureAwait(false);
                    }
                }
                else if (!recovery.Closed && !isLast)
                {
                    // Older than a later segment. Do not reopen it for append.
                    replayComplete = false;
                    replayError ??= recovery.ReplayError ?? "closed segment failed footer validation";
                    if (!seg.Closed)
                        await UpsertOpenSegmentAsync(seg, recovery, ct).ConfigureAwait(false);
                }
                else if (!recovery.Closed)
                {
                    replayComplete = false;
                    replayError ??= recovery.ReplayError ?? "closed segment failed footer validation";
                    if (!seg.Closed)
                    {
                        await UpsertOpenSegmentAsync(seg, recovery, ct).ConfigureAwait(false);
                        reopenSeg = seg with
                        {
                            FirstSeq = recovery.FirstSeq,
                            LastSeq = recovery.LastValidSeq,
                            RecordCount = recovery.RecordCount,
                            ByteCount = recovery.Bytes,
                            Closed = false,
                        };
                    }
                    else
                    {
                        // Latest segment was marked closed but its footer did not validate.
                        reopenSeg = seg;
                    }

                    reopenRecovery = recovery;
                }
            }

            // Reopen at most one incomplete tail segment for append.
            if (reopenSeg is not null && reopenRecovery is not null && !reopenSeg.Closed)
            {
                await OpenExistingSegmentAsync(
                    reopenSeg.SegmentId, reopenSeg.RelativePath, reopenRecovery, ct)
                    .ConfigureAwait(false);
            }
            else if (reopenSeg is not null && reopenRecovery is not null && reopenSeg.Closed)
            {
                // Sole tail was marked closed in SQLite but lacks a valid footer: reopen only
                // this highest segment so append can continue; leave replay_complete false.
                await _manifests.UpsertSegmentAsync(reopenSeg with
                {
                    FirstSeq = reopenRecovery.FirstSeq,
                    LastSeq = reopenRecovery.LastValidSeq,
                    RecordCount = reopenRecovery.RecordCount,
                    ByteCount = reopenRecovery.Bytes,
                    Closed = false,
                    ChecksumSha256 = null,
                }, ct).ConfigureAwait(false);
                await OpenExistingSegmentAsync(
                    reopenSeg.SegmentId, reopenSeg.RelativePath, reopenRecovery, ct)
                    .ConfigureAwait(false);
            }

            // If no open segment and we may need to write, leave stream null until first append.
            _totalBytes = totalBytes;
            _replayComplete = replayComplete;
            _replayError = replayError;
            RaiseFloorToOldest(segments);

            // The stored cursor runs ahead of the segments only after a crash:
            // sequences under the lease may have been published live and never
            // written. A resume from before them must expire, not skip them.
            if (cursor.Value > Math.Max(segmentNext, _floorSeq))
                _floorSeq = cursor.Value;

            lock (_allocLock)
            {
                // A failed floor write must not hand the consumed sequence out again.
                if (_floorSeq > maxSeq)
                    maxSeq = _floorSeq;
                _nextSeq = maxSeq;
            }

            // Take the first lease here, off the live publish path.
            var lease = maxSeq + SequenceLeaseBlock;
            var writeCursor = await _manifests.RaiseRetentionFloorAsync(
                _sessionId, _floorSeq, lease, ct).ConfigureAwait(false);
            if (!writeCursor.IsOk)
                return RuntimeResult<JournalHealth>.Fail(writeCursor.Error);

            lock (_allocLock)
            {
                _sequenceLeaseHigh = lease;
                _recovered = true;
            }

            StartCommitWorker();

            _logger.LogInformation(
                "Journal recovered next_seq={NextSeq} replay_complete={Complete} bytes={Bytes}",
                _nextSeq, _replayComplete, _totalBytes);

            return RuntimeResult<JournalHealth>.Ok(GetHealth());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public RuntimeEventRecord? ReserveReliable(
        EventClass @class,
        string type,
        string payloadJson,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payloadJson);
        // The same guards as AppendAsync, so live and replay carry the same payload.
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
        {
            _logger.LogWarning(
                "Reliable {Type} payload exceeds {Max} bytes; not published", type, MaxPayloadBytes);
            return null;
        }

        payloadJson = HyjrPayloadJson.EnsureJsonPayload(payloadJson);
        ReservedCommit item;
        lock (_allocLock)
        {
            // After dispose begins, no record may be published: the exact cursor
            // write at dispose would not cover it.
            if (!_recovered || _stopping)
                return null;

            var seq = _nextSeq;
            _nextSeq = seq + 1;
            // The commit worker renews the lease ahead of use. This synchronous
            // renewal runs only if a burst reaches the lease edge first.
            // After a failed write, do not block every reserve on SQLite again.
            if (_nextSeq > _sequenceLeaseHigh && _time.GetUtcNow() >= _leaseRetryAfter)
                ExtendSequenceLeaseUnlocked(_nextSeq + SequenceLeaseBlock);
            item = new ReservedCommit
            {
                Record = new RuntimeEventRecord
                {
                    Seq = seq,
                    Class = @class,
                    Reliability = EventReliability.Reliable,
                    Type = type,
                    OccurredAt = occurredAt,
                    PayloadJson = payloadJson,
                },
            };
            _pendingCommits[seq] = item;
            // Enqueue under the same lock as the allocation, so the single
            // commit worker always writes sequences in order.
            if (!_commitQueue.Writer.TryWrite(item))
            {
                var closed = RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Io("Journal commit queue is closed"));
                _failedCommits[item.Record.Seq] = closed;
                _pendingCommits.TryRemove(item.Record.Seq, out _);
                item.Done.TrySetResult(closed);
                return item.Record;
            }
        }

        return item.Record;
    }

    /// <inheritdoc />
    public Task<RuntimeResult<RuntimeUnit>> CommitReservedAsync(
        RuntimeEventRecord record,
        CancellationToken ct = default)
    {
        if (!_pendingCommits.TryGetValue(record.Seq, out var item))
        {
            return Task.FromResult(_failedCommits.TryRemove(record.Seq, out var failed)
                ? failed
                : RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value));
        }

        return ConsumeAsync(item, ct);

        async Task<RuntimeResult<RuntimeUnit>> ConsumeAsync(ReservedCommit pending, CancellationToken token)
        {
            try
            {
                var result = await pending.Done.Task.WaitAsync(token).ConfigureAwait(false);
                if (!result.IsOk)
                    _failedCommits.TryRemove(pending.Record.Seq, out _);
                return result;
            }
            catch (OperationCanceledException)
            {
                // Nobody will read this result: release it when the write ends.
                var seq = pending.Record.Seq;
                _ = pending.Done.Task.ContinueWith(
                    _ => _failedCommits.TryRemove(seq, out RuntimeResult<RuntimeUnit> _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }
        }
    }

    /// <summary>Renew the lease when less than half a block is left.</summary>
    private async Task RenewSequenceLeaseIfLowAsync()
    {
        long high;
        long floor;
        lock (_allocLock)
        {
            if (_sequenceLeaseHigh - _nextSeq >= SequenceLeaseBlock / 2)
                return;
            high = _nextSeq + SequenceLeaseBlock;
            floor = _floorSeq;
        }

        try
        {
            var stored = await _manifests.RaiseRetentionFloorAsync(_sessionId, floor, high)
                .ConfigureAwait(false);
            if (!stored.IsOk)
            {
                _logger.LogWarning(
                    "Journal could not renew the sequence lease {High}: {Error}", high, stored.Error.Message);
                return;
            }

            lock (_allocLock)
            {
                if (high > _sequenceLeaseHigh)
                    _sequenceLeaseHigh = high;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Journal could not renew the sequence lease {High}", high);
        }
    }

    /// <summary>
    /// Store the next lease top before any sequence under it is published.
    /// Runs once per <see cref="SequenceLeaseBlock"/> reserves. Caller holds _allocLock.
    /// </summary>
    private void ExtendSequenceLeaseUnlocked(long high)
    {
        var stored = _manifests.RaiseRetentionFloorAsync(_sessionId, _floorSeq, high)
            .GetAwaiter()
            .GetResult();
        if (stored.IsOk)
        {
            _sequenceLeaseHigh = high;
            _leaseRetryAfter = default;
            return;
        }

        _leaseRetryAfter = _time.GetUtcNow() + LeaseRetryInterval;
        _logger.LogWarning(
            "Journal could not store the sequence lease {High}: {Error}", high, stored.Error.Message);
    }

    /// <summary>Wait until every sequence allocated so far is written or gapped.</summary>
    private Task WaitForPendingCommitsAsync(CancellationToken ct)
    {
        long allocated;
        lock (_allocLock)
            allocated = _nextSeq;
        List<Task>? waits = null;
        foreach (var (seq, item) in _pendingCommits)
        {
            if (seq < allocated)
                (waits ??= []).Add(item.Done.Task);
        }

        return waits is null ? Task.CompletedTask : Task.WhenAll(waits).WaitAsync(ct);
    }

    private void StartCommitWorker()
    {
        if (Interlocked.CompareExchange(ref _commitWorkerStarted, 1, 0) != 0)
            return;
        _commitWorker = Task.Run(CommitWorkerAsync);
    }

    private async Task CommitWorkerAsync()
    {
        await foreach (var item in _commitQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // The worker must survive any fault. A dead worker leaves every later
            // CommitReservedAsync waiting forever, and each emitter with it.
            var result = RuntimeResult<RuntimeUnit>.Fail(
                RuntimePersistenceError.Io("Journal commit did not complete"));
            try
            {
                result = await WriteReservedAsync(item.Record, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Journal commit failed for seq {Seq}", item.Record.Seq);
                result = RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Io(ex.Message));
                try
                {
                    await GapReservedAsync(item.Record, ex.Message, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception gapEx)
                {
                    _logger.LogWarning(gapEx, "Journal gap failed for seq {Seq}", item.Record.Seq);
                }
            }
            finally
            {
                if (!result.IsOk)
                    _failedCommits[item.Record.Seq] = result;
                _pendingCommits.TryRemove(item.Record.Seq, out _);
                item.Done.TrySetResult(result);
            }

            await RenewSequenceLeaseIfLowAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Write one reserved record. The sequence is already consumed.
    /// Any failure raises the floor past that sequence.
    /// </summary>
    private async Task<RuntimeResult<RuntimeUnit>> WriteReservedAsync(
        RuntimeEventRecord record,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_recovered)
            {
                await GapReservedAsync(
                    record, "Journal RecoverAsync must run before AppendAsync", ct)
                    .ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Io("Journal RecoverAsync must run before AppendAsync"));
            }

            if (_floorDirty)
            {
                var prior = await PersistFloorAsync(ct).ConfigureAwait(false);
                if (!prior.IsOk)
                {
                    _logger.LogWarning(
                        "Journal retention floor is still not durable before seq {Seq}: {Error}",
                        record.Seq, prior.Error.Message);
                }
            }

            var frame = PendingFrameBytes(
                record.Type, record.OccurredAt, record.PayloadJson, NeedsRotation());
            await CompactIfOverAsync(ct, frame).ConfigureAwait(false);
            frame = PendingFrameBytes(
                record.Type, record.OccurredAt, record.PayloadJson, NeedsRotation());
            if (OverBudget(frame))
            {
                var reason =
                    $"Journal budget exceeded ({_maxJournalBytes} bytes); refusing reliable intake";
                await GapReservedAsync(record, reason, ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Budget(reason, record));
            }

            if (NeedsRotation())
            {
                var flushed = await FlushPendingDirectAsync(ct).ConfigureAwait(false);
                if (!flushed.IsOk)
                {
                    await GapReservedAsync(record, flushed.Error.Message, ct).ConfigureAwait(false);
                    return RuntimeResult<RuntimeUnit>.Fail(flushed.Error);
                }

                var closed = await CloseOpenSegmentCoreAsync(ct, durable: true).ConfigureAwait(false);
                if (!closed.IsOk)
                {
                    await GapReservedAsync(record, closed.Error.Message, ct).ConfigureAwait(false);
                    return RuntimeResult<RuntimeUnit>.Fail(closed.Error);
                }

                var opened = await OpenNewSegmentAsync(ct, durable: true, record.Seq)
                    .ConfigureAwait(false);
                if (!opened.IsOk)
                {
                    await GapReservedAsync(record, opened.Error.Message, ct).ConfigureAwait(false);
                    return RuntimeResult<RuntimeUnit>.Fail(opened.Error);
                }
            }

            var stored = HyjrSegmentFormat.WrapStoredPayload(
                record.Type, record.OccurredAt, record.PayloadJson);
            var committed = WriteRecordAndSync(
                record.Seq, record.Class, record.Reliability, Encoding.UTF8.GetBytes(stored));
            if (!committed.IsOk)
            {
                await GapReservedAsync(record, committed.Error.Message, ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Fail(committed.Error);
            }

            var written = committed.Value;
            _openRecordCount++;
            _openBytes += written;
            _openLastSeq = record.Seq;
            _totalBytes += written;
            MarkPendingUnlocked();
            // In-flight reserves sit above this record. The cursor must not
            // skip them until their own write finishes.
            _pendingNextSeq = record.Seq + 1;
            var manifest = await FlushPendingDirectAsync(ct).ConfigureAwait(false);
            if (!manifest.IsOk)
            {
                // The record is on disk. Replay must not present it as a live cursor
                // after the manifest write failed, and the sequence stays consumed.
                _logger.LogWarning(
                    "Journal manifest upsert failed after durable seq={Seq}: {Error}",
                    record.Seq, manifest.Error.Message);
                await GapReservedAsync(record, manifest.Error.Message, ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Fail(manifest.Error);
            }

            return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task GapReservedAsync(
        RuntimeEventRecord record, string reason, CancellationToken ct)
    {
        lock (_allocLock)
        {
            var floor = record.Seq + 1;
            if (floor > _floorSeq)
                _floorSeq = floor;
            if (floor > _nextSeq)
                _nextSeq = floor;
            _floorDirty = true;
        }

        NoteRefusal(record.Type, reason);
        var raised = await PersistFloorAsync(ct).ConfigureAwait(false);
        if (!raised.IsOk)
        {
            _logger.LogWarning(
                "Journal could not persist retention floor after seq {Seq}: {Error}",
                record.Seq, raised.Error.Message);
        }
    }

    private async Task<RuntimeResult<RuntimeUnit>> PersistFloorAsync(CancellationToken ct)
    {
        long floor;
        long next;
        lock (_allocLock)
        {
            floor = _floorSeq;
            next = _nextSeq;
        }

        var raised = await _manifests.RaiseRetentionFloorAsync(_sessionId, floor, next, ct)
            .ConfigureAwait(false);
        if (raised.IsOk)
        {
            lock (_allocLock)
                _floorDirty = false;
        }

        return raised;
    }

    private bool NeedsRotation() =>
        _openStream is null
        || _openSegmentUnusable
        || _openBytes >= _rotateBytes
        || _openRecordCount >= _rotateRecords;

    private static int PendingFrameBytes(
        string type, DateTimeOffset at, string payloadJson, bool includeHeader)
    {
        var stored = HyjrSegmentFormat.WrapStoredPayload(type, at, payloadJson);
        var payloadLen = Encoding.UTF8.GetByteCount(stored);
        var frame = 4 + HyjrSegmentFormat.RecordFixedBodySize + payloadLen + HyjrSegmentFormat.DigestSize;
        // Keep room for the footer that closes the segment this record lands in.
        // A rotation also closes the current segment (its footer) and opens a
        // new one (its header).
        frame += HyjrSegmentFormat.FooterSize;
        if (includeHeader)
            frame += HyjrSegmentFormat.HeaderSize + HyjrSegmentFormat.FooterSize;
        return frame;
    }

    private bool OverBudget(int pendingBytes)
    {
        if (pendingBytes <= 0)
            return _totalBytes >= _maxJournalBytes;
        return _totalBytes + pendingBytes > _maxJournalBytes;
    }

    public async Task<RuntimeResult<RuntimeEventRecord>> AppendAsync(
        EventClass @class,
        EventReliability reliability,
        string type,
        string payloadJson,
        DateTimeOffset? occurredAt = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payloadJson);

        if (@class == EventClass.Render || reliability == EventReliability.Render)
        {
            return RuntimeResult<RuntimeEventRecord>.Fail(
                RuntimePersistenceError.Io(
                    "Render events are live-only and must not be written to HYJR"));
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadBytes > MaxPayloadBytes)
        {
            // Truncate output payloads; refuse oversized reliable control payloads.
            if (reliability == EventReliability.Reliable)
            {
                return RuntimeResult<RuntimeEventRecord>.Fail(
                    RuntimePersistenceError.Io($"Event payload exceeds {MaxPayloadBytes} bytes"));
            }

            payloadJson = TruncateUtf8(payloadJson, MaxPayloadBytes);
        }

        // Fail closed: never persist a broken JSON value (redaction or truncate).
        payloadJson = HyjrPayloadJson.EnsureJsonPayload(payloadJson);

        if (LiveOnlyRefusal(@class, reliability, type) is { } earlyLiveOnly)
            return RuntimeResult<RuntimeEventRecord>.Fail(earlyLiveOnly);

        if (reliability == EventReliability.Reliable)
        {
            var at = occurredAt ?? _time.GetUtcNow();
            var reserved = ReserveReliable(@class, type, payloadJson, at);
            if (reserved is null)
            {
                return RuntimeResult<RuntimeEventRecord>.Fail(
                    RuntimePersistenceError.Io("Journal RecoverAsync must run before AppendAsync"));
            }

            var committed = await CommitReservedAsync(reserved, ct).ConfigureAwait(false);
            if (!committed.IsOk)
            {
                return RuntimeResult<RuntimeEventRecord>.Fail(
                    committed.Error with { LiveRecord = reserved });
            }

            return RuntimeResult<RuntimeEventRecord>.Ok(reserved);
        }

        var outputFlush = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_recovered)
            {
                return RuntimeResult<RuntimeEventRecord>.Fail(
                    RuntimePersistenceError.Io("Journal RecoverAsync must run before AppendAsync"));
            }

            if (LiveOnlyRefusal(@class, reliability, type) is { } liveOnly)
                return RuntimeResult<RuntimeEventRecord>.Fail(liveOnly);

            var blocked = await EnsureWritableAsync(
                @class, reliability, type, payloadJson, occurredAt, ct).ConfigureAwait(false);
            if (blocked is not null)
                return blocked.Value;

            if (_openStream is null ||
                _openSegmentUnusable ||
                _openBytes >= _rotateBytes ||
                _openRecordCount >= _rotateRecords)
            {
                // Output rotation must not wait on durable file/dir sync.
                // Reliable rotation and explicit close still fsync.
                var durableRotate = reliability == EventReliability.Reliable;
                var flushed = await FlushPendingDirectAsync(ct).ConfigureAwait(false);
                if (!flushed.IsOk)
                    return RuntimeResult<RuntimeEventRecord>.Fail(flushed.Error);

                var closed = await CloseOpenSegmentCoreAsync(ct, durableRotate)
                    .ConfigureAwait(false);
                if (!closed.IsOk)
                    return RuntimeResult<RuntimeEventRecord>.Fail(closed.Error);

                // Close can abandon a segment and count its uncounted tail.
                var afterClose = await RefuseIfOverBudgetAsync(
                    @class, reliability, type, payloadJson, occurredAt, ct).ConfigureAwait(false);
                if (afterClose is not null)
                    return afterClose.Value;

                var opened = await OpenNewSegmentAsync(ct, durableRotate)
                    .ConfigureAwait(false);
                if (!opened.IsOk)
                    return RuntimeResult<RuntimeEventRecord>.Fail(opened.Error);
            }

            var seq = _nextSeq;
            var at = occurredAt ?? DateTimeOffset.UtcNow;
            var record = new RuntimeEventRecord
            {
                Seq = seq,
                Class = @class,
                Reliability = reliability,
                Type = type,
                OccurredAt = at,
                PayloadJson = payloadJson,
            };

            var stored = HyjrSegmentFormat.WrapStoredPayload(type, at, payloadJson);
            var committed = WriteRecordAndSync(seq, @class, reliability, Encoding.UTF8.GetBytes(stored));
            if (!committed.IsOk)
                return RuntimeResult<RuntimeEventRecord>.Fail(committed.Error);
            var written = committed.Value;

            // Durable on disk: advance in-memory bounds. HYJR is the source of truth for
            // replay of the open segment; SQLite LastSeq is advisory for closed-segment skip.
            // A manifest failure after this write keeps the durable record for replay
            // and returns it as LiveRecord so callers can fan it out.
            _openRecordCount++;
            _openBytes += written;
            _openLastSeq = seq;
            _nextSeq = seq + 1;
            _totalBytes += written;

            MarkPendingUnlocked();
            if (reliability == EventReliability.Reliable)
            {
                var flushed = await FlushPendingDirectAsync(ct).ConfigureAwait(false);
                if (!flushed.IsOk)
                {
                    _logger.LogWarning(
                        "Journal manifest upsert failed after durable seq={Seq}: {Error}",
                        seq, flushed.Error.Message);
                    return RuntimeResult<RuntimeEventRecord>.Fail(
                        flushed.Error with { LiveRecord = record });
                }
            }
            else
            {
                outputFlush = NoteOutputFlushUnlocked();
            }

            return RuntimeResult<RuntimeEventRecord>.Ok(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return RuntimeResult<RuntimeEventRecord>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
        finally
        {
            _gate.Release();
            if (outputFlush)
                _ = FlushManifestAsync(CancellationToken.None);
        }
    }

    public async Task<RuntimeResult<RuntimeEventRecord>> AppendUtf8Async(
        EventClass @class,
        EventReliability reliability,
        string type,
        ReadOnlyMemory<byte> payloadUtf8,
        DateTimeOffset? occurredAt = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        if (@class == EventClass.Render || reliability == EventReliability.Render)
        {
            return RuntimeResult<RuntimeEventRecord>.Fail(
                RuntimePersistenceError.Io(
                    "Render events are live-only and must not be written to HYJR"));
        }

        var payload = payloadUtf8;
        if (payload.Length > MaxPayloadBytes)
        {
            if (reliability == EventReliability.Reliable)
            {
                return RuntimeResult<RuntimeEventRecord>.Fail(
                    RuntimePersistenceError.Io($"Event payload exceeds {MaxPayloadBytes} bytes"));
            }

            payload = payload[..TruncateUtf8Length(payload.Span, MaxPayloadBytes)];
        }

        if (!HyjrPayloadJson.IsJsonValue(payload.Span))
            payload = HyjrPayloadJson.OpaqueRedactedPayloadBytes;

        if (reliability == EventReliability.Reliable)
        {
            return await AppendAsync(
                    @class,
                    reliability,
                    type,
                    Encoding.UTF8.GetString(payload.Span),
                    occurredAt,
                    ct)
                .ConfigureAwait(false);
        }

        var outputFlush = false;
        RuntimeEventRecord? record = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_recovered)
            {
                return RuntimeResult<RuntimeEventRecord>.Fail(
                    RuntimePersistenceError.Io("Journal RecoverAsync must run before AppendAsync"));
            }

            if (LiveOnlyRefusal(@class, reliability, type) is { } liveOnlyUtf8)
                return RuntimeResult<RuntimeEventRecord>.Fail(liveOnlyUtf8);

            var payloadText = Encoding.UTF8.GetString(payload.Span);
            var blockedUtf8 = await EnsureWritableAsync(
                @class, reliability, type, payloadText, occurredAt, ct).ConfigureAwait(false);
            if (blockedUtf8 is not null)
                return blockedUtf8.Value;

            if (_openStream is null ||
                _openSegmentUnusable ||
                _openBytes >= _rotateBytes ||
                _openRecordCount >= _rotateRecords)
            {
                var durableRotate = reliability == EventReliability.Reliable;
                var flushed = await FlushPendingDirectAsync(ct).ConfigureAwait(false);
                if (!flushed.IsOk)
                    return RuntimeResult<RuntimeEventRecord>.Fail(flushed.Error);

                var closed = await CloseOpenSegmentCoreAsync(ct, durableRotate)
                    .ConfigureAwait(false);
                if (!closed.IsOk)
                    return RuntimeResult<RuntimeEventRecord>.Fail(closed.Error);

                // Close can abandon a segment and count its uncounted tail.
                var afterCloseUtf8 = await RefuseIfOverBudgetAsync(
                    @class, reliability, type, payloadText, occurredAt, ct).ConfigureAwait(false);
                if (afterCloseUtf8 is not null)
                    return afterCloseUtf8.Value;

                var opened = await OpenNewSegmentAsync(ct, durableRotate)
                    .ConfigureAwait(false);
                if (!opened.IsOk)
                    return RuntimeResult<RuntimeEventRecord>.Fail(opened.Error);
            }

            var seq = _nextSeq;
            var at = occurredAt ?? DateTimeOffset.UtcNow;
            using var storedBuffer = new PooledUtf8Buffer(payload.Length + 128);
            if (!HyjrPayloadJson.TryWriteStoredPayload(storedBuffer, type, at, payload.Span))
            {
                return RuntimeResult<RuntimeEventRecord>.Fail(
                    RuntimePersistenceError.Io("Failed to write stored payload envelope"));
            }

            var committed = WriteRecordAndSync(
                seq, @class, reliability, storedBuffer.WrittenSpan);
            if (!committed.IsOk)
                return RuntimeResult<RuntimeEventRecord>.Fail(committed.Error);
            var written = committed.Value;

            _openRecordCount++;
            _openBytes += written;
            _openLastSeq = seq;
            _nextSeq = seq + 1;
            _totalBytes += written;
            MarkPendingUnlocked();

            record = new RuntimeEventRecord
            {
                Seq = seq,
                Class = @class,
                Reliability = reliability,
                Type = type,
                OccurredAt = at,
                PayloadJson = "{}",
            };

            if (reliability == EventReliability.Reliable)
            {
                var flushed = await FlushPendingDirectAsync(ct).ConfigureAwait(false);
                if (!flushed.IsOk)
                {
                    _logger.LogWarning(
                        "Journal manifest upsert failed after durable seq={Seq}: {Error}",
                        seq, flushed.Error.Message);
                    return RuntimeResult<RuntimeEventRecord>.Fail(
                        flushed.Error with { LiveRecord = record });
                }
            }
            else
            {
                outputFlush = NoteOutputFlushUnlocked();
            }

            return RuntimeResult<RuntimeEventRecord>.Ok(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return RuntimeResult<RuntimeEventRecord>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
        finally
        {
            _gate.Release();
            if (outputFlush)
                _ = FlushManifestAsync(CancellationToken.None);
        }
    }

    public async Task<RuntimeResult<RuntimeUnit>> FlushManifestAsync(CancellationToken ct = default)
    {
        await _manifestFlushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                JournalSegmentManifest? pending;
                long nextSeq;
                long floor;
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!_manifestDirty || _pendingManifest is null)
                        return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
                    pending = _pendingManifest;
                    nextSeq = _pendingNextSeq;
                    lock (_allocLock)
                        floor = _floorSeq;
                    _manifestDirty = false;
                    _recordsSinceFlush = 0;
                    _lastFlushTicks = Environment.TickCount64;
                }
                finally
                {
                    _gate.Release();
                }

                var result = await _manifests
                    .FlushSegmentProgressAsync(pending, _sessionId, nextSeq, floor, ct)
                    .ConfigureAwait(false);
                if (!result.IsOk)
                {
                    _logger.LogWarning(
                        "Journal manifest flush failed: {Error}",
                        result.Error.Message);
                    await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        _manifestDirty = true;
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    return result;
                }

                lock (_allocLock)
                    _floorDirty = false;
            }
        }
        finally
        {
            _manifestFlushGate.Release();
        }
    }

    public async Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> ReadRangeAsync(
        long fromSeqExclusive,
        IReadOnlySet<EventClass>? classes,
        int budget,
        CancellationToken ct = default)
    {
        if (budget <= 0)
            return RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Ok([]);

        // A record is published live before its disk write. A subscriber that
        // registers in that window gets it from neither side unless replay waits.
        // Wait outside _gate: the commit worker holds _gate while it writes.
        await WaitForPendingCommitsAsync(ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_floorSeq > 0 && fromSeqExclusive + 1 < _floorSeq)
            {
                return RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Fail(
                    RuntimePersistenceError.CursorExpired(_floorSeq));
            }

            var list = await _manifests.ListSegmentsAsync(_sessionId, ct).ConfigureAwait(false);
            if (!list.IsOk)
                return RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Fail(list.Error);

            var results = new List<RuntimeEventRecord>(Math.Min(budget, 64));

            // Flush open stream so readers see latest bytes.
            _openStream?.Flush(flushToDisk: false);

            foreach (var seg in list.Value.OrderBy(s => s.FirstSeq)
                         .ThenBy(s => s.CreatedAt)
                         .ThenBy(s => s.RelativePath, StringComparer.Ordinal))
            {
                if (results.Count >= budget)
                    break;

                var isOpenSeg = _openSegmentId is not null &&
                    string.Equals(seg.SegmentId, _openSegmentId, StringComparison.Ordinal);

                // Skip only *closed* segments wholly at or below the exclusive cursor.
                // Never skip open/incomplete segments on SQLite LastSeq alone: a durable
                // HYJR write can advance the file (and in-memory _openLastSeq) before a
                // failed UpsertSegmentAsync, leaving last_seq stale. Reconnect/subscribe
                // must still see those fsynced records (design §10.2 cursor replay).
                if (seg.Closed && !isOpenSeg && seg.LastSeq is { } last && last <= fromSeqExclusive)
                    continue;

                var abs = Path.Combine(_paths.StateDirectory, seg.RelativePath);
                if (!File.Exists(abs))
                    continue;

                // If this is the open segment, read from the live path carefully.
                // Closed segments use read-only ScanFile — never RecoverFile (mutates).
                IEnumerable<RuntimeEventRecord> records;
                if (isOpenSeg)
                {
                    records = ReadOpenSegmentRecords();
                }
                else
                {
                    var scan = HyjrSegmentFormat.ScanFile(abs);
                    records = scan.Records;
                }

                foreach (var rec in records)
                {
                    if (rec.Seq <= fromSeqExclusive)
                        continue;
                    if (classes is { Count: > 0 } && !classes.Contains(rec.Class))
                        continue;
                    results.Add(rec);
                    if (results.Count >= budget)
                        break;
                }
            }

            // If the open segment is not yet in the SQLite list (upsert failed on create)
            // or ListSegments is stale, still scan the live open file.
            if (results.Count < budget &&
                _openSegmentId is not null &&
                _openRelativePath is not null &&
                list.Value.All(s => !string.Equals(s.SegmentId, _openSegmentId, StringComparison.Ordinal)))
            {
                foreach (var rec in ReadOpenSegmentRecords())
                {
                    if (rec.Seq <= fromSeqExclusive)
                        continue;
                    if (classes is { Count: > 0 } && !classes.Contains(rec.Class))
                        continue;
                    results.Add(rec);
                    if (results.Count >= budget)
                        break;
                }
            }

            return RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Ok(results);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Fail(
                RuntimePersistenceError.Io(ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeResult<RuntimeUnit>> CloseOpenSegmentAsync(CancellationToken ct = default)
    {
        var flushed = await FlushManifestAsync(ct).ConfigureAwait(false);
        if (!flushed.IsOk)
            return flushed;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CloseOpenSegmentCoreAsync(ct, durable: true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MarkPendingUnlocked()
    {
        if (_openSegmentId is null || _openRelativePath is null)
            return;

        _pendingManifest = new JournalSegmentManifest
        {
            SegmentId = _openSegmentId,
            SessionId = _sessionId,
            RelativePath = _openRelativePath,
            FirstSeq = _openFirstSeq,
            LastSeq = _openLastSeq,
            RecordCount = _openRecordCount,
            ByteCount = _openBytes,
            Closed = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _pendingNextSeq = _nextSeq;
        _manifestDirty = true;
    }

    private bool NoteOutputFlushUnlocked()
    {
        _recordsSinceFlush++;
        var countTrigger = _recordsSinceFlush >= _manifestFlushRecords;
        var intervalTrigger = _lastFlushTicks != 0
            && Environment.TickCount64 - _lastFlushTicks >= (long)_manifestFlushInterval.TotalMilliseconds;
        if (_lastFlushTicks == 0)
            _lastFlushTicks = Environment.TickCount64;
        if (!countTrigger)
            ArmManifestFlushTimerUnlocked();
        return countTrigger || intervalTrigger;
    }

    private void ArmManifestFlushTimerUnlocked()
    {
        if (_flushTimerArmed)
            return;
        _flushTimerArmed = true;
        var token = _flushTimerCts.Token;
        _ = FlushManifestAfterDelayAsync(token);
    }

    private async Task FlushManifestAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_manifestFlushInterval, token).ConfigureAwait(false);
            await FlushManifestAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Dispose cancelled the timer.
        }
        finally
        {
            try
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    _flushTimerArmed = false;
                    if (_manifestDirty)
                        ArmManifestFlushTimerUnlocked();
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (ObjectDisposedException)
            {
                // Gate already disposed.
            }
        }
    }

    /// <summary>
    /// Write the pending manifest and cursor while holding <see cref="_gate"/>.
    /// Used on the Reliable path and before rotation. Output batch flushes use
    /// <see cref="FlushManifestAsync"/> so they do not hold the journal gate.
    /// </summary>
    private async Task<RuntimeResult<RuntimeUnit>> FlushPendingDirectAsync(CancellationToken ct)
    {
        if (!_manifestDirty || _pendingManifest is null)
            return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);

        var pending = _pendingManifest;
        var nextSeq = _pendingNextSeq;
        long floor;
        lock (_allocLock)
            floor = _floorSeq;
        var result = await _manifests
            .FlushSegmentProgressAsync(pending, _sessionId, nextSeq, floor, ct)
            .ConfigureAwait(false);
        if (!result.IsOk)
            return result;

        lock (_allocLock)
            _floorDirty = false;
        _manifestDirty = false;
        _recordsSinceFlush = 0;
        _lastFlushTicks = Environment.TickCount64;
        return result;
    }

    /// <param name="durable">
    /// When true, sync file then directory after the footer. Output rotation
    /// passes false so the emit path does not wait on durable sync.
    /// </param>
    private async Task<RuntimeResult<RuntimeUnit>> CloseOpenSegmentCoreAsync(
        CancellationToken ct,
        bool durable)
    {
        if (_openStream is null || _openSegmentId is null)
            return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);

        try
        {
            if (_openSegmentUnusable)
            {
                _logger.LogWarning(
                    "Journal segment {SegmentId} has an uncounted tail; leaving it open without a footer",
                    _openSegmentId);
                AbandonOpenSegmentWithoutFooter();
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }

            // Flush the writer buffer so the footer digest covers all records.
            // Output rotation must not fsync here.
            _openStream.Flush(flushToDisk: false);
            var countedEnd = CountedOpenEnd();
            if (_openStream.Length != countedEnd)
            {
                _logger.LogWarning(
                    "Journal segment {SegmentId} length {Length} does not match counted bytes {Counted}; leaving it open without a footer",
                    _openSegmentId,
                    _openStream.Length,
                    countedEnd);
                AbandonOpenSegmentWithoutFooter();
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }

            _openStream.Position = 0;
            var headerAndRecords = new byte[_openStream.Length];
            _openStream.ReadExactly(headerAndRecords);

            _openStream.Position = _openStream.Length;
            var lastSeq = _openLastSeq ?? (_openFirstSeq > 0 ? _openFirstSeq - 1 : 0);
            HyjrSegmentFormat.WriteFooter(
                _openStream, lastSeq, _openRecordCount, headerAndRecords);
            _openStream.Flush(flushToDisk: false);
            if (durable)
                DurablySyncOpenFile();

            var abs = Path.Combine(_paths.StateDirectory, _openRelativePath!);
            _openStream.Dispose();
            _openStream = null;

            var checksum = HyjrSegmentFormat.ComputeFileChecksum(abs);
            var byteCount = new FileInfo(abs).Length;
            _totalBytes = _totalBytes - _openBytes + byteCount;
            // The footer is now counted. A later abandon must not count it again.
            _openBytes = byteCount;

            // The footer keeps first_seq - 1 for an empty segment. The manifest stores
            // NULL because journal_segments requires last_seq >= first_seq.
            var closed = await _manifests.CloseSegmentAsync(
                _openSegmentId,
                _openLastSeq,
                _openRecordCount,
                byteCount,
                checksum,
                DateTimeOffset.UtcNow,
                ct).ConfigureAwait(false);

            _openSegmentId = null;
            _openRelativePath = null;
            _openRecordCount = 0;
            _openBytes = 0;
            _openLastSeq = null;

            return closed;
        }
        catch (OperationCanceledException)
        {
            AbandonOpenSegmentWithoutFooter();
            throw;
        }
        catch (Exception ex)
        {
            // A partial footer must not stay under a live writer. Abandon the segment
            // so the next append rotates. Recovery truncates or seals the file.
            AbandonOpenSegmentWithoutFooter();
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
    }

    private async Task<RuntimeResult<RuntimeUnit>> OpenNewSegmentAsync(
        CancellationToken ct,
        bool durable,
        long? reservedSeq = null)
    {
        try
        {
            _paths.EnsureJournalDirectory();
            var segmentId = Guid.NewGuid().ToString("N")[..12];
            var firstSeq = reservedSeq ?? _nextSeq;
            var relative = $"{RuntimeStatePaths.JournalDirectoryName}/seg_{firstSeq}_{segmentId}.hyjr";
            var abs = Path.Combine(_paths.StateDirectory, relative);

            var fs = CreateSegmentStream(abs, FileMode.CreateNew);
            try
            {
                HyjrSegmentFormat.WriteHeader(fs, firstSeq);
                if (durable)
                    _durableSync.SyncFileThenDirectory(fs.SafeFileHandle, abs);
            }
            catch
            {
                await fs.DisposeAsync().ConfigureAwait(false);
                try { File.Delete(abs); } catch { /* ignore */ }
                throw;
            }

            var manifest = new JournalSegmentManifest
            {
                SegmentId = segmentId,
                SessionId = _sessionId,
                RelativePath = relative,
                FirstSeq = firstSeq,
                LastSeq = null,
                RecordCount = 0,
                ByteCount = HyjrSegmentFormat.HeaderSize,
                Closed = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var upsert = await _manifests.UpsertSegmentAsync(manifest, ct).ConfigureAwait(false);
            if (!upsert.IsOk)
            {
                await fs.DisposeAsync().ConfigureAwait(false);
                try { File.Delete(abs); } catch { /* ignore */ }
                return upsert;
            }

            _openStream = fs;
            _openSegmentId = segmentId;
            _openRelativePath = relative;
            _openFirstSeq = firstSeq;
            _openRecordCount = 0;
            _openBytes = HyjrSegmentFormat.HeaderSize;
            _openLastSeq = null;
            _totalBytes += HyjrSegmentFormat.HeaderSize;
            return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
    }

    private Task OpenExistingSegmentAsync(
        string segmentId, string relativePath, HyjrRecoveryResult recovery, CancellationToken ct)
    {
        _ = ct;
        // Never leave multiple FileStreams open: dispose any previous reopen candidate.
        if (_openStream is not null)
        {
            try { _openStream.Dispose(); }
            catch { /* ignore */ }
            _openStream = null;
        }

        var abs = Path.Combine(_paths.StateDirectory, relativePath);
        var fs = CreateSegmentStream(abs, FileMode.Open);
        fs.Position = fs.Length;

        _openStream = fs;
        _openSegmentId = segmentId;
        _openRelativePath = relativePath;
        _openFirstSeq = recovery.FirstSeq;
        _openRecordCount = recovery.RecordCount;
        _openBytes = recovery.Bytes;
        _openLastSeq = recovery.LastValidSeq;
        return Task.CompletedTask;
    }

    private static bool IsChronologicallyLast(
        JournalSegmentManifest seg,
        IReadOnlyList<JournalSegmentManifest> segments)
    {
        foreach (var other in segments)
        {
            if (CompareSegmentOrder(other, seg) > 0)
                return false;
        }

        return true;
    }

    private static bool HeaderIsUsable(HyjrRecoveryResult recovery) =>
        recovery.ReplayError is not "empty segment"
        && recovery.ReplayError?.StartsWith("header invalid", StringComparison.Ordinal) != true;

    /// <summary>
    /// A missing footer or an incomplete tail leaves a valid record prefix.
    /// Digest and structural failures do not.
    /// </summary>
    private static bool TailIsClean(HyjrRecoveryResult recovery)
    {
        // A partial footer from a crash during close is clean after RecoverFile truncates it.
        return recovery.ReplayError is "missing footer"
            or "footer truncated or missing"
            or "footer read incomplete"
            or "incomplete record_length"
            or "incomplete record body"
            or "incomplete record read";
    }

    private async Task UpsertOpenSegmentAsync(
        JournalSegmentManifest seg,
        HyjrRecoveryResult recovery,
        CancellationToken ct)
    {
        await _manifests.UpsertSegmentAsync(seg with
        {
            FirstSeq = recovery.FirstSeq,
            LastSeq = recovery.LastValidSeq,
            RecordCount = recovery.RecordCount,
            ByteCount = recovery.Bytes,
            Closed = false,
            ChecksumSha256 = null,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a footer and checksum for a recovered prefix and mark the manifest closed.
    /// </summary>
    private async Task<bool> TrySealRecoveredSegmentAsync(
        JournalSegmentManifest seg,
        HyjrRecoveryResult recovery,
        CancellationToken ct)
    {
        var abs = Path.Combine(_paths.StateDirectory, seg.RelativePath);
        try
        {
            using (var fs = new FileStream(abs, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (fs.Length != recovery.Bytes)
                    return false;
                var headerAndRecords = new byte[fs.Length];
                fs.ReadExactly(headerAndRecords);
                var lastSeq = recovery.LastValidSeq ?? (recovery.FirstSeq > 0 ? recovery.FirstSeq - 1 : 0);
                HyjrSegmentFormat.WriteFooter(fs, lastSeq, recovery.RecordCount, headerAndRecords);
                fs.Flush(flushToDisk: false);
                _durableSync.SyncFileThenDirectory(fs.SafeFileHandle, abs);
            }

            var checksum = HyjrSegmentFormat.ComputeFileChecksum(abs);
            var byteCount = new FileInfo(abs).Length;
            var closed = await _manifests.CloseSegmentAsync(
                seg.SegmentId,
                recovery.LastValidSeq,
                recovery.RecordCount,
                byteCount,
                checksum,
                DateTimeOffset.UtcNow,
                ct).ConfigureAwait(false);
            if (closed.IsOk)
                return true;
            _logger.LogWarning(
                "Journal could not close sealed segment {SegmentId}: {Error}",
                seg.SegmentId,
                closed.Error.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Journal could not seal segment {SegmentId}", seg.SegmentId);
        }

        RollBackSeal(abs, recovery.Bytes, seg.SegmentId);
        return false;
    }

    /// <summary>
    /// Truncate a sealed file back to its recovered record end so it matches the open
    /// manifest row. A failure leaves the footer; <see cref="SealLeftoverBytes"/> counts it.
    /// </summary>
    private void RollBackSeal(string abs, long recoveredBytes, string segmentId)
    {
        try
        {
            using var fs = new FileStream(abs, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (fs.Length <= recoveredBytes)
                return;
            fs.SetLength(recoveredBytes);
            _durableSync.SyncFileThenDirectory(fs.SafeFileHandle, abs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Journal could not roll back seal of segment {SegmentId}", segmentId);
        }
    }

    private static long SealLeftoverBytes(string abs, long recoveredBytes)
    {
        try
        {
            var extra = new FileInfo(abs).Length - recoveredBytes;
            return extra > 0 ? extra : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Order incomplete segments: higher FirstSeq wins; ties break on RelativePath ordinal.
    /// Positive when <paramref name="a"/> should replace <paramref name="b"/> as reopen candidate.
    /// </summary>
    private static int CompareSegmentOrder(JournalSegmentManifest a, JournalSegmentManifest b)
    {
        var bySeq = a.FirstSeq.CompareTo(b.FirstSeq);
        if (bySeq != 0)
            return bySeq;
        // Segment ids are random. Creation time orders segments that share a first seq.
        var byCreated = a.CreatedAt.CompareTo(b.CreatedAt);
        if (byCreated != 0)
            return byCreated;
        return string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal);
    }

    private List<RuntimeEventRecord> ReadOpenSegmentRecords()
    {
        if (_openStream is null)
            return [];

        var abs = Path.Combine(_paths.StateDirectory, _openRelativePath!);
        // Read via a separate stream so we do not disturb the writer position.
        using var read = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (read.Length < HyjrSegmentFormat.HeaderSize)
            return [];

        HyjrSegmentFormat.ReadHeader(read, out _, out _);
        var records = new List<RuntimeEventRecord>();
        Span<byte> magicPeek = stackalloc byte[4];
        while (read.Position < read.Length)
        {
            // Stop before footer if present.
            if (read.Length - read.Position >= 4)
            {
                var peek = read.Position;
                read.ReadExactly(magicPeek);
                read.Position = peek;
                if (magicPeek.SequenceEqual(HyjrSegmentFormat.FooterMagic))
                    break;
            }

            if (!HyjrSegmentFormat.TryReadRecord(read, out var rec, out _))
                break;
            records.Add(rec!);
        }

        return records;
    }

    private void DurablySyncOpenFile()
    {
        if (_openStream is null || string.IsNullOrEmpty(_openRelativePath))
            return;
        var abs = Path.Combine(_paths.StateDirectory, _openRelativePath);
        _durableSync.SyncFileThenDirectory(_openStream.SafeFileHandle, abs);
    }

    private static RuntimePersistenceError? LiveOnlyRefusal(
        EventClass cls, EventReliability reliability, string type)
    {
        if (cls == EventClass.Output
            || reliability == EventReliability.Output
            || string.Equals(type, ProtocolEventTypes.TerminalOutput, StringComparison.Ordinal))
        {
            return RuntimePersistenceError.Io(
                "terminal.output is live-only and must not be written to HYJR");
        }

        return null;
    }

    /// <summary>
    /// Compact, then refuse a reliable append that is still over the budget.
    /// Returns null when the append may write. Caller holds the journal gate.
    /// </summary>
    private async Task<RuntimeResult<RuntimeEventRecord>?> EnsureWritableAsync(
        EventClass cls,
        EventReliability reliability,
        string type,
        string payloadJson,
        DateTimeOffset? occurredAt,
        CancellationToken ct) =>
        await RefuseIfOverBudgetAsync(cls, reliability, type, payloadJson, occurredAt, ct)
            .ConfigureAwait(false);

    private async Task<RuntimeResult<RuntimeEventRecord>?> RefuseIfOverBudgetAsync(
        EventClass cls,
        EventReliability reliability,
        string type,
        string payloadJson,
        DateTimeOffset? occurredAt,
        CancellationToken ct)
    {
        var pending = PendingFrameBytes(
            type, occurredAt ?? _time.GetUtcNow(), payloadJson, NeedsRotation());
        await CompactIfOverAsync(ct, pending).ConfigureAwait(false);
        if (BudgetRefusal(reliability, pending) is not { } refused)
            return null;
        if (reliability != EventReliability.Reliable)
            return RuntimeResult<RuntimeEventRecord>.Fail(refused);
        return await RefuseReliableGapAsync(cls, type, payloadJson, occurredAt, refused.Message, ct)
            .ConfigureAwait(false);
    }

    private async Task<RuntimeResult<RuntimeEventRecord>> RefuseReliableGapAsync(
        EventClass cls,
        string type,
        string payloadJson,
        DateTimeOffset? occurredAt,
        string reason,
        CancellationToken ct)
    {
        long seq;
        long floor;
        lock (_allocLock)
        {
            seq = _nextSeq;
            _nextSeq = seq + 1;
            floor = seq + 1;
            if (floor > _floorSeq)
                _floorSeq = floor;
        }

        var record = new RuntimeEventRecord
        {
            Seq = seq,
            Class = cls,
            Reliability = EventReliability.Reliable,
            Type = type,
            OccurredAt = occurredAt ?? _time.GetUtcNow(),
            PayloadJson = payloadJson,
        };

        var raised = await _manifests.RaiseRetentionFloorAsync(_sessionId, _floorSeq, _nextSeq, ct)
            .ConfigureAwait(false);
        if (!raised.IsOk)
        {
            lock (_allocLock)
                _floorDirty = true;
            _logger.LogWarning(
                "Journal could not persist retention floor after seq {Seq}: {Error}",
                seq, raised.Error.Message);
        }

        NoteRefusal(type, reason);
        return RuntimeResult<RuntimeEventRecord>.Fail(RuntimePersistenceError.Budget(reason, record));
    }

    private void NoteRefusal(string eventType, string reason)
    {
        _refusalCount++;
        _lastRefusalReason = reason;
        _refusalsSinceLog++;
        var now = _time.GetUtcNow();
        if (_refusalLogged && now - _lastRefusalLogAt < RefusalLogInterval)
            return;

        var count = _refusalsSinceLog;
        _refusalsSinceLog = 0;
        _lastRefusalLogAt = now;
        _refusalLogged = true;
        _processLog.Write(new ProcessLogRecord
        {
            Event = ProcessLogEvents.JournalRefused,
            Subsystem = ProcessLogEvents.SubsystemMux,
            Outcome = ProcessLogEvents.OutcomeRejected,
            Ts = now,
            Pid = Environment.ProcessId,
            Level = ProcessLogLevel.Warning,
            SessionId = _sessionId,
            EventType = eventType,
            Reason = reason,
            DropCount = count,
        });
    }

    /// <summary>Compact until the journal plus <paramref name="pendingBytes"/> fits the budget.</summary>
    private async Task CompactIfOverAsync(CancellationToken ct, long pendingBytes = 0)
    {
        if (_totalBytes + pendingBytes <= _maxJournalBytes)
            return;

        var list = await _manifests.ListSegmentsAsync(_sessionId, ct).ConfigureAwait(false);
        if (!list.IsOk)
        {
            _logger.LogWarning("Journal compaction skipped: {Error}", list.Error.Message);
            return;
        }

        var segments = list.Value.ToList();
        await CompactListedAsync(segments, ct, pendingBytes).ConfigureAwait(false);
    }

    private async Task CompactListedAsync(
        List<JournalSegmentManifest> segments, CancellationToken ct, long pendingBytes = 0)
    {
        var limit = _maxJournalBytes - pendingBytes;
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;
        foreach (var seg in segments)
        {
            var len = SegmentByteLength(seg);
            sizes[seg.SegmentId] = len;
            total += len;
        }

        // _totalBytes also holds bytes that no manifest row lists (the open tail,
        // an undeleted file), so the larger value is the real disk use.
        if (_totalBytes > total)
            total = _totalBytes;

        if (total <= limit && _totalBytes <= limit)
        {
            RaiseFloorToOldest(segments);
            return;
        }

        var holdResult = await _retention.ReadHoldAsync(_sessionId, ct).ConfigureAwait(false);
        if (!holdResult.IsOk)
        {
            _logger.LogWarning("Journal compaction skipped: {Error}", holdResult.Error.Message);
            RaiseFloorToOldest(segments);
            return;
        }

        var ordered = segments
            .Where(s => s.Closed && !string.Equals(s.SegmentId, _openSegmentId, StringComparison.Ordinal))
            .OrderBy(s => s.FirstSeq)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.RelativePath, StringComparer.Ordinal)
            .ToList();

        foreach (var seg in ordered)
        {
            if (total <= limit && _totalBytes <= limit)
                break;
            if (!CanDeleteSegment(seg, holdResult.Value))
                break;

            var nextFloor = FloorAfterRemove(seg, segments);
            var deleted = await _manifests.DeleteClosedSegmentAsync(
                seg.SegmentId, _sessionId, nextFloor, ct).ConfigureAwait(false);
            if (!deleted.IsOk)
            {
                _logger.LogWarning(
                    "Journal could not delete segment {SegmentId}: {Error}",
                    seg.SegmentId, deleted.Error.Message);
                break;
            }

            lock (_allocLock)
            {
                if (nextFloor > _floorSeq)
                    _floorSeq = nextFloor;
            }

            var abs = Path.Combine(_paths.StateDirectory, seg.RelativePath);
            try
            {
                if (File.Exists(abs))
                    File.Delete(abs);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file still uses disk: keep its bytes in the budget and stop.
                // Recovery deletes it as an orphan below the floor.
                _logger.LogWarning(
                    ex,
                    "Journal manifest deleted; file remains for recovery {Path}",
                    seg.RelativePath);
                break;
            }

            var removed = sizes.TryGetValue(seg.SegmentId, out var n) ? n : seg.ByteCount;
            total -= removed;
            _totalBytes -= removed;
            if (_totalBytes < 0)
                _totalBytes = 0;
            segments.RemoveAll(s => string.Equals(s.SegmentId, seg.SegmentId, StringComparison.Ordinal));
        }

        RaiseFloorToOldest(segments);
    }

    private static bool CanDeleteSegment(JournalSegmentManifest seg, JournalRetentionHold hold)
    {
        if (!seg.Closed || seg.LastSeq is not long last)
            return false;
        if (hold.LowestExportAck is long ack && last > ack)
            return false;
        if (hold.BarrierSeq is long barrier && last >= barrier)
            return false;
        return true;
    }

    private long FloorAfterRemove(
        JournalSegmentManifest removing, IReadOnlyList<JournalSegmentManifest> segments)
    {
        long? oldest = null;
        foreach (var seg in segments)
        {
            if (string.Equals(seg.SegmentId, removing.SegmentId, StringComparison.Ordinal))
                continue;
            if (seg.FirstSeq <= 0)
                continue;
            if (oldest is null || seg.FirstSeq < oldest)
                oldest = seg.FirstSeq;
        }

        if (oldest is { } next)
            return Math.Max(_floorSeq, next);

        var after = (removing.LastSeq ?? removing.FirstSeq) + 1;
        return Math.Max(_floorSeq, after);
    }

    private void RaiseFloorToOldest(IReadOnlyList<JournalSegmentManifest> segments)
    {
        long? oldest = null;
        foreach (var seg in segments)
        {
            if (seg.FirstSeq <= 0)
                continue;
            if (oldest is null || seg.FirstSeq < oldest)
                oldest = seg.FirstSeq;
        }

        lock (_allocLock)
        {
            if (oldest is { } value && value > _floorSeq)
                _floorSeq = value;
        }
    }

    private bool OrphanFileIsBelowFloor(string absolutePath)
    {
        if (_floorSeq <= 0)
            return false;
        try
        {
            using var stream = new FileStream(
                absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var first = HyjrSegmentFormat.ReadHeader(stream, out _, out _);
            return first < _floorSeq;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private long SegmentByteLength(JournalSegmentManifest seg)
    {
        var abs = Path.Combine(_paths.StateDirectory, seg.RelativePath);
        try
        {
            if (File.Exists(abs))
                return new FileInfo(abs).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Use the manifest count.
        }

        return seg.ByteCount < 0 ? 0 : seg.ByteCount;
    }

    /// <summary>Returns the refusal when the journal budget blocks this append.</summary>
    private RuntimePersistenceError? BudgetRefusal(EventReliability reliability, int pendingBytes = 0)
    {
        if (!OverBudget(pendingBytes))
            return null;
        return reliability switch
        {
            EventReliability.Reliable => RuntimePersistenceError.Io(
                $"Journal budget exceeded ({_maxJournalBytes} bytes); refusing reliable intake"),
            EventReliability.Output => RuntimePersistenceError.Io(
                "Journal budget exceeded; output not journaled"),
            _ => null,
        };
    }

    /// <summary>
    /// Header plus the bytes of records whose in-memory counters have advanced.
    /// </summary>
    private long CountedOpenEnd()
    {
        var recordBytes = _openBytes - HyjrSegmentFormat.HeaderSize;
        if (recordBytes < 0)
            recordBytes = 0;
        return HyjrSegmentFormat.HeaderSize + recordBytes;
    }

    /// <summary>
    /// Write one record and sync it. If that fails before the counters advance,
    /// truncate the stream back to its previous length so the next seq is not
    /// already on disk.
    /// </summary>
    private RuntimeResult<int> WriteRecordAndSync(
        long seq,
        EventClass cls,
        EventReliability reliability,
        ReadOnlySpan<byte> storedPayloadUtf8)
    {
        var stream = _openStream!;
        var lengthBefore = stream.Length;
        try
        {
            var written = HyjrSegmentFormat.WriteRecord(
                stream, seq, cls, reliability, storedPayloadUtf8);
            if (reliability == EventReliability.Reliable)
                DurablySyncOpenFile();
            else
                stream.Flush(flushToDisk: false);
            return RuntimeResult<int>.Ok(written);
        }
        catch (Exception ex)
        {
            if (!TryDiscardUncountedTail(stream, lengthBefore))
                MarkOpenSegmentUnusable(seq);
            if (ex is OperationCanceledException)
                throw;
            return RuntimeResult<int>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
    }

    private static bool TryDiscardUncountedTail(FileStream stream, long lengthBefore)
    {
        try
        {
            stream.SetLength(lengthBefore);
            stream.Position = lengthBefore;
            return stream.Length == lengthBefore;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The assigned seq may already be in the file. Do not hand it out again.
    /// </summary>
    private void MarkOpenSegmentUnusable(long assignedSeq)
    {
        _openSegmentUnusable = true;
        var next = assignedSeq + 1;
        lock (_allocLock)
        {
            if (next > _nextSeq)
                _nextSeq = next;
        }
    }

    private FileStream CreateSegmentStream(string abs, FileMode mode)
    {
        if (_segmentStreamFactory is { } factory)
            return factory(abs, mode);
        return new FileStream(
            abs, mode, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous);
    }

    /// <summary>
    /// Drop the writer without a footer or checksum. The manifest row stays open
    /// so startup scans the file. Move the allocator past any complete record left behind.
    /// Replace this segment's counted bytes with the file's on-disk length.
    /// </summary>
    private void AbandonOpenSegmentWithoutFooter()
    {
        var segmentId = _openSegmentId;
        var relative = _openRelativePath;
        var countedOpen = _openBytes;
        if (_openStream is not null)
        {
            try
            {
                _openStream.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.LogWarning(ex, "Journal failed to dispose segment {SegmentId}", segmentId);
            }

            _openStream = null;
        }

        _openSegmentId = null;
        _openRelativePath = null;
        _openRecordCount = 0;
        _openBytes = 0;
        _openLastSeq = null;
        _openSegmentUnusable = false;

        if (string.IsNullOrEmpty(relative))
        {
            ReplaceCountedOpenBytes(countedOpen, 0);
            return;
        }

        var abs = Path.Combine(_paths.StateDirectory, relative);
        try
        {
            if (!File.Exists(abs))
            {
                ReplaceCountedOpenBytes(countedOpen, 0);
                return;
            }

            ReplaceCountedOpenBytes(countedOpen, new FileInfo(abs).Length);
            var scan = HyjrSegmentFormat.ScanFile(abs);
            lock (_allocLock)
            {
                if (scan.LastValidSeq is { } last && last + 1 > _nextSeq)
                    _nextSeq = last + 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Journal could not scan abandoned segment {SegmentId}", segmentId);
        }
    }

    /// <summary>
    /// <paramref name="countedOpen"/> is already inside <see cref="_totalBytes"/>.
    /// Swap it for the abandoned file length so an uncounted tail is counted once.
    /// </summary>
    private void ReplaceCountedOpenBytes(long countedOpen, long onDiskLength)
    {
        var next = _totalBytes - countedOpen + onDiskLength;
        _totalBytes = next < 0 ? 0 : next;
    }

    private static string TruncateUtf8(string s, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= maxBytes)
            return s;
        // Walk back to a char boundary.
        var len = maxBytes;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80)
            len--;
        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    private static int TruncateUtf8Length(ReadOnlySpan<byte> bytes, int maxBytes)
    {
        if (bytes.Length <= maxBytes)
            return bytes.Length;
        var len = maxBytes;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80)
            len--;
        return len;
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_allocLock)
        {
            _stopping = true;
            _commitQueue.Writer.TryComplete();
        }
        if (_commitWorker is not null)
        {
            try
            {
                await _commitWorker.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The queued write already recorded a gap.
            }
        }

        try
        {
            _flushTimerCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already cancelled
        }

        try
        {
            await FlushManifestAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }

        // Every reserve is written or gapped now, so the lease can shrink to the
        // exact next sequence. A crash skips this and the next start skips ahead.
        if (_recovered)
        {
            try
            {
                if (_floorDirty)
                    await PersistFloorAsync(CancellationToken.None).ConfigureAwait(false);
                long next;
                lock (_allocLock)
                    next = _nextSeq;
                await _manifests.WriteEventCursorAsync(_sessionId, next, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_openStream is not null)
            {
                try
                {
                    await CloseOpenSegmentCoreAsync(CancellationToken.None, durable: true)
                        .ConfigureAwait(false);
                }
                catch
                {
                    try { _openStream.Dispose(); } catch { /* ignore */ }
                    _openStream = null;
                }
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _manifestFlushGate.Dispose();
            _flushTimerCts.Dispose();
        }
    }

    private sealed class ReservedCommit
    {
        public required RuntimeEventRecord Record { get; init; }

        public TaskCompletionSource<RuntimeResult<RuntimeUnit>> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
