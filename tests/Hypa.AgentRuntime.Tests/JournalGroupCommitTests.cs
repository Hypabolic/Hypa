using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The commit worker takes every queued reliable record and commits them as one
/// group: one file sync, one directory sync and one manifest transaction.
/// Each test holds the sync of a first record, so the next records queue behind it.
/// </summary>
public sealed class JournalGroupCommitTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public JournalGroupCommitTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-group-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task Queued_records_commit_with_one_file_sync_one_directory_sync_and_one_manifest_transaction()
    {
        var native = new HoldingNativeSync();
        var store = new CountingManifestStore(new SqliteJournalManifestStore(_paths));
        var journal = await OpenAsync("rs_group", native, store);
        // The segment header and the first record sync before the count starts.
        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable, ProtocolEventTypes.PaneLifecycle, "{\"n\":0}");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);

        var phasesBefore = native.Phases.Count;
        var writesBefore = store.Writes;
        var flushesBefore = store.ProgressFlushes;
        native.HoldNextFileSync();
        var first = Reserve(journal, 1);
        await native.Held.WaitAsync(Poll.HangGuard);
        var queued = Enumerable.Range(2, 5).Select(n => Reserve(journal, n)).ToList();
        native.ReleaseHeld();

        var all = new[] { first }.Concat(queued).ToList();
        foreach (var record in all)
        {
            var committed = await journal.CommitReservedAsync(record).WaitAsync(Poll.HangGuard);
            Assert.True(committed.IsOk, committed.IsOk ? null : committed.Error.Message);
        }

        // The first record had its own group. The five queued records shared one.
        Assert.Equal(
            new[] { "file", "directory", "file", "directory" },
            native.Phases.Skip(phasesBefore).ToArray());
        Assert.Equal(2, store.Writes - writesBefore);
        Assert.Equal(2, store.ProgressFlushes - flushesBefore);

        var expected = new[] { seed.Value.Seq }.Concat(all.Select(r => r.Seq)).ToList();
        var live = await journal.ReadRangeAsync(0, classes: null, budget: 50);
        Assert.True(live.IsOk, live.IsOk ? null : live.Error.Message);
        Assert.Equal(expected, live.Value.Select(r => r.Seq).ToList());
        Assert.Equal(
            Enumerable.Range(0, 7).Select(n => "{\"n\":" + n + "}"),
            live.Value.Select(r => r.PayloadJson));
        await journal.DisposeAsync();

        await using var reopened = await OpenAsync("rs_group");
        var replay = await reopened.ReadRangeAsync(0, classes: null, budget: 50);
        Assert.True(replay.IsOk, replay.IsOk ? null : replay.Error.Message);
        Assert.Equal(expected, replay.Value.Select(r => r.Seq).ToList());
        Assert.Equal(expected[^1] + 1, reopened.NextSeq);
        Assert.True(reopened.GetHealth().ReplayComplete);
    }

    [Fact]
    public async Task Health_reports_the_commit_queue_while_a_sync_is_held_and_counts_each_commit()
    {
        var native = new HoldingNativeSync();
        var journal = await OpenAsync("rs_group_health", native);
        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable, ProtocolEventTypes.PaneLifecycle, "{\"n\":0}");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);
        var idle = journal.GetHealth();
        Assert.Equal(0, idle.CommitQueueDepth);
        Assert.Equal(1, idle.CommittedReliable);

        native.HoldNextFileSync();
        var first = Reserve(journal, 1);
        await native.Held.WaitAsync(Poll.HangGuard);
        var queued = Enumerable.Range(2, 5).Select(n => Reserve(journal, n)).ToList();

        // The held record and the five records behind it all wait on the disk.
        var held = journal.GetHealth();
        Assert.Equal(6, held.CommitQueueDepth);
        Assert.Equal(1, held.CommittedReliable);

        native.ReleaseHeld();
        foreach (var record in new[] { first }.Concat(queued))
        {
            var committed = await journal.CommitReservedAsync(record).WaitAsync(Poll.HangGuard);
            Assert.True(committed.IsOk, committed.IsOk ? null : committed.Error.Message);
        }

        var drained = journal.GetHealth();
        Assert.Equal(0, drained.CommitQueueDepth);
        Assert.Equal(7, drained.CommittedReliable);
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_group_sync_fails_every_record_of_the_group()
    {
        var native = new HoldingNativeSync();
        var journal = await OpenAsync("rs_group_fail", native);
        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable, ProtocolEventTypes.PaneLifecycle, "{\"n\":0}");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);

        native.HoldNextFileSync();
        var first = Reserve(journal, 1);
        await native.Held.WaitAsync(Poll.HangGuard);
        var group = Enumerable.Range(2, 5).Select(n => Reserve(journal, n)).ToList();
        // The held sync of the first record completes. The group sync after it fails.
        native.FailFileSyncAfterHeld = new IOException("injected group sync failure");
        native.ReleaseHeld();

        var firstResult = await journal.CommitReservedAsync(first).WaitAsync(Poll.HangGuard);
        Assert.True(firstResult.IsOk, firstResult.IsOk ? null : firstResult.Error.Message);
        foreach (var record in group)
        {
            var result = await journal.CommitReservedAsync(record).WaitAsync(Poll.HangGuard);
            Assert.False(result.IsOk);
            Assert.Contains("injected group sync failure", result.Error.Message, StringComparison.Ordinal);
        }

        var health = journal.GetHealth();
        Assert.Equal(group[^1].Seq + 1, health.FloorSeq);
        Assert.Equal(5, health.RefusalCount);
        // A failed record is not a commit. Only the seed and the first record count.
        Assert.Equal(2, health.CommittedReliable);
        Assert.Equal(0, health.CommitQueueDepth);

        // The worker goes on, and the next record commits after the gap.
        var next = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable, ProtocolEventTypes.PaneLifecycle, "{\"n\":7}");
        Assert.True(next.IsOk, next.IsOk ? null : next.Error.Message);
        Assert.True(next.Value.Seq > group[^1].Seq);
        await journal.DisposeAsync();

        // No record of the failed group is in a segment file.
        var groupSeqs = group.Select(r => r.Seq).ToHashSet();
        var onDisk = Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr")
            .SelectMany(f => HyjrSegmentFormat.ScanFile(f).Records)
            .Select(r => r.Seq)
            .ToList();
        Assert.DoesNotContain(onDisk, groupSeqs.Contains);
        Assert.Contains(first.Seq, onDisk);
        Assert.Contains(next.Value.Seq, onDisk);

        await using var reopened = await OpenAsync("rs_group_fail");
        Assert.True(reopened.GetHealth().FloorSeq > group[^1].Seq);
        var replay = await reopened.ReadRangeAsync(group[^1].Seq, classes: null, budget: 50);
        Assert.True(replay.IsOk, replay.IsOk ? null : replay.Error.Message);
        Assert.Equal(new[] { next.Value.Seq }, replay.Value.Select(r => r.Seq).ToArray());
    }

    [Fact]
    public async Task A_group_that_crosses_a_segment_rotation_commits_every_record_in_order()
    {
        var native = new HoldingNativeSync();
        var journal = await OpenAsync("rs_group_rot", native, rotateRecords: 3);
        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable, ProtocolEventTypes.PaneLifecycle, "{\"n\":0}");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);

        native.HoldNextFileSync();
        var first = Reserve(journal, 1);
        await native.Held.WaitAsync(Poll.HangGuard);
        var queued = Enumerable.Range(2, 8).Select(n => Reserve(journal, n)).ToList();
        native.ReleaseHeld();

        var all = new[] { first }.Concat(queued).ToList();
        foreach (var record in all)
        {
            var committed = await journal.CommitReservedAsync(record).WaitAsync(Poll.HangGuard);
            Assert.True(committed.IsOk, committed.IsOk ? null : committed.Error.Message);
        }

        var expected = new[] { seed.Value.Seq }.Concat(all.Select(r => r.Seq)).ToList();
        var live = await journal.ReadRangeAsync(0, classes: null, budget: 50);
        Assert.True(live.IsOk, live.IsOk ? null : live.Error.Message);
        Assert.Equal(expected, live.Value.Select(r => r.Seq).ToList());
        await journal.DisposeAsync();

        Assert.True(Directory.GetFiles(_paths.JournalDirectory, "*.hyjr").Length >= 3);
        foreach (var file in Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr"))
            Assert.True(HyjrSegmentFormat.ScanFile(file).Records.Count <= 3);

        await using var reopened = await OpenAsync("rs_group_rot", rotateRecords: 3);
        Assert.True(reopened.GetHealth().ReplayComplete);
        var replay = await reopened.ReadRangeAsync(0, classes: null, budget: 50);
        Assert.True(replay.IsOk, replay.IsOk ? null : replay.Error.Message);
        Assert.Equal(expected, replay.Value.Select(r => r.Seq).ToList());
        Assert.Equal(expected[^1] + 1, reopened.NextSeq);
    }

    [Fact]
    public async Task A_budget_refusal_inside_a_group_commits_the_records_before_it()
    {
        var native = new HoldingNativeSync();
        // Room for the header, the footer and a few small records only.
        var journal = await OpenAsync("rs_group_budget", native, maxJournalBytes: 1200);
        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable, ProtocolEventTypes.PaneLifecycle, "{\"n\":0}");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);

        native.HoldNextFileSync();
        var first = Reserve(journal, 1);
        await native.Held.WaitAsync(Poll.HangGuard);
        var queued = Enumerable.Range(2, 12).Select(n => Reserve(journal, n)).ToList();
        native.ReleaseHeld();

        var results = new List<RuntimeResult<RuntimeUnit>>();
        foreach (var record in new[] { first }.Concat(queued))
            results.Add(await journal.CommitReservedAsync(record).WaitAsync(Poll.HangGuard));

        // A run of commits, then refusals: a refusal never lands between two commits.
        var firstRefused = results.FindIndex(r => !r.IsOk);
        Assert.True(firstRefused > 1, "the first two records fit the budget");
        Assert.All(results.Skip(firstRefused), r => Assert.False(r.IsOk));
        Assert.All(results.Skip(firstRefused), r => Assert.Contains("budget", r.Error.Message, StringComparison.Ordinal));
        Assert.True(journal.GetHealth().Bytes <= 1200);

        // Each committed record is on disk; no refused record is.
        await journal.DisposeAsync();
        var onDisk = Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr")
            .SelectMany(f => HyjrSegmentFormat.ScanFile(f).Records)
            .Select(r => r.Seq)
            .ToHashSet();
        var all = new[] { first }.Concat(queued).ToList();
        for (var i = 0; i < all.Count; i++)
            Assert.Equal(results[i].IsOk, onDisk.Contains(all[i].Seq));
    }

    [Fact]
    public async Task Dispose_drains_a_backlog_with_one_sync_for_each_batch()
    {
        const int backlog = 1500;
        var native = new HoldingNativeSync();
        var journal = await OpenAsync("rs_group_drain", native);

        native.HoldNextFileSync();
        var first = Reserve(journal, 0);
        await native.Held.WaitAsync(Poll.HangGuard);
        var queued = Enumerable.Range(1, backlog).Select(n => Reserve(journal, n)).ToList();
        var phasesBefore = native.Phases.Count;
        native.ReleaseHeld();

        // The mux disposes the journal at shutdown. Dispose waits for the backlog.
        await journal.DisposeAsync().AsTask().WaitAsync(Poll.HangGuard);

        // The held sync is the header of the first segment. After it: the group of
        // the first record, the backlog in batches, and the footer sync of the close.
        // One record at a time needs a sync for each record.
        var batches = (backlog + FileRuntimeEventJournal.MaxCommitBatchRecords - 1)
            / FileRuntimeEventJournal.MaxCommitBatchRecords;
        var fileSyncs = native.Phases.Skip(phasesBefore).Count(p => p == "file");
        Assert.Equal(3, batches);
        Assert.Equal(1 + batches + 1, fileSyncs);

        await using var reopened = await OpenAsync("rs_group_drain");
        var replay = await reopened.ReadRangeAsync(0, classes: null, budget: backlog + 10);
        Assert.True(replay.IsOk, replay.IsOk ? null : replay.Error.Message);
        Assert.Equal(
            new[] { first }.Concat(queued).Select(r => r.Seq).ToList(),
            replay.Value.Select(r => r.Seq).ToList());
        Assert.Equal(0, reopened.GetHealth().RefusalCount);
    }

    private static RuntimeEventRecord Reserve(FileRuntimeEventJournal journal, int n)
    {
        var record = journal.ReserveReliable(
            EventClass.Lifecycle, ProtocolEventTypes.PaneLifecycle, "{\"n\":" + n + "}", DateTimeOffset.UtcNow);
        Assert.NotNull(record);
        return record;
    }

    private async Task<FileRuntimeEventJournal> OpenAsync(
        string sessionId,
        HoldingNativeSync? native = null,
        IJournalManifestStore? store = null,
        int? rotateRecords = null,
        long? maxJournalBytes = null)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT OR IGNORE INTO sessions(
                  session_id, name, lifecycle_state, placement, placement_generation,
                  started_at, updated_at, replay_complete)
                VALUES ('{sessionId}', 'j', 'ready', 'local', 0,
                  '2026-10-17T00:00:00Z', '2026-10-17T00:00:00Z', 1);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var journal = new FileRuntimeEventJournal(
            _paths,
            store ?? new SqliteJournalManifestStore(_paths),
            sessionId,
            durableSync: native is null ? null : new DurableFileSync(native),
            rotateRecords: rotateRecords,
            maxJournalBytes: maxJournalBytes);
        var recovered = await journal.RecoverAsync();
        Assert.True(recovered.IsOk, recovered.IsOk ? null : recovered.Error.Message);
        return journal;
    }

    /// <summary>
    /// Records each sync phase. When armed, the next file sync waits until the
    /// test releases it, and the file sync after that can fail once.
    /// </summary>
    private sealed class HoldingNativeSync : IDurableNativeSync
    {
        private readonly object _gate = new();
        private readonly List<string> _phases = [];
        private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _holdNext;
        private bool _afterHeld;

        public string FileSyncKind => "test";

        public Task Held => _held.Task;

        public Exception? FailFileSyncAfterHeld { get; set; }

        public IReadOnlyList<string> Phases
        {
            get
            {
                lock (_gate)
                    return _phases.ToArray();
            }
        }

        public void HoldNextFileSync()
        {
            lock (_gate)
            {
                _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _holdNext = true;
            }
        }

        public void ReleaseHeld() => _release.TrySetResult();

        public void SyncHandle(SafeFileHandle handle)
        {
            Task? wait = null;
            Exception? fail = null;
            lock (_gate)
            {
                _phases.Add("file");
                if (_holdNext)
                {
                    _holdNext = false;
                    _afterHeld = true;
                    _held.TrySetResult();
                    wait = _release.Task;
                }
                else if (_afterHeld)
                {
                    _afterHeld = false;
                    fail = FailFileSyncAfterHeld;
                }
            }

            wait?.Wait(Poll.HangGuard);
            if (fail is not null)
                throw fail;
        }

        public void SyncDirectory(string directoryPath)
        {
            lock (_gate)
                _phases.Add("directory");
        }
    }

    /// <summary>Counts the manifest writes that each commit makes.</summary>
    private sealed class CountingManifestStore(IJournalManifestStore inner) : IJournalManifestStore
    {
        private int _writes;
        private int _progressFlushes;

        public int Writes => Volatile.Read(ref _writes);

        public int ProgressFlushes => Volatile.Read(ref _progressFlushes);

        public Task<RuntimeResult<RuntimeUnit>> UpsertSegmentAsync(
            JournalSegmentManifest segment, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.UpsertSegmentAsync(segment, ct);
        }

        public Task<RuntimeResult<IReadOnlyList<JournalSegmentManifest>>> ListSegmentsAsync(
            string sessionId, CancellationToken ct = default) =>
            inner.ListSegmentsAsync(sessionId, ct);

        public Task<RuntimeResult<JournalSegmentManifest?>> GetOpenSegmentAsync(
            string sessionId, CancellationToken ct = default) =>
            inner.GetOpenSegmentAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> CloseSegmentAsync(
            string segmentId,
            long? lastSeq,
            int recordCount,
            long byteCount,
            string checksumSha256,
            DateTimeOffset closedAt,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.CloseSegmentAsync(
                segmentId, lastSeq, recordCount, byteCount, checksumSha256, closedAt, ct);
        }

        public Task<RuntimeResult<long>> ReadEventCursorAsync(
            string sessionId, CancellationToken ct = default) =>
            inner.ReadEventCursorAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> WriteEventCursorAsync(
            string sessionId, long nextSeq, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.WriteEventCursorAsync(sessionId, nextSeq, ct);
        }

        public Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
            JournalSegmentManifest segment,
            string sessionId,
            long nextSeq,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writes);
            Interlocked.Increment(ref _progressFlushes);
            return inner.FlushSegmentProgressAsync(segment, sessionId, nextSeq, ct);
        }

        public Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
            JournalSegmentManifest segment,
            string sessionId,
            long nextSeq,
            long floorSeq,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writes);
            Interlocked.Increment(ref _progressFlushes);
            return inner.FlushSegmentProgressAsync(segment, sessionId, nextSeq, floorSeq, ct);
        }

        public Task<RuntimeResult<long>> ReadRetentionFloorAsync(
            string sessionId, CancellationToken ct = default) =>
            inner.ReadRetentionFloorAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> RaiseRetentionFloorAsync(
            string sessionId, long floorSeq, long nextSeq, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.RaiseRetentionFloorAsync(sessionId, floorSeq, nextSeq, ct);
        }

        public Task<RuntimeResult<RuntimeUnit>> DeleteClosedSegmentAsync(
            string segmentId, string sessionId, long floorSeq, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.DeleteClosedSegmentAsync(segmentId, sessionId, floorSeq, ct);
        }
    }
}
