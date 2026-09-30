using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class JournalManifestStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public JournalManifestStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h03-manifest-" + Guid.NewGuid().ToString("N"));
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

    private async Task PrepareAsync()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        var m = await migrator.MigrateAsync();
        Assert.True(m.IsOk, m.IsOk ? null : m.Error.Message);

        // sessions row required for FK on journal_segments / event_cursor.
        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO sessions(
              session_id, name, lifecycle_state, placement, placement_generation,
              started_at, updated_at, replay_complete)
            VALUES ('rs_test', 'test', 'ready', 'local', 0, '2026-08-12T00:00:00Z', '2026-08-12T00:00:00Z', 1);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Closed_segment_checksum_equals_full_file_sha256_including_footer()
    {
        await PrepareAsync();
        var store = new SqliteJournalManifestStore(_paths);
        _paths.EnsureJournalDirectory();

        var rel = "journal/seg_1_abc.hyjr";
        var abs = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        using (var fs = File.Create(abs))
        {
            HyjrSegmentFormat.WriteHeader(fs, firstSeq: 1);
            var rec = new RuntimeEventRecord
            {
                Seq = 1,
                Class = EventClass.Lifecycle,
                Reliability = EventReliability.Reliable,
                Type = "pane.lifecycle",
                OccurredAt = DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
                PayloadJson = HyjrSegmentFormat.WrapStoredPayload(
                    "pane.lifecycle",
                    DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
                    """{"pane_id":"p1"}"""),
            };
            HyjrSegmentFormat.WriteRecord(fs, rec);
            fs.Flush();
            fs.Position = 0;
            var body = new byte[fs.Length];
            fs.ReadExactly(body);
            fs.Position = fs.Length;
            HyjrSegmentFormat.WriteFooter(fs, lastSeq: 1, recordCount: 1, body);
        }

        var checksum = HyjrSegmentFormat.ComputeFileChecksum(abs);
        var byteCount = new FileInfo(abs).Length;

        var open = await store.UpsertSegmentAsync(new JournalSegmentManifest
        {
            SegmentId = "abc",
            SessionId = "rs_test",
            RelativePath = rel,
            FirstSeq = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            Closed = false,
        });
        Assert.True(open.IsOk);

        var close = await store.CloseSegmentAsync(
            "abc", lastSeq: 1, recordCount: 1, byteCount, checksum, DateTimeOffset.UtcNow);
        Assert.True(close.IsOk, close.IsOk ? null : close.Error.Message);

        var list = await store.ListSegmentsAsync("rs_test");
        Assert.True(list.IsOk);
        Assert.Single(list.Value);
        var seg = list.Value[0];
        Assert.True(seg.Closed);
        Assert.Equal(checksum, seg.ChecksumSha256);
        Assert.Equal(byteCount, seg.ByteCount);
        Assert.Equal(1, seg.LastSeq);
    }

    [Fact]
    public async Task Event_cursor_round_trips()
    {
        await PrepareAsync();
        var store = new SqliteJournalManifestStore(_paths);

        var w = await store.WriteEventCursorAsync("rs_test", 42);
        Assert.True(w.IsOk);
        var r = await store.ReadEventCursorAsync("rs_test");
        Assert.True(r.IsOk);
        Assert.Equal(42, r.Value);
    }

    [Fact]
    public async Task Flush_segment_progress_writes_the_row_and_the_cursor()
    {
        await PrepareAsync();
        var store = new SqliteJournalManifestStore(_paths);
        var flush = await store.FlushSegmentProgressAsync(
            new JournalSegmentManifest
            {
                SegmentId = "seg_progress",
                SessionId = "rs_test",
                RelativePath = "journal/seg_1_progress.hyjr",
                FirstSeq = 1,
                LastSeq = 7,
                RecordCount = 7,
                ByteCount = 128,
                Closed = false,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            "rs_test",
            nextSeq: 8);
        Assert.True(flush.IsOk, flush.IsOk ? null : flush.Error.Message);

        var list = await store.ListSegmentsAsync("rs_test");
        Assert.True(list.IsOk);
        var seg = Assert.Single(list.Value);
        Assert.Equal(7, seg.LastSeq);
        Assert.Equal(7, seg.RecordCount);
        var cursor = await store.ReadEventCursorAsync("rs_test");
        Assert.True(cursor.IsOk);
        Assert.Equal(8, cursor.Value);
    }

    [Fact]
    public async Task Flush_segment_progress_opens_one_connection()
    {
        await PrepareAsync();
        var store = new SqliteJournalManifestStore(_paths);
        var before = store.ConnectionsOpened;
        var flush = await store.FlushSegmentProgressAsync(
            new JournalSegmentManifest
            {
                SegmentId = "seg_one_conn",
                SessionId = "rs_test",
                RelativePath = "journal/seg_1_one.hyjr",
                FirstSeq = 1,
                LastSeq = 2,
                RecordCount = 2,
                ByteCount = 64,
                Closed = false,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            "rs_test",
            nextSeq: 3);
        Assert.True(flush.IsOk, flush.IsOk ? null : flush.Error.Message);
        Assert.Equal(1, store.ConnectionsOpened - before);
    }
}
