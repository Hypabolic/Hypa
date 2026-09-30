using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class JournalOutputBatchTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public JournalOutputBatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-jout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        try
        {
            SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public async Task Output_appends_do_not_flush_the_manifest()
    {
        var counting = await PrepareCountingAsync("rs_batch_count");
        await using var journal = counting.Journal;
        var payload = OutputPayloadUtf8("p1");
        var before = journal.NextSeq;
        for (var i = 0; i < 32; i++)
        {
            var append = await journal.AppendUtf8Async(
                EventClass.Output, EventReliability.Output,
                "terminal.output", payload);
            Assert.False(append.IsOk);
        }

        await journal.FlushManifestAsync();
        Assert.Equal(0, counting.Store.FlushCount);
        Assert.Equal(before, journal.NextSeq);
        var range = await journal.ReadRangeAsync(0, classes: null, budget: 50);
        Assert.True(range.IsOk);
        Assert.Empty(range.Value);
    }

    [Fact]
    public async Task Output_appends_do_not_write_the_cursor()
    {
        var counting = await PrepareCountingAsync("rs_batch_cursor");
        await using var journal = counting.Journal;
        counting.Store.Reset();
        var payload = OutputPayloadUtf8("p1");
        for (var i = 0; i < 8; i++)
        {
            var append = await journal.AppendUtf8Async(
                EventClass.Output, EventReliability.Output,
                "terminal.output", payload);
            Assert.False(append.IsOk);
        }

        await journal.FlushManifestAsync();
        Assert.Equal(0, counting.Store.CursorWrites);
        Assert.Equal(0, counting.Store.FlushCount);
        Assert.Equal(1, journal.NextSeq);
    }

    [Fact]
    public async Task Manifest_flush_does_not_hold_the_journal_gate()
    {
        var blocking = new BlockingFlushManifestStore();
        await using var journal = await PrepareJournalAsync(
            "rs_batch_gate",
            blocking,
            manifestFlushRecords: 1,
            manifestFlushInterval: TimeSpan.FromHours(1));

        var payload = OutputPayloadUtf8("p1");
        try
        {
            var first = await journal.AppendUtf8Async(
                EventClass.Output, EventReliability.Output, "terminal.output", payload);
            Assert.False(first.IsOk);
            Assert.False(
                blocking.Entered.Wait(TimeSpan.FromMilliseconds(50)),
                "a live-only output append must not enter the manifest store");
            Assert.Equal(1, journal.NextSeq);
        }
        finally
        {
            blocking.Release.Set();
        }

        await journal.FlushManifestAsync();
    }

    [Fact]
    public async Task Segment_rotation_flushes_the_manifest_first()
    {
        await using var journal = await PrepareJournalAsync(
            "rs_batch_rot",
            rotateRecords: 2);
        RuntimeEventRecord? last = null;
        for (var i = 0; i < 3; i++)
        {
            var append = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                "pane.lifecycle", $$"""{"pane_id":"p{{i}}"}""");
            Assert.True(append.IsOk, append.IsOk ? null : append.Error.Message);
            last = append.Value;
        }

        await journal.FlushManifestAsync();
        await journal.CloseOpenSegmentAsync();
        var store = new SqliteJournalManifestStore(_paths);
        var list = await store.ListSegmentsAsync("rs_batch_rot");
        Assert.True(list.IsOk);
        Assert.True(list.Value.Count >= 2);
        var closed = list.Value.Where(s => s.Closed).OrderBy(s => s.FirstSeq).First();
        Assert.Equal(2, closed.RecordCount);
        Assert.Equal(2, closed.LastSeq);
        Assert.NotNull(last);
    }

    [Fact]
    public async Task Stop_flushes_the_manifest()
    {
        var sessionId = "rs_batch_stop";
        await using (var journal = await PrepareJournalAsync(sessionId, manifestFlushRecords: 10_000))
        {
            for (var i = 0; i < 8; i++)
            {
                var append = await journal.AppendAsync(
                    EventClass.Lifecycle, EventReliability.Reliable,
                    "pane.lifecycle", $$"""{"pane_id":"p{{i}}"}""");
                Assert.True(append.IsOk, append.IsOk ? null : append.Error.Message);
            }

            var flushed = await journal.FlushManifestAsync();
            Assert.True(flushed.IsOk, flushed.IsOk ? null : flushed.Error.Message);
            Assert.Equal(9, journal.NextSeq);
        }

        var store = new SqliteJournalManifestStore(_paths);
        var cursor = await store.ReadEventCursorAsync(sessionId);
        Assert.True(cursor.IsOk);
        Assert.Equal(9, cursor.Value);
    }

    [Fact]
    public async Task Reliable_append_still_writes_the_manifest_before_it_returns()
    {
        var inner = await PrepareStoreAsync("rs_batch_rel");
        var failing = new FailingFlushManifestStore(inner, failAfterSuccessfulFlushes: 0);
        await using var journal = new FileRuntimeEventJournal(_paths, failing, "rs_batch_rel");
        Assert.True((await journal.RecoverAsync()).IsOk);

        var append = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.False(append.IsOk, "Reliable append must fail closed when the store fails");
    }

    [Fact]
    public async Task Byte_cap_still_refuses_a_reliable_append()
    {
        await using var journal = await PrepareJournalAsync(
            "rs_batch_cap",
            maxJournalBytes: 1);
        // The budget counts the pending record, so even the first one is refused.
        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.False(seed.IsOk);

        var refused = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p2"}""");
        Assert.False(refused.IsOk);
        Assert.Equal(
            "Journal budget exceeded (1 bytes); refusing reliable intake",
            refused.Error.Message);
        Assert.NotNull(refused.Error.LiveRecord);
        Assert.True(refused.Error.LiveRecord!.Seq > seed.Error.LiveRecord!.Seq);
    }

    [Fact]
    public async Task Byte_rotation_still_fires_at_the_same_count()
    {
        await using var journal = await PrepareJournalAsync(
            "rs_batch_bytrot",
            rotateBytes: 64);
        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);

        var rotated = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", "{\"pane_id\":\"" + new string('x', 180) + "\"}");
        Assert.True(rotated.IsOk, rotated.IsOk ? null : rotated.Error.Message);
        Assert.True(
            Directory.GetFiles(_paths.JournalDirectory, "*.hyjr").Length >= 2,
            "byte threshold must close the open segment and open a new one");
    }

    private async Task<(SqliteJournalManifestStore Inner, CountingManifestStore Store, FileRuntimeEventJournal Journal)>
        PrepareCountingAsync(string sessionId)
    {
        var inner = await PrepareStoreAsync(sessionId);
        var counting = new CountingManifestStore(inner);
        var journal = new FileRuntimeEventJournal(
            _paths,
            counting,
            sessionId,
            manifestFlushRecords: 512,
            manifestFlushInterval: TimeSpan.FromHours(1));
        Assert.True((await journal.RecoverAsync()).IsOk);
        counting.Reset();
        return (inner, counting, journal);
    }

    private async Task<FileRuntimeEventJournal> PrepareJournalAsync(
        string sessionId,
        IJournalManifestStore? manifests = null,
        int? rotateRecords = null,
        long? rotateBytes = null,
        int? manifestFlushRecords = null,
        TimeSpan? manifestFlushInterval = null,
        long? maxJournalBytes = null)
    {
        manifests ??= await PrepareStoreAsync(sessionId);
        var journal = new FileRuntimeEventJournal(
            _paths,
            manifests,
            sessionId,
            rotateBytes: rotateBytes,
            rotateRecords: rotateRecords,
            manifestFlushRecords: manifestFlushRecords,
            manifestFlushInterval: manifestFlushInterval,
            maxJournalBytes: maxJournalBytes);
        Assert.True((await journal.RecoverAsync()).IsOk);
        return journal;
    }

    private async Task<SqliteJournalManifestStore> PrepareStoreAsync(string sessionId)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT OR IGNORE INTO sessions(
              session_id, name, lifecycle_state, placement, placement_generation,
              started_at, updated_at, replay_complete)
            VALUES ('{sessionId}', 'j', 'ready', 'local', 0, '2026-08-12T00:00:00Z', '2026-08-12T00:00:00Z', 1);
            """;
        await cmd.ExecuteNonQueryAsync();
        return new SqliteJournalManifestStore(_paths);
    }

    private static ReadOnlyMemory<byte> OutputPayloadUtf8(string paneId)
    {
        using var buffer = new PooledUtf8Buffer(64);
        Assert.True(TerminalOutputPayloadWriter.TryWriteTerminalOutput(buffer, paneId, "A"u8));
        return buffer.WrittenSpan.ToArray();
    }

    private sealed class CountingManifestStore : IJournalManifestStore
    {
        private readonly IJournalManifestStore _inner;

        public CountingManifestStore(IJournalManifestStore inner) => _inner = inner;

        public int FlushCount;
        public int CursorWrites;
        public int FlushUpsertFallback;

        public void Reset()
        {
            FlushCount = 0;
            CursorWrites = 0;
            FlushUpsertFallback = 0;
        }

        public Task<RuntimeResult<RuntimeUnit>> UpsertSegmentAsync(
            JournalSegmentManifest segment, CancellationToken ct = default)
        {
            // New-segment upserts have RecordCount 0. Progress upserts must not.
            if (segment.RecordCount > 0 || segment.LastSeq is not null)
                Interlocked.Increment(ref FlushUpsertFallback);
            return _inner.UpsertSegmentAsync(segment, ct);
        }

        public Task<RuntimeResult<IReadOnlyList<JournalSegmentManifest>>> ListSegmentsAsync(
            string sessionId, CancellationToken ct = default) =>
            _inner.ListSegmentsAsync(sessionId, ct);

        public Task<RuntimeResult<JournalSegmentManifest?>> GetOpenSegmentAsync(
            string sessionId, CancellationToken ct = default) =>
            _inner.GetOpenSegmentAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> CloseSegmentAsync(
            string segmentId, long? lastSeq, int recordCount, long byteCount,
            string checksumSha256, DateTimeOffset closedAt, CancellationToken ct = default) =>
            _inner.CloseSegmentAsync(
                segmentId, lastSeq, recordCount, byteCount, checksumSha256, closedAt, ct);

        public Task<RuntimeResult<long>> ReadEventCursorAsync(
            string sessionId, CancellationToken ct = default) =>
            _inner.ReadEventCursorAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> WriteEventCursorAsync(
            string sessionId, long nextSeq, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CursorWrites);
            return _inner.WriteEventCursorAsync(sessionId, nextSeq, ct);
        }

        public Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
            JournalSegmentManifest segment,
            string sessionId,
            long nextSeq,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref FlushCount);
            return _inner.FlushSegmentProgressAsync(segment, sessionId, nextSeq, ct);
        }
    }

    private sealed class BlockingFlushManifestStore : IJournalManifestStore
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public Task<RuntimeResult<RuntimeUnit>> UpsertSegmentAsync(
            JournalSegmentManifest segment, CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value));

        public Task<RuntimeResult<IReadOnlyList<JournalSegmentManifest>>> ListSegmentsAsync(
            string sessionId, CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<IReadOnlyList<JournalSegmentManifest>>.Ok(
                Array.Empty<JournalSegmentManifest>()));

        public Task<RuntimeResult<JournalSegmentManifest?>> GetOpenSegmentAsync(
            string sessionId, CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<JournalSegmentManifest?>.Ok(null));

        public Task<RuntimeResult<RuntimeUnit>> CloseSegmentAsync(
            string segmentId, long? lastSeq, int recordCount, long byteCount,
            string checksumSha256, DateTimeOffset closedAt, CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value));

        public Task<RuntimeResult<long>> ReadEventCursorAsync(
            string sessionId, CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<long>.Ok(0));

        public Task<RuntimeResult<RuntimeUnit>> WriteEventCursorAsync(
            string sessionId, long nextSeq, CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value));

        public Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
            JournalSegmentManifest segment,
            string sessionId,
            long nextSeq,
            CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("manifest flush was not released");
            }

            return Task.FromResult(RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value));
        }

        private int _blocked;
    }

    private sealed class FailingFlushManifestStore : IJournalManifestStore
    {
        private readonly IJournalManifestStore _inner;
        private readonly int _failAfter;
        private int _flushes;

        public FailingFlushManifestStore(IJournalManifestStore inner, int failAfterSuccessfulFlushes)
        {
            _inner = inner;
            _failAfter = failAfterSuccessfulFlushes;
        }

        public Task<RuntimeResult<RuntimeUnit>> UpsertSegmentAsync(
            JournalSegmentManifest segment, CancellationToken ct = default) =>
            _inner.UpsertSegmentAsync(segment, ct);

        public Task<RuntimeResult<IReadOnlyList<JournalSegmentManifest>>> ListSegmentsAsync(
            string sessionId, CancellationToken ct = default) =>
            _inner.ListSegmentsAsync(sessionId, ct);

        public Task<RuntimeResult<JournalSegmentManifest?>> GetOpenSegmentAsync(
            string sessionId, CancellationToken ct = default) =>
            _inner.GetOpenSegmentAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> CloseSegmentAsync(
            string segmentId, long? lastSeq, int recordCount, long byteCount,
            string checksumSha256, DateTimeOffset closedAt, CancellationToken ct = default) =>
            _inner.CloseSegmentAsync(
                segmentId, lastSeq, recordCount, byteCount, checksumSha256, closedAt, ct);

        public Task<RuntimeResult<long>> ReadEventCursorAsync(
            string sessionId, CancellationToken ct = default) =>
            _inner.ReadEventCursorAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> WriteEventCursorAsync(
            string sessionId, long nextSeq, CancellationToken ct = default) =>
            _inner.WriteEventCursorAsync(sessionId, nextSeq, ct);

        public Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
            JournalSegmentManifest segment,
            string sessionId,
            long nextSeq,
            CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _flushes);
            if (n > _failAfter)
            {
                return Task.FromResult(RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Io("injected flush failure")));
            }

            return _inner.FlushSegmentProgressAsync(segment, sessionId, nextSeq, ct);
        }
    }
}
