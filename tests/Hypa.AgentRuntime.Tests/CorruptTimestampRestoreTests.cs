using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class CorruptTimestampRestoreTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public CorruptTimestampRestoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h22-ts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    [Fact]
    public void Parser_never_substitutes_UtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(DateTimeOffset.UnixEpoch, DurableTimestampParser.ParseOrSentinel(null));
        Assert.Equal(DateTimeOffset.UnixEpoch, DurableTimestampParser.ParseOrSentinel(""));
        Assert.Equal(DateTimeOffset.UnixEpoch, DurableTimestampParser.ParseOrSentinel("not-a-date"));
        var parsed = DurableTimestampParser.ParseOrSentinel("2026-08-12T00:00:00Z");
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero), parsed);
        Assert.True((DateTimeOffset.UtcNow - before).TotalSeconds < 30);
        Assert.True(Math.Abs((DurableTimestampParser.ParseOrSentinel("not-a-date") - DateTimeOffset.UtcNow).TotalSeconds) > 60);
    }

    [Fact]
    public async Task Session_started_at_not_a_date_loads_unix_epoch_sentinel()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions(
                  session_id, name, lifecycle_state, placement, placement_generation,
                  started_at, updated_at, replay_complete)
                VALUES ('sess_bad_ts', 'bad-ts', 'ready', 'local', 0, 'not-a-date', 'also-bad', 1);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteRuntimeSessionStore(_paths);
        var load = await store.TryLoadAsync("bad-ts");
        Assert.True(load.IsOk);
        Assert.NotNull(load.Value);
        Assert.Equal(DateTimeOffset.UnixEpoch, load.Value!.StartedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch, load.Value.UpdatedAt);
        AssertNotNearUtcNow(load.Value.StartedAt);
    }

    [Fact]
    public async Task Checkpoint_created_at_corrupt_is_not_near_UtcNow()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var session = conn.CreateCommand();
            session.CommandText = """
                INSERT INTO sessions(
                  session_id, name, lifecycle_state, placement, placement_generation,
                  started_at, updated_at, replay_complete)
                VALUES ('sess_ckpt', 'ckpt-ts', 'ready', 'local', 0, '2026-08-12T00:00:00Z', '2026-08-12T00:00:00Z', 1);
                """;
            await session.ExecuteNonQueryAsync();

            await using var ck = conn.CreateCommand();
            ck.CommandText = """
                INSERT INTO checkpoints(
                  checkpoint_id, session_id, state, barrier_seq, next_seq_at_prepare,
                  session_fingerprint, created_at)
                VALUES ('ckpt_bad', 'sess_ckpt', 'prepared', 0, 1, 'fp', 'not-a-date');
                """;
            await ck.ExecuteNonQueryAsync();
        }

        var store = new FileCheckpointStore(_paths);
        var got = await store.GetAsync("ckpt_bad");
        Assert.True(got.IsOk);
        Assert.NotNull(got.Value);
        Assert.Equal(DateTimeOffset.UnixEpoch, got.Value!.CreatedAt);
        AssertNotNearUtcNow(got.Value.CreatedAt);
    }

    [Fact]
    public void Checkpoint_sidecar_ToDomain_corrupt_created_at_is_sentinel()
    {
        var dto = new CheckpointStorageDto
        {
            CheckpointId = "ckpt_json",
            SessionId = "sess",
            State = "prepared",
            SessionFingerprint = "fp",
            CreatedAt = "not-a-date",
        };
        var domain = dto.ToDomain();
        Assert.Equal(DateTimeOffset.UnixEpoch, domain.CreatedAt);
        AssertNotNearUtcNow(domain.CreatedAt);
    }

    private static void AssertNotNearUtcNow(DateTimeOffset value)
    {
        Assert.True(
            Math.Abs((value - DateTimeOffset.UtcNow).TotalSeconds) > 60,
            "restored timestamp must not be invented as UtcNow; was " + value);
    }
}
