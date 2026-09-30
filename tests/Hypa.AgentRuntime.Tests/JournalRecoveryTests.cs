using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class JournalRecoveryTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public JournalRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h03-jrecover-" + Guid.NewGuid().ToString("N"));
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

    private async Task<(SqliteJournalManifestStore store, FileRuntimeEventJournal journal)> PrepareAsync(
        string sessionId = "rs_j",
        long? rotateBytes = null,
        int? rotateRecords = null,
        IDurableFileSync? durableSync = null,
        long? maxJournalBytes = null)
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

        var store = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(
            _paths,
            store,
            sessionId,
            durableSync: durableSync,
            rotateBytes: rotateBytes,
            rotateRecords: rotateRecords,
            maxJournalBytes: maxJournalBytes);
        return (store, journal);
    }

    [Fact]
    public async Task RecoverAsync_after_crash_mid_record_restores_next_seq_and_replay_complete_false()
    {
        var sessionId = "rs_crash";
        var (store, journal) = await PrepareAsync(sessionId);

        var health0 = await journal.RecoverAsync();
        Assert.True(health0.IsOk);
        Assert.True(health0.Value.ReplayComplete);

        var a1 = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","state":"running"}""");
        Assert.True(a1.IsOk, a1.IsOk ? null : a1.Error.Message);
        var a2 = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","state":"exited"}""");
        Assert.True(a2.IsOk);

        // Capture open segment path before a clean close would write the footer.
        var open = await store.GetOpenSegmentAsync(sessionId);
        Assert.True(open.IsOk);
        Assert.NotNull(open.Value);
        var abs = Path.Combine(_dir, open.Value!.RelativePath);
        Assert.True(File.Exists(abs));

        // Snapshot the durable open file (header + complete records, no footer).
        var snapshot = await File.ReadAllBytesAsync(abs);

        // Release file locks via dispose (which closes with footer — discard that state).
        await journal.DisposeAsync();

        // Restore the pre-footer crash image and append an incomplete record tail.
        await File.WriteAllBytesAsync(abs, snapshot);
        await using (var fs = new FileStream(abs, FileMode.Append, FileAccess.Write))
        {
            Span<byte> bad = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bad, 500);
            fs.Write(bad);
            fs.Write(new byte[] { 9, 9, 9 });
        }

        // Mark segment open again in SQLite (dispose closed it).
        await store.UpsertSegmentAsync(open.Value! with
        {
            Closed = false,
            ChecksumSha256 = null,
            ClosedAt = null,
        });

        // New journal instance recovers.
        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.False(health.Value.ReplayComplete);
        Assert.Equal(3, health.Value.NextSeq); // seqs 1,2 assigned → next is 3
        Assert.False(string.IsNullOrEmpty(health.Value.ReplayError));

        var range = await journal2.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 100);
        Assert.True(range.IsOk);
        Assert.Equal(2, range.Value.Count);
        Assert.Equal(1, range.Value[0].Seq);
        Assert.Equal(2, range.Value[1].Seq);

        await journal2.DisposeAsync();
    }

    [Fact]
    public async Task Append_reliable_then_ReadRangeAsync_returns_monotonic_seq_payloads()
    {
        var (_, journal) = await PrepareAsync("rs_mono");
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.Equal(1, journal.NextSeq); // 1-based allocator

        for (var i = 0; i < 5; i++)
        {
            var r = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                "pane.lifecycle", $$"""{"pane_id":"p1","n":{{i}}}""");
            Assert.True(r.IsOk, r.IsOk ? null : r.Error.Message);
            Assert.Equal(i + 1, r.Value.Seq); // seqs 1..5
        }

        var range = await journal.ReadRangeAsync(fromSeqExclusive: 1, classes: null, budget: 10);
        Assert.True(range.IsOk);
        Assert.Equal(4, range.Value.Count); // seqs 2,3,4,5
        Assert.Equal(2, range.Value[0].Seq);
        Assert.Equal(5, range.Value[^1].Seq);
        Assert.Contains("\"n\":4", range.Value[^1].PayloadJson, StringComparison.Ordinal);

        var output = await journal.AppendAsync(
            EventClass.Output, EventReliability.Output,
            "terminal.output", """{"pane_id":"p1","encoding":"base64","data":"YQ==","byte_count":1}""");
        Assert.False(output.IsOk);
        Assert.Equal(6, journal.NextSeq);

        var lifeOnly = await journal.ReadRangeAsync(
            fromSeqExclusive: 0,
            classes: new HashSet<EventClass> { EventClass.Lifecycle },
            budget: 100);
        Assert.True(lifeOnly.IsOk);
        Assert.All(lifeOnly.Value, r => Assert.Equal(EventClass.Lifecycle, r.Class));
        Assert.Equal(5, lifeOnly.Value.Count);
        Assert.Equal(1, lifeOnly.Value[0].Seq);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_reopens_only_highest_incomplete_segment()
    {
        var sessionId = "rs_multi_open";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);

        // Write a few events, force-close segment, write more (second segment).
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""")).IsOk);

        var list = await store.ListSegmentsAsync(sessionId);
        Assert.True(list.IsOk);
        Assert.Equal(2, list.Value.Count);

        // Snapshot both files without footers (simulate multi incomplete).
        var segs = list.Value.OrderBy(s => s.FirstSeq).ToList();
        var abs0 = Path.Combine(_dir, segs[0].RelativePath);
        var abs1 = Path.Combine(_dir, segs[1].RelativePath);
        // Dispose closes open with footer — restore both as open incomplete.
        var snap0 = await File.ReadAllBytesAsync(abs0);
        // Second still open; read before dispose.
        await journal.DisposeAsync();
        // After dispose seg1 has footer. Strip footers / mark both open.
        // Rebuild: write two incomplete files with known first_seq.
        // Use recover path by re-opening second without footer.
        if (File.Exists(abs1))
        {
            var bytes1 = await File.ReadAllBytesAsync(abs1);
            // Truncate footer if present (FooterSize = 4+8+4+32 = 48).
            if (bytes1.Length > HyjrSegmentFormat.FooterSize)
            {
                // If closed with footer, strip it.
                var withoutFooter = bytes1.AsSpan(0, bytes1.Length - HyjrSegmentFormat.FooterSize).ToArray();
                // Only strip if magic ENDJ at end-48.
                if (bytes1.Length >= 4 &&
                    System.Text.Encoding.ASCII.GetString(bytes1, bytes1.Length - HyjrSegmentFormat.FooterSize, 4) == "ENDJ")
                {
                    await File.WriteAllBytesAsync(abs1, withoutFooter);
                }
            }
        }

        // Force first segment open without footer too (dispose closed it).
        // Re-read closed file and strip footer.
        if (File.Exists(abs0))
        {
            var bytes0 = await File.ReadAllBytesAsync(abs0);
            if (bytes0.Length >= HyjrSegmentFormat.FooterSize &&
                System.Text.Encoding.ASCII.GetString(bytes0, bytes0.Length - HyjrSegmentFormat.FooterSize, 4) == "ENDJ")
            {
                await File.WriteAllBytesAsync(abs0, bytes0.AsSpan(0, bytes0.Length - HyjrSegmentFormat.FooterSize).ToArray());
            }
        }

        foreach (var s in segs)
        {
            await store.UpsertSegmentAsync(s with
            {
                Closed = false,
                ChecksumSha256 = null,
                ClosedAt = null,
            });
        }

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.False(health.Value.ReplayComplete);

        // Further appends must succeed (single open stream, no multi-open leak).
        var a = await journal2.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":3}""");
        Assert.True(a.IsOk, a.IsOk ? null : a.Error.Message);

        await journal2.DisposeAsync();
        _ = snap0;
    }

    [Fact]
    public async Task ReadRangeAsync_does_not_mutate_closed_segment_bytes_or_length()
    {
        var sessionId = "rs_readonly";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);

        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);

        var list = await store.ListSegmentsAsync(sessionId);
        Assert.True(list.IsOk);
        Assert.Single(list.Value);
        Assert.True(list.Value[0].Closed);
        var abs = Path.Combine(_dir, list.Value[0].RelativePath);
        var before = await File.ReadAllBytesAsync(abs);
        var beforeLen = before.Length;

        // Corrupt footer digest bytes without changing length (simulates bit-rot).
        // RecoverFile would truncate; ScanFile/ReadRange must not.
        var corrupted = (byte[])before.Clone();
        // Footer is last FooterSize bytes; flip a digest byte near the end.
        corrupted[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(abs, corrupted);

        var range = await journal.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 100);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        // Records before footer are still readable; footer failure does not destroy them.
        Assert.Equal(2, range.Value.Count);

        var after = await File.ReadAllBytesAsync(abs);
        Assert.Equal(beforeLen, after.Length);
        Assert.True(corrupted.AsSpan().SequenceEqual(after),
            "ReadRangeAsync must not change closed-segment bytes (no RecoverFile truncate)");

        // Startup recovery still mutates via RecoverFile when appropriate.
        await journal.DisposeAsync();
        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk);
        // After recovery, file may be truncated to last valid record (footer invalid).
        var recoveredLen = new FileInfo(abs).Length;
        Assert.True(recoveredLen <= beforeLen);
        await journal2.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_closed_rotated_segments_preserve_bytes_and_replay()
    {
        const int recordCount = 6;
        var sessionId = "rs_closed_rot";
        var (store, journal) = await PrepareAsync(
            sessionId, rotateBytes: 256, rotateRecords: 2);
        Assert.True((await journal.RecoverAsync()).IsOk);

        for (var i = 0; i < recordCount; i++)
        {
            var appended = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                "pane.lifecycle", $$"""{"pane_id":"p1","n":{{i}}}""");
            Assert.True(appended.IsOk, appended.IsOk ? null : appended.Error.Message);
            Assert.Equal(i + 1, appended.Value.Seq);
        }

        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();

        var list = await store.ListSegmentsAsync(sessionId);
        Assert.True(list.IsOk, list.IsOk ? null : list.Error.Message);
        Assert.True(list.Value.Count >= 3, "rotation must close several segments");
        Assert.All(list.Value, seg =>
        {
            Assert.True(seg.Closed);
            Assert.False(string.IsNullOrEmpty(seg.ChecksumSha256));
        });

        var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long byteSum = 0;
        foreach (var seg in list.Value)
        {
            var abs = Path.Combine(_dir, seg.RelativePath);
            var bytes = await File.ReadAllBytesAsync(abs);
            before[abs] = bytes;
            byteSum += bytes.Length;
        }

        var journal2 = new FileRuntimeEventJournal(
            _paths, store, sessionId, rotateBytes: 256, rotateRecords: 2);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.True(health.Value.ReplayComplete);
        Assert.Null(health.Value.ReplayError);
        Assert.Equal(recordCount + 1, health.Value.NextSeq);
        Assert.Equal(recordCount + 1, journal2.NextSeq);
        Assert.Equal(byteSum, health.Value.Bytes);

        foreach (var (abs, bytes) in before)
        {
            var after = await File.ReadAllBytesAsync(abs);
            Assert.Equal(bytes.Length, after.Length);
            Assert.True(bytes.AsSpan().SequenceEqual(after),
                "closed segment bytes changed during recovery");
        }

        var range = await journal2.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 100);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.Equal(recordCount, range.Value.Count);
        for (var i = 0; i < recordCount; i++)
        {
            Assert.Equal(i + 1, range.Value[i].Seq);
            Assert.Contains($"\"n\":{i}", range.Value[i].PayloadJson, StringComparison.Ordinal);
        }

        await journal2.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_flipped_byte_in_closed_segment_is_incomplete()
    {
        var sessionId = "rs_flip_closed";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);

        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();

        var list = await store.ListSegmentsAsync(sessionId);
        Assert.True(list.IsOk, list.IsOk ? null : list.Error.Message);
        var seg = Assert.Single(list.Value);
        Assert.True(seg.Closed);
        Assert.False(string.IsNullOrEmpty(seg.ChecksumSha256));

        var abs = Path.Combine(_dir, seg.RelativePath);
        var bytes = await File.ReadAllBytesAsync(abs);
        var originalLength = bytes.Length;
        // Payload of the first record sits after the header, length prefix, and fixed body.
        var flipAt = HyjrSegmentFormat.HeaderSize + 4 + HyjrSegmentFormat.RecordFixedBodySize + 4;
        Assert.True(flipAt < originalLength - HyjrSegmentFormat.FooterSize);
        bytes[flipAt] ^= 0xFF;
        await File.WriteAllBytesAsync(abs, bytes);
        Assert.Equal(originalLength, new FileInfo(abs).Length);

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.False(health.Value.ReplayComplete);

        // Full recovery truncates the damaged tail. The checksum fast path does not.
        var recoveredLength = new FileInfo(abs).Length;
        Assert.True(recoveredLength < originalLength);
        await journal2.DisposeAsync();
    }

    [Fact]
    public async Task Sync_failure_does_not_leave_a_duplicate_sequence()
    {
        var sessionId = "rs_sync_fail";
        var sync = new ThrowingDurableFileSync(DurableFileSync.Production);
        var (store, journal) = await PrepareAsync(sessionId, durableSync: sync);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var first = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""");
        Assert.True(first.IsOk, first.IsOk ? null : first.Error.Message);

        sync.ThrowOnNext = true;
        var failed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""");
        Assert.False(failed.IsOk);

        var retry = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":3}""");
        Assert.True(retry.IsOk, retry.IsOk ? null : retry.Error.Message);
        // The failed record may already be live: its sequence is used up.
        var gap = failed.Error.LiveRecord!.Seq;
        Assert.Equal(first.Value.Seq + 1, gap);
        Assert.Equal(gap + 1, retry.Value.Seq);

        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.True(health.Value.ReplayComplete);

        // A resume from before the gap would miss the gapped event, so it expires.
        var expired = await journal2.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 100);
        Assert.False(expired.IsOk);
        Assert.Equal(gap + 1, health.Value.FloorSeq);
        var range = await journal2.ReadRangeAsync(
            fromSeqExclusive: health.Value.FloorSeq - 1, classes: null, budget: 100);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.Equal(new[] { retry.Value.Seq }, range.Value.Select(r => r.Seq));
        Assert.Contains("\"n\":3", range.Value[0].PayloadJson, StringComparison.Ordinal);
        for (var i = 1; i < range.Value.Count; i++)
            Assert.True(range.Value[i].Seq > range.Value[i - 1].Seq);
        Assert.True(journal2.NextSeq > range.Value.Max(r => r.Seq));
        Assert.Equal(journal2.NextSeq, health.Value.NextSeq);

        await journal2.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_closed_segment_verifies_under_shared_read()
    {
        var sessionId = "rs_shared_read";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();

        var list = await store.ListSegmentsAsync(sessionId);
        Assert.True(list.IsOk, list.IsOk ? null : list.Error.Message);
        var seg = Assert.Single(list.Value);
        Assert.True(seg.Closed);
        var abs = Path.Combine(_dir, seg.RelativePath);
        var writtenAt = File.GetLastWriteTimeUtc(abs);

        // RecoverFile opens ReadWrite with FileShare.None. A shared reader blocks that
        // on the mux hosts. The checksum path opens FileAccess.Read, FileShare.Read.
        await using var shared = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<IOException>(() =>
        {
            using var exclusive = new FileStream(
                abs, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.True(health.Value.ReplayComplete);
        Assert.Null(health.Value.ReplayError);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(abs));
        var after = await File.ReadAllBytesAsync(abs);
        Assert.Equal(seg.ByteCount, after.Length);

        shared.Dispose();
        var range = await journal2.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 10);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.Single(range.Value);
        Assert.Equal(1, range.Value[0].Seq);
        await journal2.DisposeAsync();
    }

    [Fact]
    public void TryVerifyClosedFile_accepts_uppercase_checksum_and_rejects_drift()
    {
        var path = Path.Combine(_dir, "verify_closed.hyjr");
        WriteClosedSegment(path, recordCount: 1, firstSeq: 1);
        var length = new FileInfo(path).Length;
        var checksum = HyjrSegmentFormat.ComputeFileChecksum(path);

        Assert.True(HyjrSegmentFormat.TryVerifyClosedFile(path, checksum, length, out var ok));
        Assert.Equal(1, ok.FirstSeq);
        Assert.Equal(1, ok.LastValidSeq);
        Assert.Equal(1, ok.RecordCount);
        Assert.True(ok.Closed);
        Assert.False(ok.Truncated);
        Assert.True(ok.ReplayComplete);
        Assert.Null(ok.ReplayError);
        Assert.Equal(length, ok.Bytes);
        Assert.Empty(ok.Records);

        Assert.True(HyjrSegmentFormat.TryVerifyClosedFile(
            path, checksum.ToUpperInvariant(), length, out var upper));
        Assert.Equal(ok.LastValidSeq, upper.LastValidSeq);

        Assert.False(HyjrSegmentFormat.TryVerifyClosedFile(path, checksum, length + 1, out _));
        Assert.False(HyjrSegmentFormat.TryVerifyClosedFile(path, checksum, 0, out _));
        Assert.False(HyjrSegmentFormat.TryVerifyClosedFile(path, checksum, -1, out _));

        var wrongChecksum = checksum[..^1] + (checksum[^1] == 'a' ? "b" : "a");
        Assert.False(HyjrSegmentFormat.TryVerifyClosedFile(path, wrongChecksum, length, out _));

        var intact = File.ReadAllBytes(path);
        var withTail = new byte[intact.Length + 1];
        intact.CopyTo(withTail, 0);
        var tailPath = Path.Combine(_dir, "verify_tail.hyjr");
        File.WriteAllBytes(tailPath, withTail);
        var tailChecksum = HyjrSegmentFormat.ComputeFileChecksum(tailPath);
        Assert.False(HyjrSegmentFormat.TryVerifyClosedFile(
            tailPath, tailChecksum, withTail.Length, out _));

        var badMagic = (byte[])intact.Clone();
        var magicAt = badMagic.Length - HyjrSegmentFormat.FooterSize;
        badMagic[magicAt] ^= 0xFF;
        var magicPath = Path.Combine(_dir, "verify_magic.hyjr");
        File.WriteAllBytes(magicPath, badMagic);
        var magicChecksum = HyjrSegmentFormat.ComputeFileChecksum(magicPath);
        Assert.False(HyjrSegmentFormat.TryVerifyClosedFile(
            magicPath, magicChecksum, badMagic.Length, out _));

        var untouched = File.ReadAllBytes(path);
        Assert.True(intact.AsSpan().SequenceEqual(untouched));
    }

    [Fact]
    public void TryVerifyClosedFile_zero_record_segment_has_null_last_seq()
    {
        var path = Path.Combine(_dir, "verify_empty.hyjr");
        WriteClosedSegment(path, recordCount: 0, firstSeq: 4);
        var length = new FileInfo(path).Length;
        var checksum = HyjrSegmentFormat.ComputeFileChecksum(path);

        Assert.True(HyjrSegmentFormat.TryVerifyClosedFile(path, checksum, length, out var result));
        Assert.Equal(4, result.FirstSeq);
        Assert.Null(result.LastValidSeq);
        Assert.Equal(0, result.RecordCount);
        Assert.True(result.Closed);
        Assert.True(result.ReplayComplete);
        Assert.Equal(length, result.Bytes);
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task RecoverAsync_does_not_append_into_an_older_open_segment()
    {
        var sessionId = "rs_older_open";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();

        var list = await store.ListSegmentsAsync(sessionId);
        Assert.True(list.IsOk, list.IsOk ? null : list.Error.Message);
        var segs = list.Value.OrderBy(s => s.FirstSeq).ToList();
        Assert.Equal(2, segs.Count);
        var older = Path.Combine(_dir, segs[0].RelativePath);
        var later = Path.Combine(_dir, segs[1].RelativePath);
        var laterBytes = await File.ReadAllBytesAsync(later);

        var olderBytes = await File.ReadAllBytesAsync(older);
        Assert.Equal(
            "ENDJ",
            System.Text.Encoding.ASCII.GetString(
                olderBytes, olderBytes.Length - HyjrSegmentFormat.FooterSize, 4));
        await File.WriteAllBytesAsync(
            older, olderBytes.AsSpan(0, olderBytes.Length - HyjrSegmentFormat.FooterSize).ToArray());
        await store.UpsertSegmentAsync(segs[0] with
        {
            Closed = false,
            ChecksumSha256 = null,
            ClosedAt = null,
            ByteCount = olderBytes.Length - HyjrSegmentFormat.FooterSize,
        });

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.True(health.Value.ReplayComplete);
        Assert.Equal(3, health.Value.NextSeq);

        var appended = await journal2.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":3}""");
        Assert.True(appended.IsOk, appended.IsOk ? null : appended.Error.Message);
        Assert.Equal(3, appended.Value.Seq);

        var range = await journal2.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 10);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.Equal(new long[] { 1, 2, 3 }, range.Value.Select(r => r.Seq));
        for (var i = 1; i < range.Value.Count; i++)
            Assert.True(range.Value[i].Seq > range.Value[i - 1].Seq);

        var after = await store.ListSegmentsAsync(sessionId);
        Assert.True(after.IsOk);
        var newest = after.Value.OrderBy(s => s.FirstSeq).Last();
        Assert.True(newest.FirstSeq > segs[1].FirstSeq);
        Assert.NotEqual(segs[0].RelativePath, newest.RelativePath);
        var laterAfter = await File.ReadAllBytesAsync(later);
        Assert.True(laterBytes.AsSpan().SequenceEqual(laterAfter));

        var olderScan = HyjrSegmentFormat.ScanFile(older);
        Assert.True(olderScan.Closed);
        Assert.Equal(1, olderScan.LastValidSeq);
        await journal2.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoverAsync_rejects_footer_metadata_that_does_not_match_records(bool wrongCount)
    {
        var sessionId = wrongCount ? "rs_bad_count" : "rs_bad_last";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();

        var list = await store.ListSegmentsAsync(sessionId);
        Assert.True(list.IsOk, list.IsOk ? null : list.Error.Message);
        var seg = Assert.Single(list.Value);
        var abs = Path.Combine(_dir, seg.RelativePath);
        var bytes = await File.ReadAllBytesAsync(abs);
        var footer = bytes.Length - HyjrSegmentFormat.FooterSize;
        if (wrongCount)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(footer + 12), 99);
        else
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(footer + 4), 99);
        await File.WriteAllBytesAsync(abs, bytes);
        var checksum = HyjrSegmentFormat.ComputeFileChecksum(abs);
        Assert.False(HyjrSegmentFormat.TryVerifyClosedFile(abs, checksum, bytes.Length, out _));

        await store.UpsertSegmentAsync(seg with
        {
            ChecksumSha256 = checksum,
            ByteCount = bytes.Length,
            Closed = true,
        });

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.False(health.Value.ReplayComplete);

        var range = await journal2.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 10);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.NotEmpty(range.Value);
        Assert.True(health.Value.NextSeq > range.Value.Max(r => r.Seq));
        for (var i = 1; i < range.Value.Count; i++)
            Assert.True(range.Value[i].Seq > range.Value[i - 1].Seq);
        await journal2.DisposeAsync();
    }

    [Fact]
    public void WriteRecord_writes_the_frame_in_one_call()
    {
        var stream = new CountingWriteStream();
        var at = DateTimeOffset.Parse("2026-08-12T00:00:00Z");
        var written = HyjrSegmentFormat.WriteRecord(stream, new RuntimeEventRecord
        {
            Seq = 1,
            Class = EventClass.Lifecycle,
            Reliability = EventReliability.Reliable,
            Type = "pane.lifecycle",
            OccurredAt = at,
            PayloadJson = HyjrSegmentFormat.WrapStoredPayload(
                "pane.lifecycle", at, """{"pane_id":"p1"}"""),
        });
        Assert.Equal(1, stream.WriteCalls);
        Assert.Equal(written, stream.Length);
    }

    [Fact]
    public async Task Sync_failure_of_any_exception_does_not_leave_a_duplicate_sequence()
    {
        var sessionId = "rs_sync_any";
        var sync = new ThrowingDurableFileSync(DurableFileSync.Production)
        {
            Exception = new InvalidOperationException("injected sync failure"),
        };
        var (store, journal) = await PrepareAsync(sessionId, durableSync: sync);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var first = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""");
        Assert.True(first.IsOk, first.IsOk ? null : first.Error.Message);

        sync.ThrowOnNext = true;
        var failed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""");
        Assert.False(failed.IsOk);

        sync.Exception = new OperationCanceledException("injected cancel");
        sync.ThrowOnNext = true;
        var cancelled = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":4}""");
        Assert.False(cancelled.IsOk);

        var retry = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":3}""");
        Assert.True(retry.IsOk, retry.IsOk ? null : retry.Error.Message);
        Assert.Equal(first.Value.Seq + 1, failed.Error.LiveRecord!.Seq);
        Assert.Equal(first.Value.Seq + 2, cancelled.Error.LiveRecord!.Seq);
        Assert.Equal(first.Value.Seq + 3, retry.Value.Seq);

        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        var range = await journal2.ReadRangeAsync(
            fromSeqExclusive: health.Value.FloorSeq - 1, classes: null, budget: 10);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.Equal(new[] { retry.Value.Seq }, range.Value.Select(r => r.Seq));
        Assert.True(journal2.NextSeq > range.Value.Max(r => r.Seq));
        await journal2.DisposeAsync();
    }

    [Fact]
    public async Task Abandoned_segment_counts_bytes_left_by_failed_truncation()
    {
        var sessionId = "rs_trunc_bytes";
        var sync = new ThrowingDurableFileSync(DurableFileSync.Production);
        var (_, journal) = await PrepareAsync(sessionId, durableSync: sync);
        TruncateFailingFileStream? open = null;
        journal.SetSegmentStreamFactory((path, mode) =>
        {
            open = new TruncateFailingFileStream(path, mode);
            return open;
        });
        Assert.True((await journal.RecoverAsync()).IsOk);

        var first = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""");
        Assert.True(first.IsOk, first.IsOk ? null : first.Error.Message);
        Assert.NotNull(open);

        sync.ThrowOnNext = true;
        open.FailNextSetLength = true;
        var failed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""");
        Assert.False(failed.IsOk);

        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        var health = journal.GetHealth();
        var sum = Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr")
            .Sum(path => new FileInfo(path).Length);
        Assert.True(sum > HyjrSegmentFormat.HeaderSize);
        Assert.Equal(sum, health.Bytes);

        var scan = HyjrSegmentFormat.ScanFile(
            Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr").Single());
        Assert.Equal(2, scan.RecordCount);
        Assert.False(scan.Closed);
        await journal.DisposeAsync();
    }

    [Fact]
    public void TryVerifyClosedFile_rejects_a_footer_digest_that_does_not_match_the_records()
    {
        var path = Path.Combine(_dir, "verify_footer_digest.hyjr");
        WriteClosedSegment(path, recordCount: 2, firstSeq: 1);
        var bytes = File.ReadAllBytes(path);
        // The last footer byte is inside the header-and-records digest.
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        var checksum = HyjrSegmentFormat.ComputeFileChecksum(path);

        Assert.False(HyjrSegmentFormat.TryVerifyClosedFile(path, checksum, bytes.Length, out _));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void TryVerifyClosedFile_accepts_fields_split_across_the_read_buffer(int bytesBeforeBoundary)
    {
        // Frame = 4-byte length + fixed body + payload + digest.
        const int frameOverhead = 4 + HyjrSegmentFormat.RecordFixedBodySize + HyjrSegmentFormat.DigestSize;
        var boundary = HyjrSegmentFormat.ClosedFileVerifyBufferBytes;
        var firstPayload = boundary - bytesBeforeBoundary - HyjrSegmentFormat.HeaderSize - frameOverhead;
        var path = Path.Combine(_dir, $"verify_split_{bytesBeforeBoundary}.hyjr");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            HyjrSegmentFormat.WriteHeader(fs, 1);
            HyjrSegmentFormat.WriteRecord(
                fs, 1, EventClass.Lifecycle, EventReliability.Reliable, new byte[firstPayload]);
            Assert.Equal(boundary - bytesBeforeBoundary, fs.Position);
            HyjrSegmentFormat.WriteRecord(
                fs, 2, EventClass.Lifecycle, EventReliability.Reliable, "{}"u8);
            HyjrSegmentFormat.WriteRecord(
                fs, 3, EventClass.Lifecycle, EventReliability.Reliable, "{}"u8);
            fs.Flush();
            fs.Position = 0;
            var prefix = new byte[fs.Length];
            fs.ReadExactly(prefix);
            fs.Position = fs.Length;
            HyjrSegmentFormat.WriteFooter(fs, 3, 3, prefix);
        }

        var length = new FileInfo(path).Length;
        var checksum = HyjrSegmentFormat.ComputeFileChecksum(path);
        Assert.True(HyjrSegmentFormat.TryVerifyClosedFile(path, checksum, length, out var result));
        Assert.Equal(3, result.RecordCount);
        Assert.Equal(3, result.LastValidSeq);
    }

    [Fact]
    public async Task Append_is_refused_when_an_abandoned_tail_crosses_the_budget()
    {
        var sessionId = "rs_budget_tail";
        var at = DateTimeOffset.Parse("2026-08-12T00:00:00Z");
        const string payload = """{"pane_id":"p1","n":1}""";
        var stored = HyjrSegmentFormat.WrapStoredPayload("pane.lifecycle", at, payload);
        var frame = 4 + HyjrSegmentFormat.RecordFixedBodySize + HyjrSegmentFormat.DigestSize
            + Encoding.UTF8.GetByteCount(stored);
        // One header and one record fit, with room for the footer that a rotation
        // counts. The uncounted tail of a second record does not.
        var budget = HyjrSegmentFormat.HeaderSize + (2 * HyjrSegmentFormat.FooterSize) + frame + 1;
        var sync = new ThrowingDurableFileSync(DurableFileSync.Production);
        var (_, journal) = await PrepareAsync(sessionId, durableSync: sync, maxJournalBytes: budget);
        TruncateFailingFileStream? open = null;
        journal.SetSegmentStreamFactory((path, mode) =>
        {
            open = new TruncateFailingFileStream(path, mode);
            return open;
        });
        Assert.True((await journal.RecoverAsync()).IsOk);

        var first = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable, "pane.lifecycle", payload, at);
        Assert.True(first.IsOk, first.IsOk ? null : first.Error.Message);
        Assert.NotNull(open);

        sync.ThrowOnNext = true;
        open.FailNextSetLength = true;
        var failed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable, "pane.lifecycle", payload, at);
        Assert.False(failed.IsOk);

        var refused = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable, "pane.lifecycle", payload, at);
        Assert.False(refused.IsOk);
        Assert.Contains("budget", refused.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr"));
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Close_after_a_rolled_back_first_append_closes_an_empty_segment()
    {
        var sessionId = "rs_empty_close";
        // Call 1 syncs the new segment. Call 2 is the first record sync.
        var sync = new NthCallThrowingDurableFileSync(DurableFileSync.Production, throwOnCall: 2);
        var (store, journal) = await PrepareAsync(sessionId, durableSync: sync);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var failed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""");
        Assert.False(failed.IsOk);

        var closed = await journal.CloseOpenSegmentAsync();
        Assert.True(closed.IsOk, closed.IsOk ? null : closed.Error.Message);
        var list = await store.ListSegmentsAsync(sessionId);
        Assert.True(list.IsOk);
        var segment = Assert.Single(list.Value);
        Assert.True(segment.Closed);
        Assert.Null(segment.LastSeq);
        Assert.Equal(0, segment.RecordCount);
        await journal.DisposeAsync();

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.True(health.Value.ReplayComplete, health.Value.ReplayError);
        var next = await journal2.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""");
        Assert.True(next.IsOk, next.IsOk ? null : next.Error.Message);
        await journal2.DisposeAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(HyjrSegmentFormat.FooterSize - 1)]
    public async Task RecoverAsync_seals_an_older_segment_that_ends_in_a_partial_footer(int footerBytesKept)
    {
        var sessionId = "rs_partial_footer_" + footerBytesKept;
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();

        var list = await store.ListSegmentsAsync(sessionId);
        Assert.True(list.IsOk);
        var older = list.Value.OrderBy(s => s.FirstSeq).First();
        var olderPath = Path.Combine(_dir, older.RelativePath);
        var olderBytes = await File.ReadAllBytesAsync(olderPath);
        // A crash during close left only part of the footer on disk.
        var partialLength = olderBytes.Length - HyjrSegmentFormat.FooterSize + footerBytesKept;
        await File.WriteAllBytesAsync(olderPath, olderBytes.AsSpan(0, partialLength).ToArray());
        await store.UpsertSegmentAsync(older with
        {
            Closed = false,
            ChecksumSha256 = null,
            ClosedAt = null,
            ByteCount = partialLength,
        });

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.True(health.Value.ReplayComplete, health.Value.ReplayError);
        Assert.Equal(3, health.Value.NextSeq);
        var after = await store.ListSegmentsAsync(sessionId);
        Assert.True(after.IsOk);
        Assert.True(after.Value.Single(s => s.SegmentId == older.SegmentId).Closed);
        Assert.True(HyjrSegmentFormat.ScanFile(olderPath).Closed);
        await journal2.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_counts_disk_bytes_when_sealing_an_older_segment_fails()
    {
        var sessionId = "rs_seal_fail";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();

        var list = await store.ListSegmentsAsync(sessionId);
        Assert.True(list.IsOk);
        var older = list.Value.OrderBy(s => s.FirstSeq).First();
        var olderPath = Path.Combine(_dir, older.RelativePath);
        var olderBytes = await File.ReadAllBytesAsync(olderPath);
        var unsealed = olderBytes.Length - HyjrSegmentFormat.FooterSize;
        await File.WriteAllBytesAsync(olderPath, olderBytes.AsSpan(0, unsealed).ToArray());
        await store.UpsertSegmentAsync(older with
        {
            Closed = false,
            ChecksumSha256 = null,
            ClosedAt = null,
            ByteCount = unsealed,
        });

        var failing = new FailingUpsertManifestStore(store, int.MaxValue) { FailClose = true };
        var journal2 = new FileRuntimeEventJournal(_paths, failing, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.False(health.Value.ReplayComplete);
        var onDisk = Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr")
            .Sum(path => new FileInfo(path).Length);
        Assert.Equal(onDisk, health.Value.Bytes);
        Assert.Equal(unsealed, new FileInfo(olderPath).Length);
        var rows = await store.ListSegmentsAsync(sessionId);
        Assert.True(rows.IsOk);
        Assert.Equal(unsealed, rows.Value.Single(s => s.SegmentId == older.SegmentId).ByteCount);
        await journal2.DisposeAsync();
    }

    [Fact]
    public void TryVerifyClosedFile_rejects_a_high_bit_sequence()
    {
        var path = Path.Combine(_dir, "verify_high_bit.hyjr");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            HyjrSegmentFormat.WriteHeader(fs, long.MinValue);
            HyjrSegmentFormat.WriteRecord(
                fs, long.MinValue, EventClass.Lifecycle, EventReliability.Reliable, "{}"u8);
            fs.Flush();
            fs.Position = 0;
            var prefix = new byte[fs.Length];
            fs.ReadExactly(prefix);
            fs.Position = fs.Length;
            HyjrSegmentFormat.WriteFooter(fs, long.MinValue, 1, prefix);
        }

        var length = new FileInfo(path).Length;
        var checksum = HyjrSegmentFormat.ComputeFileChecksum(path);
        Assert.False(HyjrSegmentFormat.TryVerifyClosedFile(path, checksum, length, out _));
    }

    [Fact]
    public async Task Append_after_a_non_io_footer_sync_failure_stays_readable()
    {
        var sessionId = "rs_footer_sync_other";
        var sync = new NthCallThrowingDurableFileSync(
            DurableFileSync.Production,
            throwOnCall: 3,
            failure: () => new InvalidOperationException("injected sync failure"));
        var (store, journal) = await PrepareAsync(sessionId, durableSync: sync);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);

        Assert.False((await journal.CloseOpenSegmentAsync()).IsOk);
        var next = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""");
        Assert.True(next.IsOk, next.IsOk ? null : next.Error.Message);
        await journal.DisposeAsync();

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        Assert.True((await journal2.RecoverAsync()).IsOk);
        var range = await journal2.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 10);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.Equal(new long[] { 1, 2 }, range.Value.Select(r => r.Seq));
        await journal2.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_does_not_append_into_an_empty_headerless_segment()
    {
        var sessionId = "rs_empty_orphan";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();

        // A crash after the file was created and before its header was written.
        var relative = $"{RuntimeStatePaths.JournalDirectoryName}/seg_2_emptyorphan0.hyjr";
        await File.WriteAllBytesAsync(Path.Combine(_dir, relative), []);
        await store.UpsertSegmentAsync(new JournalSegmentManifest
        {
            SegmentId = "emptyorphan0",
            SessionId = sessionId,
            RelativePath = relative,
            FirstSeq = 2,
            ByteCount = 0,
            Closed = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.True(health.Value.ReplayComplete, health.Value.ReplayError);
        Assert.True((await journal2.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""")).IsOk);
        await journal2.DisposeAsync();
        Assert.Equal(0, new FileInfo(Path.Combine(_dir, relative)).Length);

        var journal3 = new FileRuntimeEventJournal(_paths, store, sessionId);
        Assert.True((await journal3.RecoverAsync()).IsOk);
        var range = await journal3.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 10);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.Equal(new long[] { 1, 2 }, range.Value.Select(r => r.Seq));
        await journal3.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_orders_segments_that_share_a_first_seq_by_creation()
    {
        var sessionId = "rs_first_seq_tie";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);
        Assert.True((await journal.CloseOpenSegmentAsync()).IsOk);
        await journal.DisposeAsync();
        var later = Assert.Single((await store.ListSegmentsAsync(sessionId)).Value);

        // An older, header-only segment with the same first seq. Its path sorts last.
        var relative = $"{RuntimeStatePaths.JournalDirectoryName}/seg_1_zzzzolder000.hyjr";
        await using (var fs = new FileStream(Path.Combine(_dir, relative), FileMode.CreateNew))
            HyjrSegmentFormat.WriteHeader(fs, 1);
        await store.UpsertSegmentAsync(new JournalSegmentManifest
        {
            SegmentId = "zzzzolder000",
            SessionId = sessionId,
            RelativePath = relative,
            FirstSeq = 1,
            ByteCount = HyjrSegmentFormat.HeaderSize,
            Closed = false,
            CreatedAt = later.CreatedAt - TimeSpan.FromMinutes(1),
        });

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        var health = await journal2.RecoverAsync();
        Assert.True(health.IsOk, health.IsOk ? null : health.Error.Message);
        Assert.True(health.Value.ReplayComplete, health.Value.ReplayError);
        var appended = await journal2.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""");
        Assert.True(appended.IsOk, appended.IsOk ? null : appended.Error.Message);
        Assert.Equal(2, appended.Value.Seq);
        await journal2.DisposeAsync();
        Assert.True(HyjrSegmentFormat.ScanFile(Path.Combine(_dir, relative)).RecordCount == 0);

        var journal3 = new FileRuntimeEventJournal(_paths, store, sessionId);
        Assert.True((await journal3.RecoverAsync()).IsOk);
        var range = await journal3.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 10);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.Equal(new long[] { 1, 2 }, range.Value.Select(r => r.Seq));
        await journal3.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_manifest_close_counts_the_footer_once()
    {
        var sessionId = "rs_close_cancel";
        var (store, _) = await PrepareAsync(sessionId);
        var failing = new FailingUpsertManifestStore(store, int.MaxValue);
        var journal = new FileRuntimeEventJournal(_paths, failing, sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);

        failing.CancelClose = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.CloseOpenSegmentAsync());
        failing.CancelClose = false;

        var onDisk = Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr")
            .Sum(path => new FileInfo(path).Length);
        Assert.Equal(onDisk, journal.GetHealth().Bytes);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""")).IsOk);
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Append_after_a_failed_footer_sync_stays_readable()
    {
        var sessionId = "rs_footer_sync";
        // Call 1 syncs the new segment, call 2 syncs the record, call 3 syncs the footer.
        var sync = new NthCallThrowingDurableFileSync(DurableFileSync.Production, throwOnCall: 3);
        var (store, journal) = await PrepareAsync(sessionId, durableSync: sync);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""")).IsOk);

        Assert.False((await journal.CloseOpenSegmentAsync()).IsOk);

        var next = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""");
        Assert.True(next.IsOk, next.IsOk ? null : next.Error.Message);
        Assert.Equal(2, next.Value.Seq);
        await journal.DisposeAsync();

        var journal2 = new FileRuntimeEventJournal(_paths, store, sessionId);
        Assert.True((await journal2.RecoverAsync()).IsOk);
        var range = await journal2.ReadRangeAsync(fromSeqExclusive: 0, classes: null, budget: 10);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.Equal(new long[] { 1, 2 }, range.Value.Select(r => r.Seq));
        await journal2.DisposeAsync();
    }

    private sealed class NthCallThrowingDurableFileSync(
        IDurableFileSync inner,
        int throwOnCall,
        Func<Exception>? failure = null)
        : IDurableFileSync
    {
        private int _calls;

        public void SyncFileThenDirectory(SafeFileHandle fileHandle, string filePath)
        {
            if (++_calls == throwOnCall)
                throw failure?.Invoke() ?? new IOException("injected sync failure");
            inner.SyncFileThenDirectory(fileHandle, filePath);
        }
    }

    private sealed class TruncateFailingFileStream : FileStream
    {
        public TruncateFailingFileStream(string path, FileMode mode)
            : base(
                path,
                mode,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous)
        {
        }

        public bool FailNextSetLength { get; set; }

        public override void SetLength(long value)
        {
            if (FailNextSetLength)
            {
                FailNextSetLength = false;
                Flush(flushToDisk: false);
                throw new IOException("injected truncate failure");
            }

            base.SetLength(value);
        }
    }

    private sealed class CountingWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public int WriteCalls { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteCalls++;
            _inner.Write(buffer);
        }
    }

    private static void WriteClosedSegment(string path, int recordCount, long firstSeq)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        HyjrSegmentFormat.WriteHeader(fs, firstSeq);
        long lastSeq = 0;
        for (var i = 0; i < recordCount; i++)
        {
            lastSeq = firstSeq + i;
            var at = DateTimeOffset.Parse("2026-08-12T00:00:00Z");
            HyjrSegmentFormat.WriteRecord(fs, new RuntimeEventRecord
            {
                Seq = lastSeq,
                Class = EventClass.Lifecycle,
                Reliability = EventReliability.Reliable,
                Type = "pane.lifecycle",
                OccurredAt = at,
                PayloadJson = HyjrSegmentFormat.WrapStoredPayload(
                    "pane.lifecycle", at, $$"""{"pane_id":"p1","n":{{i}}}"""),
            });
        }

        fs.Flush();
        fs.Position = 0;
        var prefix = new byte[fs.Length];
        fs.ReadExactly(prefix);
        fs.Position = fs.Length;
        HyjrSegmentFormat.WriteFooter(fs, lastSeq, recordCount, prefix);
    }

    /// <summary>
    /// After a durable HYJR write, an UpsertSegmentAsync failure still returns the
    /// record for live fanout, and the sequence becomes a gap below the floor.
    /// </summary>
    [Fact]
    public async Task Append_upsert_failure_after_the_write_gaps_the_sequence()
    {
        var sessionId = "rs_upsert_fail";
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                INSERT OR IGNORE INTO sessions(
                  session_id, name, lifecycle_state, placement, placement_generation,
                  started_at, updated_at, replay_complete)
                VALUES ('{sessionId}', 'j', 'ready', 'local', 0, '2026-08-12T00:00:00Z', '2026-08-12T00:00:00Z', 1);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var inner = new SqliteJournalManifestStore(_paths);
        var manifests = new FailingUpsertManifestStore(inner, failAfterSuccessfulUpserts: 2);
        var journal = new FileRuntimeEventJournal(_paths, manifests, sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);

        // First append: open segment upsert (1) + record upsert (2) — both succeed.
        var a1 = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":1}""");
        Assert.True(a1.IsOk, a1.IsOk ? null : a1.Error.Message);
        Assert.Equal(1, a1.Value.Seq);
        Assert.Equal(2, journal.NextSeq);

        // Second append: record upsert fails after durable write.
        var a2 = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1","n":2}""");
        Assert.False(a2.IsOk);
        Assert.Equal(2, a2.Error.LiveRecord!.Seq);
        // Allocator advanced for the durable on-disk record.
        Assert.Equal(3, journal.NextSeq);

        // Seq 2 is on disk, but the manifest does not record it. The mux treats
        // it as a gap: the floor moves past it, so no resume can claim it.
        Assert.Equal(3, journal.GetHealth().FloorSeq);
        var across = await journal.ReadRangeAsync(fromSeqExclusive: 1, classes: null, budget: 100);
        Assert.False(across.IsOk);
        var after = await journal.ReadRangeAsync(fromSeqExclusive: 2, classes: null, budget: 100);
        Assert.True(after.IsOk, after.IsOk ? null : after.Error.Message);
        Assert.Empty(after.Value);

        await journal.DisposeAsync();
        await inner.DisposeAsync();
    }

    /// <summary>Throws <see cref="IOException"/> on the next durable sync, then delegates.</summary>
    private sealed class ThrowingDurableFileSync : IDurableFileSync
    {
        private readonly IDurableFileSync _inner;

        public ThrowingDurableFileSync(IDurableFileSync inner) =>
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public bool ThrowOnNext { get; set; }

        public Exception? Exception { get; set; }

        public void SyncFileThenDirectory(SafeFileHandle fileHandle, string filePath)
        {
            if (ThrowOnNext)
            {
                ThrowOnNext = false;
                throw Exception ?? new IOException("injected sync failure");
            }

            _inner.SyncFileThenDirectory(fileHandle, filePath);
        }
    }

    /// <summary>Delegates to an inner store; fails UpsertSegmentAsync after N successes.</summary>
    private sealed class FailingUpsertManifestStore : IJournalManifestStore
    {
        private readonly IJournalManifestStore _inner;
        private readonly int _failAfter;
        private int _upserts;

        public FailingUpsertManifestStore(IJournalManifestStore inner, int failAfterSuccessfulUpserts)
        {
            _inner = inner;
            _failAfter = failAfterSuccessfulUpserts;
        }

        public async Task<RuntimeResult<RuntimeUnit>> UpsertSegmentAsync(
            JournalSegmentManifest segment, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _upserts);
            if (n > _failAfter)
            {
                return RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Io("injected upsert failure"));
            }

            return await _inner.UpsertSegmentAsync(segment, ct).ConfigureAwait(false);
        }

        public Task<RuntimeResult<IReadOnlyList<JournalSegmentManifest>>> ListSegmentsAsync(
            string sessionId, CancellationToken ct = default) =>
            _inner.ListSegmentsAsync(sessionId, ct);

        public Task<RuntimeResult<JournalSegmentManifest?>> GetOpenSegmentAsync(
            string sessionId, CancellationToken ct = default) =>
            _inner.GetOpenSegmentAsync(sessionId, ct);

        public bool FailClose { get; set; }

        public bool CancelClose { get; set; }

        public Task<RuntimeResult<RuntimeUnit>> CloseSegmentAsync(
            string segmentId, long? lastSeq, int recordCount, long byteCount,
            string checksumSha256, DateTimeOffset closedAt, CancellationToken ct = default) =>
            CancelClose
                ? throw new OperationCanceledException("injected close cancellation")
                : FailClose
                ? Task.FromResult(RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Io("injected close failure")))
                : _inner.CloseSegmentAsync(
                    segmentId, lastSeq, recordCount, byteCount, checksumSha256, closedAt, ct);

        public Task<RuntimeResult<long>> ReadEventCursorAsync(
            string sessionId, CancellationToken ct = default) =>
            _inner.ReadEventCursorAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> WriteEventCursorAsync(
            string sessionId, long nextSeq, CancellationToken ct = default) =>
            _inner.WriteEventCursorAsync(sessionId, nextSeq, ct);

        public async Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
            JournalSegmentManifest segment,
            string sessionId,
            long nextSeq,
            CancellationToken ct = default)
        {
            var upsert = await UpsertSegmentAsync(segment, ct).ConfigureAwait(false);
            if (!upsert.IsOk)
                return upsert;
            return await _inner.WriteEventCursorAsync(sessionId, nextSeq, ct).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Abrupt_stop_replays_every_accepted_output_record()
    {
        const int n = 32;
        var sessionId = "rs_abrupt";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);
        for (var i = 0; i < n; i++)
        {
            var refused = await journal.AppendAsync(
                EventClass.Output, EventReliability.Output,
                "terminal.output", """{"pane_id":"p1","encoding":"base64","data":"YQ==","byte_count":1}""");
            Assert.False(refused.IsOk);
            var append = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                "pane.lifecycle", $$"""{"pane_id":"p1","n":{{i}}}""");
            Assert.True(append.IsOk, append.IsOk ? null : append.Error.Message);
        }

        var files = Directory.GetFiles(_paths.JournalDirectory, "*.hyjr");
        Assert.Single(files);
        var snapshot = await File.ReadAllBytesAsync(files[0]);
        await journal.DisposeAsync();

        var copyDir = Path.Combine(_dir, "abrupt-copy");
        Directory.CreateDirectory(Path.Combine(copyDir, "journal"));
        var copyPaths = new RuntimeStatePaths { StateDirectory = copyDir };
        await File.WriteAllBytesAsync(
            Path.Combine(copyDir, "journal", Path.GetFileName(files[0])), snapshot);

        var migrator = new SqliteRuntimeSchemaMigrator(copyPaths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        await using (var conn = new SqliteConnection($"Data Source={copyPaths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var session = conn.CreateCommand();
            session.CommandText = $"""
                INSERT OR IGNORE INTO sessions(
                  session_id, name, lifecycle_state, placement, placement_generation,
                  started_at, updated_at, replay_complete)
                VALUES ('{sessionId}', 'j', 'ready', 'local', 0, '2026-08-12T00:00:00Z', '2026-08-12T00:00:00Z', 1);
                """;
            await session.ExecuteNonQueryAsync();
        }

        var copyStore = new SqliteJournalManifestStore(copyPaths);
        await using var recovered = new FileRuntimeEventJournal(copyPaths, copyStore, sessionId);
        var recoveredHealth = await recovered.RecoverAsync();
        Assert.True(recoveredHealth.IsOk, recoveredHealth.IsOk ? null : recoveredHealth.Error.Message);
        var range = await recovered.ReadRangeAsync(0, null, n + 8);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.Equal(n, range.Value.Count);
        Assert.DoesNotContain(range.Value, r => r.Type == "terminal.output");
        for (var i = 0; i < n; i++)
            Assert.Equal(i + 1, range.Value[i].Seq);
    }

    [Fact]
    public async Task Abrupt_stop_with_a_stale_manifest_replays_the_open_segment()
    {
        const int n = 12;
        var sessionId = "rs_stale";
        var (store, journal) = await PrepareAsync(sessionId);
        Assert.True((await journal.RecoverAsync()).IsOk);
        for (var i = 0; i < n; i++)
        {
            var append = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                "pane.lifecycle", $$"""{"pane_id":"p1","n":{{i}}}""");
            Assert.True(append.IsOk, append.IsOk ? null : append.Error.Message);
        }

        var files = Directory.GetFiles(_paths.JournalDirectory, "*.hyjr");
        Assert.Single(files);
        var snapshot = await File.ReadAllBytesAsync(files[0]);
        await journal.DisposeAsync();

        var copyDir = Path.Combine(_dir, "stale-copy");
        Directory.CreateDirectory(Path.Combine(copyDir, "journal"));
        var copyPaths = new RuntimeStatePaths { StateDirectory = copyDir };
        await File.WriteAllBytesAsync(
            Path.Combine(copyDir, "journal", Path.GetFileName(files[0])), snapshot);

        var migrator = new SqliteRuntimeSchemaMigrator(copyPaths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        await using (var conn = new SqliteConnection($"Data Source={copyPaths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using (var session = conn.CreateCommand())
            {
                session.CommandText = $"""
                    INSERT OR IGNORE INTO sessions(
                      session_id, name, lifecycle_state, placement, placement_generation,
                      started_at, updated_at, replay_complete)
                    VALUES ('{sessionId}', 'j', 'ready', 'local', 0, '2026-08-12T00:00:00Z', '2026-08-12T00:00:00Z', 1);
                    """;
                await session.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO journal_segments(
                  segment_id, session_id, relative_path, first_seq, last_seq,
                  record_count, byte_count, closed, created_at)
                VALUES (
                  'stale', @sid, @rel, 1, 1, 1, 16, 0, '2026-08-12T00:00:00Z');
                INSERT INTO event_cursor(session_id, next_seq) VALUES (@sid, 2);
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue(
                "@rel",
                "journal/" + Path.GetFileName(files[0]));
            await cmd.ExecuteNonQueryAsync();
        }

        var copyStore = new SqliteJournalManifestStore(copyPaths);
        await using var recovered = new FileRuntimeEventJournal(copyPaths, copyStore, sessionId);
        Assert.True((await recovered.RecoverAsync()).IsOk);
        var range = await recovered.ReadRangeAsync(0, null, n + 8);
        Assert.True(range.IsOk);
        Assert.Equal(n, range.Value.Count);
    }

    [Fact]
    public void ScanFile_is_read_only_when_footer_invalid()
    {
        var path = Path.Combine(_dir, "scan_only.hyjr");
        using (var fs = File.Create(path))
        {
            HyjrSegmentFormat.WriteHeader(fs, firstSeq: 1);
            HyjrSegmentFormat.WriteRecord(fs, new RuntimeEventRecord
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
            });
            fs.Flush();
            // Incomplete: no footer.
        }

        var lenBefore = new FileInfo(path).Length;
        var bytesBefore = File.ReadAllBytes(path);

        var scan = HyjrSegmentFormat.ScanFile(path);
        Assert.False(scan.Closed);
        Assert.Single(scan.Records);
        Assert.False(scan.Truncated);

        var bytesAfter = File.ReadAllBytes(path);
        Assert.Equal(lenBefore, bytesAfter.Length);
        Assert.True(bytesBefore.AsSpan().SequenceEqual(bytesAfter));

        // RecoverFile may truncate (no change if already at lastCompleteEnd with no garbage).
        var recover = HyjrSegmentFormat.RecoverFile(path);
        Assert.False(recover.Closed);
        Assert.Single(recover.Records);
    }
}
