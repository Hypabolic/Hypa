using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class JournalDurabilityTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public JournalDurabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h22-jdur-" + Guid.NewGuid().ToString("N"));
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

    private async Task<FileRuntimeEventJournal> PrepareAsync(
        string sessionId = "rs_h22",
        IDurableFileSync? durableSync = null,
        int? rotateRecords = null,
        long? rotateBytes = null)
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
            rotateRecords: rotateRecords);
        Assert.True((await journal.RecoverAsync()).IsOk);
        return journal;
    }

    [Fact]
    public async Task Append_syncs_file_then_directory()
    {
        var native = new RecordingDurableNativeSync();
        await using var journal = await PrepareAsync(durableSync: new DurableFileSync(native));

        var appended = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.True(appended.IsOk, appended.IsOk ? null : appended.Error.Message);

        AssertFileThenDirectoryPairs(native.Phases);
        Assert.True(native.Phases.Count >= 4, "new-segment header plus record each sync file then directory");
    }

    [Fact]
    public async Task Output_append_does_not_call_durable_sync()
    {
        var native = new RecordingDurableNativeSync();
        await using var journal = await PrepareAsync("rs_h26_out", new DurableFileSync(native));

        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);
        var phasesAfterSeed = native.Phases.Count;
        Assert.True(phasesAfterSeed >= 2);

        var before = journal.NextSeq;
        var output = await journal.AppendAsync(
            EventClass.Output, EventReliability.Output,
            "terminal.output",
            """{"pane_id":"p1","encoding":"base64","data":"YQ==","byte_count":1}""");
        Assert.False(output.IsOk);
        Assert.Equal(phasesAfterSeed, native.Phases.Count);
        Assert.Equal(before, journal.NextSeq);

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 20);
        Assert.True(range.IsOk);
        Assert.DoesNotContain(range.Value, r => r.Type == "terminal.output");
    }

    [Fact]
    public async Task Output_first_append_does_not_call_durable_sync()
    {
        var native = new RecordingDurableNativeSync();
        await using var journal = await PrepareAsync("rs_h26_first", new DurableFileSync(native));

        var output = await journal.AppendAsync(
            EventClass.Output, EventReliability.Output,
            "terminal.output",
            """{"pane_id":"p1","encoding":"base64","data":"YQ==","byte_count":1}""");
        Assert.False(output.IsOk);
        Assert.Empty(native.Phases);
        Assert.Equal(1, journal.NextSeq);

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 20);
        Assert.True(range.IsOk);
        Assert.Empty(range.Value);
    }

    [Fact]
    public async Task Output_append_at_record_rotation_does_not_call_durable_sync()
    {
        var native = new RecordingDurableNativeSync();
        var armed = new ArmableDurableFileSync(new DurableFileSync(native));
        await using var journal = await PrepareAsync(
            "rs_h26_rot",
            armed,
            rotateRecords: 2);

        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);
        var fill = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p2"}""");
        Assert.True(fill.IsOk, fill.IsOk ? null : fill.Error.Message);
        var phasesAfterFill = native.Phases.Count;
        Assert.True(phasesAfterFill >= 2);

        armed.Armed = true;
        var output = await journal.AppendAsync(
            EventClass.Output, EventReliability.Output,
            "terminal.output",
            """{"pane_id":"p1","encoding":"base64","data":"Yg==","byte_count":1}""");
        Assert.False(output.IsOk);
        Assert.False(armed.CalledWhileArmed);
        Assert.Equal(phasesAfterFill, native.Phases.Count);
        Assert.Single(Directory.GetFiles(_paths.JournalDirectory, "*.hyjr"));

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 20);
        Assert.True(range.IsOk);
        Assert.Equal(2, range.Value.Count);
        Assert.DoesNotContain(range.Value, r => r.Type == "terminal.output");
        Assert.Equal(fill.Value.Seq + 1, journal.NextSeq);
    }

    [Fact]
    public async Task Output_append_at_byte_rotation_does_not_call_durable_sync()
    {
        var native = new RecordingDurableNativeSync();
        var armed = new ArmableDurableFileSync(new DurableFileSync(native));
        await using var journal = await PrepareAsync(
            "rs_h26_rotb",
            armed,
            rotateBytes: 64);

        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);
        var phasesAfterSeed = native.Phases.Count;
        Assert.True(phasesAfterSeed >= 2);

        armed.Armed = true;
        var output = await journal.AppendAsync(
            EventClass.Output, EventReliability.Output,
            "terminal.output",
            """{"pane_id":"p1","encoding":"base64","data":"YQ==","byte_count":1}""");
        Assert.False(output.IsOk);
        Assert.False(armed.CalledWhileArmed);
        Assert.Equal(phasesAfterSeed, native.Phases.Count);
        Assert.Single(Directory.GetFiles(_paths.JournalDirectory, "*.hyjr"));

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 20);
        Assert.True(range.IsOk);
        Assert.Single(range.Value);
        Assert.Equal(seed.Value.Seq, range.Value[0].Seq);
        Assert.DoesNotContain(range.Value, r => r.Type == "terminal.output");
    }

    [Fact]
    public async Task Utf8_output_append_at_byte_rotation_does_not_call_durable_sync()
    {
        var native = new RecordingDurableNativeSync();
        var armed = new ArmableDurableFileSync(new DurableFileSync(native));
        await using var journal = await PrepareAsync(
            "rs_h26_rotb_utf8",
            armed,
            rotateBytes: 64);

        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);
        var phasesAfterSeed = native.Phases.Count;
        Assert.True(phasesAfterSeed >= 2);

        armed.Armed = true;
        using var buffer = new PooledUtf8Buffer(64);
        Assert.True(TerminalOutputPayloadWriter.TryWriteTerminalOutput(buffer, "p1", "A"u8));
        var rotated = await journal.AppendUtf8Async(
            EventClass.Output, EventReliability.Output,
            "terminal.output", buffer.WrittenMemory);
        Assert.False(rotated.IsOk);
        Assert.False(armed.CalledWhileArmed);
        Assert.Equal(phasesAfterSeed, native.Phases.Count);
        Assert.Single(Directory.GetFiles(_paths.JournalDirectory, "*.hyjr"));
    }

    [Fact]
    public async Task Reliable_append_at_record_rotation_still_syncs()
    {
        var native = new RecordingDurableNativeSync();
        await using var journal = await PrepareAsync(
            "rs_h26_rot_rel",
            new DurableFileSync(native),
            rotateRecords: 2);

        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);
        var fill = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p2"}""");
        Assert.True(fill.IsOk, fill.IsOk ? null : fill.Error.Message);
        var phasesAfterFill = native.Phases.Count;

        var reliable = await journal.AppendAsync(
            EventClass.Control, EventReliability.Reliable,
            "lease.changed", """{"lease_id":"l1"}""");
        Assert.True(reliable.IsOk, reliable.IsOk ? null : reliable.Error.Message);

        // Close file+dir, new-segment header file+dir, record file+dir.
        Assert.Equal(phasesAfterFill + 6, native.Phases.Count);
        AssertFileThenDirectoryPairs(native.Phases);
        Assert.Equal("file", native.Phases[^2]);
        Assert.Equal("directory", native.Phases[^1]);

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 20);
        Assert.True(range.IsOk);
        Assert.Equal(3, range.Value.Count);
        Assert.Equal(EventClass.Control, range.Value[2].Class);
        Assert.Equal(reliable.Value.Seq, range.Value[2].Seq);
    }

    [Fact]
    public async Task Output_rotation_replays_in_seq_order_after_journal_restart()
    {
        IReadOnlyList<RuntimeEventRecord> before;
        await using (var journal = await PrepareAsync("rs_h26_rot_replay", rotateRecords: 2))
        {
            var seed = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                "pane.lifecycle", """{"pane_id":"p1"}""");
            Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);
            var fill = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                "pane.lifecycle", """{"pane_id":"p2"}""");
            Assert.True(fill.IsOk, fill.IsOk ? null : fill.Error.Message);
            var output = await journal.AppendAsync(
                EventClass.Output, EventReliability.Output,
                "terminal.output",
                """{"pane_id":"p1","encoding":"base64","data":"Yg==","byte_count":1}""");
            Assert.False(output.IsOk);
            var rotated = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                "pane.lifecycle", """{"pane_id":"p3"}""");
            Assert.True(rotated.IsOk, rotated.IsOk ? null : rotated.Error.Message);

            var range = await journal.ReadRangeAsync(0, classes: null, budget: 20);
            Assert.True(range.IsOk);
            Assert.Equal(3, range.Value.Count);
            before = range.Value;
        }

        var store = new SqliteJournalManifestStore(_paths);
        await using var recovered = new FileRuntimeEventJournal(
            _paths, store, "rs_h26_rot_replay", rotateRecords: 2);
        Assert.True((await recovered.RecoverAsync()).IsOk);
        var after = await recovered.ReadRangeAsync(0, classes: null, budget: 20);
        Assert.True(after.IsOk);
        Assert.Equal(before.Select(r => r.Seq), after.Value.Select(r => r.Seq));
        Assert.Equal(before.Select(r => r.Class), after.Value.Select(r => r.Class));
        Assert.Equal(before.Select(r => r.PayloadJson), after.Value.Select(r => r.PayloadJson));
        Assert.Equal(before[^1].Seq + 1, recovered.NextSeq);
    }

    [Fact]
    public async Task Reliable_append_still_syncs_file_then_directory()
    {
        var native = new RecordingDurableNativeSync();
        await using var journal = await PrepareAsync("rs_h26_rel", new DurableFileSync(native));

        var seed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);

        var beforeOutput = native.Phases.Count;
        var output = await journal.AppendAsync(
            EventClass.Output, EventReliability.Output,
            "terminal.output",
            """{"pane_id":"p1","encoding":"base64","data":"YQ==","byte_count":1}""");
        Assert.False(output.IsOk);
        Assert.Equal(beforeOutput, native.Phases.Count);
        var phasesAfterOutput = native.Phases.Count;

        var reliable = await journal.AppendAsync(
            EventClass.Control, EventReliability.Reliable,
            "lease.changed", """{"lease_id":"l1"}""");
        Assert.True(reliable.IsOk, reliable.IsOk ? null : reliable.Error.Message);

        Assert.Equal(phasesAfterOutput + 2, native.Phases.Count);
        Assert.Equal("file", native.Phases[^2]);
        Assert.Equal("directory", native.Phases[^1]);
        AssertFileThenDirectoryPairs(native.Phases);
    }

    [Fact]
    public async Task Output_then_reliable_still_reads_output_in_seq_order()
    {
        await using var journal = await PrepareAsync("rs_h26_ord");

        var before = journal.NextSeq;
        var output = await journal.AppendAsync(
            EventClass.Output, EventReliability.Output,
            "terminal.output",
            """{"pane_id":"p1","encoding":"base64","data":"YQ==","byte_count":1}""");
        Assert.False(output.IsOk);
        Assert.Equal(before, journal.NextSeq);

        var reliable = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.True(reliable.IsOk, reliable.IsOk ? null : reliable.Error.Message);

        var range = await journal.ReadRangeAsync(0, classes: null, budget: 20);
        Assert.True(range.IsOk);
        Assert.Single(range.Value);
        Assert.Equal(EventClass.Lifecycle, range.Value[0].Class);
        Assert.Equal(reliable.Value.Seq, range.Value[0].Seq);
        Assert.DoesNotContain(range.Value, r => r.Type == "terminal.output");
        Assert.Equal(range.Value[^1].Seq + 1, journal.NextSeq);
    }

    [Fact]
    public async Task AppendAsync_refuses_Render_and_writes_no_HYJR()
    {
        await using var journal = await PrepareAsync("rs_h25_render");
        var life = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");
        Assert.True(life.IsOk, life.IsOk ? null : life.Error.Message);
        var nextAfterLife = journal.NextSeq;

        var refusedClass = await journal.AppendAsync(
            EventClass.Render, EventReliability.Render,
            "terminal.render",
            """{"pane_id":"p1","encoding":"base64","data":"YQ==","byte_count":1}""");
        Assert.False(refusedClass.IsOk);
        Assert.Contains("live-only", refusedClass.Error.Message, StringComparison.OrdinalIgnoreCase);

        var refusedRel = await journal.AppendAsync(
            EventClass.Output, EventReliability.Render,
            "terminal.output",
            """{"pane_id":"p1","encoding":"base64","data":"YQ==","byte_count":1}""");
        Assert.False(refusedRel.IsOk);

        var refusedOutput = await journal.AppendAsync(
            EventClass.Output, EventReliability.Output,
            "terminal.output",
            """{"pane_id":"p1","encoding":"base64","data":"YQ==","byte_count":1}""");
        Assert.False(refusedOutput.IsOk);
        Assert.Contains("live-only", refusedOutput.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(nextAfterLife, journal.NextSeq);
        var range = await journal.ReadRangeAsync(0, classes: null, budget: 50);
        Assert.True(range.IsOk);
        Assert.DoesNotContain(range.Value, r => r.Class == EventClass.Render);
        Assert.DoesNotContain(range.Value, r => r.Reliability == EventReliability.Render);
        Assert.Single(range.Value);
    }

    [Fact]
    public async Task New_segment_create_syncs_file_then_directory()
    {
        var native = new RecordingDurableNativeSync();
        await using var journal = await PrepareAsync("rs_h22_seg", new DurableFileSync(native));

        var first = await journal.AppendAsync(
            EventClass.Control, EventReliability.Reliable,
            "lease.changed", """{"lease_id":"l1"}""");
        Assert.True(first.IsOk, first.IsOk ? null : first.Error.Message);

        Assert.Equal("file", native.Phases[0]);
        Assert.Equal("directory", native.Phases[1]);
        AssertFileThenDirectoryPairs(native.Phases);
    }

    [Fact]
    public async Task Directory_phase_failure_fails_append_and_gaps_its_sequence()
    {
        var native = new RecordingDurableNativeSync
        {
            DirectoryFailure = new IOException("injected directory-phase failure"),
        };
        await using var journal = await PrepareAsync("rs_h22_crash", new DurableFileSync(native));
        var before = journal.NextSeq;

        var appended = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            "pane.lifecycle", """{"pane_id":"p1"}""");

        Assert.False(appended.IsOk);
        Assert.Contains("directory-phase failure", appended.Error.Message, StringComparison.Ordinal);
        Assert.Contains("file", native.Phases);
        Assert.Contains("directory", native.Phases);
        // The failed record may already be live, so its sequence is used up and
        // the floor moves past it. The next record never reuses it.
        Assert.Equal(before + 1, journal.NextSeq);
        Assert.Equal(before + 1, journal.GetHealth().FloorSeq);
    }

    [Fact]
    public async Task Directory_phase_failure_recover_does_not_invent_a_durable_record()
    {
        var native = new RecordingDurableNativeSync
        {
            DirectoryFailure = new IOException("injected directory-phase failure"),
        };
        var before = 1L;
        await using (var journal = await PrepareAsync("rs_h22_crash2", new DurableFileSync(native)))
        {
            before = journal.NextSeq;
            var appended = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                "pane.lifecycle", """{"pane_id":"p1"}""");
            Assert.False(appended.IsOk);
        }

        var store = new SqliteJournalManifestStore(_paths);
        await using var recovered = new FileRuntimeEventJournal(_paths, store, "rs_h22_crash2");
        Assert.True((await recovered.RecoverAsync()).IsOk);
        Assert.Equal(before + 1, recovered.NextSeq);
        var floor = recovered.GetHealth().FloorSeq;
        Assert.Equal(before + 1, floor);
        var range = await recovered.ReadRangeAsync(floor - 1, classes: null, budget: 20, CancellationToken.None);
        Assert.True(range.IsOk);
        Assert.Empty(range.Value);
    }

    [Fact]
    public void SyncFileThenDirectory_directory_phase_absent_is_observable()
    {
        var native = new RecordingDurableNativeSync();
        var sync = new DurableFileSync(native);
        var path = Path.Combine(_dir, "phase.bin");
        File.WriteAllText(path, "x");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        sync.SyncFileThenDirectory(fs.SafeFileHandle, path);

        Assert.Equal(new[] { "file", "directory" }, native.Phases);
    }

    [Fact]
    public void MacOS_uses_F_FULLFSYNC_or_documented_equal()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("F_FULLFSYNC", DurableFileSync.FileSyncKind);
            Assert.Equal(51, DurableFileSync.MacOsFFullFsync);
            return;
        }

        if (OperatingSystem.IsWindows())
            Assert.Equal("FlushFileBuffers", DurableFileSync.FileSyncKind);
        else
            Assert.Equal("fsync", DurableFileSync.FileSyncKind);
    }

    private static void AssertFileThenDirectoryPairs(IReadOnlyList<string> order)
    {
        Assert.True(order.Count >= 2, "expected at least one file-then-directory pair");
        Assert.True(order.Count % 2 == 0, "sync phases must be file then directory pairs: " + string.Join(",", order));
        for (var i = 0; i < order.Count; i += 2)
        {
            Assert.Equal("file", order[i]);
            Assert.Equal("directory", order[i + 1]);
        }
    }

    private sealed class RecordingDurableNativeSync : IDurableNativeSync
    {
        public List<string> Phases { get; } = [];

        public Exception? DirectoryFailure { get; init; }

        public string FileSyncKind => "test";

        public void SyncHandle(SafeFileHandle handle) => Phases.Add("file");

        public void SyncDirectory(string directoryPath)
        {
            Phases.Add("directory");
            if (DirectoryFailure is not null)
                throw DirectoryFailure;
        }
    }

    private sealed class ArmableDurableFileSync : IDurableFileSync
    {
        private readonly IDurableFileSync _inner;

        public ArmableDurableFileSync(IDurableFileSync inner) =>
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public bool Armed { get; set; }

        public bool CalledWhileArmed { get; private set; }

        public void SyncFileThenDirectory(SafeFileHandle fileHandle, string filePath)
        {
            if (Armed)
                CalledWhileArmed = true;
            _inner.SyncFileThenDirectory(fileHandle, filePath);
        }
    }
}
