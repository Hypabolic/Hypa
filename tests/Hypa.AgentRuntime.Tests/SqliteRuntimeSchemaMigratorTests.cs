using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class SqliteRuntimeSchemaMigratorTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public SqliteRuntimeSchemaMigratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h02-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    [Fact]
    public async Task Migrator_on_fresh_db_creates_all_Appendix_A_tables_including_journal_segments_and_scrollback_segments()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        var result = await migrator.MigrateAsync();
        Assert.True(result.IsOk, result.IsOk ? null : result.Error.Message);

        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));
        }

        foreach (var table in SqliteRuntimeSchemaMigrator.NormativeTables)
            Assert.Contains(table, names);

        Assert.Contains("journal_segments", names);
        Assert.Contains("scrollback_segments", names);
    }

    [Fact]
    public async Task Migrator_stamps_runtime_meta_schema_version_1_and_runtime_session_id()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();

        await using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = "SELECT value FROM runtime_meta WHERE key='schema_version'";
        Assert.Equal("1", (string?)await versionCmd.ExecuteScalarAsync());

        await using var sidCmd = conn.CreateCommand();
        sidCmd.CommandText = "SELECT value FROM runtime_meta WHERE key='runtime_session_id'";
        var sid = (string?)await sidCmd.ExecuteScalarAsync();
        Assert.False(string.IsNullOrWhiteSpace(sid));
    }

    [Fact]
    public async Task Migrator_is_idempotent_on_existing_v1_db()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        await using var sidCmd = conn.CreateCommand();
        sidCmd.CommandText = "SELECT value FROM runtime_meta WHERE key='runtime_session_id'";
        var firstSid = (string?)await sidCmd.ExecuteScalarAsync();

        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using var sidCmd2 = conn.CreateCommand();
        sidCmd2.CommandText = "SELECT value FROM runtime_meta WHERE key='runtime_session_id'";
        var secondSid = (string?)await sidCmd2.ExecuteScalarAsync();
        Assert.Equal(firstSid, secondSid);
    }

    [Fact]
    public async Task EnsurePaneAuthorityColumn_is_idempotent()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        await SqliteRuntimeSchemaMigrator.EnsurePaneAuthorityColumnAsync(conn, CancellationToken.None);
        await SqliteRuntimeSchemaMigrator.EnsurePaneAuthorityColumnAsync(conn, CancellationToken.None);

        await using var cols = conn.CreateCommand();
        cols.CommandText = "PRAGMA table_info(panes)";
        await using var reader = await cols.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            names.Add(reader.GetString(1));
        Assert.Contains("agent_authority_json", names);

        await using var version = conn.CreateCommand();
        version.CommandText = "SELECT value FROM runtime_meta WHERE key='schema_version'";
        Assert.Equal("1", (string?)await version.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EnsurePaneAgentNameColumn_is_idempotent()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        await SqliteRuntimeSchemaMigrator.EnsurePaneAgentNameColumnAsync(conn, CancellationToken.None);
        await SqliteRuntimeSchemaMigrator.EnsurePaneAgentNameColumnAsync(conn, CancellationToken.None);

        await using var cols = conn.CreateCommand();
        cols.CommandText = "PRAGMA table_info(panes)";
        await using var reader = await cols.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            names.Add(reader.GetString(1));
        Assert.Contains("agent_name", names);

        await using var version = conn.CreateCommand();
        version.CommandText = "SELECT value FROM runtime_meta WHERE key='schema_version'";
        Assert.Equal("1", (string?)await version.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Migrator_fails_closed_when_schema_version_is_future()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE runtime_meta SET value='99' WHERE key='schema_version'";
            await cmd.ExecuteNonQueryAsync();
        }

        SqliteTestCleanup.ReleaseDatabase(_paths.DatabasePath);
        var result = await migrator.MigrateAsync();
        Assert.False(result.IsOk);
        Assert.Equal(RuntimePersistenceError.FutureSchema, result.Error.Code);
    }

    [Fact]
    public async Task Migrator_upgrades_early_v1_db_with_event_exports_table()
    {
        // Luna P1: schema_version already = current short-circuits to Ensure* helpers only.
        // Early v1 DBs stamped without event_exports must still receive the table.
        Directory.CreateDirectory(_paths.StateDirectory);
        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var seed = conn.CreateCommand();
            // Minimal early-v1 surface: meta + sessions (FK target) without event_exports.
            seed.CommandText = """
                CREATE TABLE runtime_meta (
                  key TEXT PRIMARY KEY,
                  value TEXT NOT NULL
                );
                CREATE TABLE sessions (
                  session_id TEXT PRIMARY KEY,
                  name TEXT NOT NULL UNIQUE,
                  lifecycle_state TEXT NOT NULL,
                  placement TEXT NOT NULL,
                  placement_generation INTEGER NOT NULL DEFAULT 0,
                  started_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  replay_complete INTEGER NOT NULL DEFAULT 1,
                  replay_error TEXT
                );
                INSERT INTO runtime_meta(key, value) VALUES ('schema_version', '1');
                INSERT INTO runtime_meta(key, value) VALUES ('runtime_session_id', 'earlyv1session');
                INSERT INTO runtime_meta(key, value) VALUES ('event_seq', '0');
                INSERT INTO sessions(
                  session_id, name, lifecycle_state, placement,
                  placement_generation, started_at, updated_at, replay_complete)
                VALUES (
                  'earlyv1session', 'early', 'ready', 'local',
                  0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        SqliteTestCleanup.ReleaseDatabase(_paths.DatabasePath);

        // Precondition: event_exports missing while schema_version is already current.
        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var check = conn.CreateCommand();
            check.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='event_exports'";
            Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
        }

        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var check = conn.CreateCommand();
            check.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='event_exports'";
            Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);

            // Unique(session_id, consumer) must exist for upsert semantics.
            await using var info = conn.CreateCommand();
            info.CommandText = "PRAGMA index_list(event_exports)";
            var hasUnique = false;
            await using (var reader = await info.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    // seq, name, unique, origin, partial
                    if (reader.GetInt32(2) == 1)
                        hasUnique = true;
                }
            }
            Assert.True(hasUnique, "event_exports must retain UNIQUE(session_id,consumer)");

            // Idempotent: second migrate leaves table intact.
            Assert.True((await migrator.MigrateAsync()).IsOk);
            await using var check2 = conn.CreateCommand();
            check2.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='event_exports'";
            Assert.Equal(1L, (long)(await check2.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Migrator_early_v1_event_exports_accepts_ack_upsert()
    {
        // End-to-end: upgrade then SqliteRuntimeEvidenceJournal.AckAsync against the new table.
        Directory.CreateDirectory(_paths.StateDirectory);
        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var seed = conn.CreateCommand();
            seed.CommandText = """
                CREATE TABLE runtime_meta (
                  key TEXT PRIMARY KEY,
                  value TEXT NOT NULL
                );
                CREATE TABLE sessions (
                  session_id TEXT PRIMARY KEY,
                  name TEXT NOT NULL UNIQUE,
                  lifecycle_state TEXT NOT NULL,
                  placement TEXT NOT NULL,
                  placement_generation INTEGER NOT NULL DEFAULT 0,
                  started_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  replay_complete INTEGER NOT NULL DEFAULT 1,
                  replay_error TEXT
                );
                INSERT INTO runtime_meta(key, value) VALUES ('schema_version', '1');
                INSERT INTO runtime_meta(key, value) VALUES ('runtime_session_id', 'ackupgrade');
                INSERT INTO sessions(
                  session_id, name, lifecycle_state, placement,
                  placement_generation, started_at, updated_at, replay_complete)
                VALUES (
                  'ackupgrade', 'ack', 'ready', 'local',
                  0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        SqliteTestCleanup.ReleaseDatabase(_paths.DatabasePath);
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var evidence = new SqliteRuntimeEvidenceJournal(_paths);
        var ack = await evidence.AckAsync(
            sessionId: "ackupgrade",
            exportId: "exp_upgrade_1",
            lastSeq: 7,
            consumer: "atomic-test");
        Assert.True(ack.IsOk, ack.IsOk ? null : ack.Error.Message);
        Assert.Equal(7, ack.Value.LastAckedSeq);

        var read = await evidence.GetAsync("exp_upgrade_1");
        Assert.True(read.IsOk);
        Assert.NotNull(read.Value);
        Assert.Equal(7, read.Value!.LastAckedSeq);
        Assert.Equal("atomic-test", read.Value.Consumer);
    }

    [Fact]
    public async Task Migrator_upgrades_early_v1_db_with_checkpoints_table()
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var seed = conn.CreateCommand();
            seed.CommandText = """
                CREATE TABLE runtime_meta (
                  key TEXT PRIMARY KEY,
                  value TEXT NOT NULL
                );
                CREATE TABLE sessions (
                  session_id TEXT PRIMARY KEY,
                  name TEXT NOT NULL UNIQUE,
                  lifecycle_state TEXT NOT NULL,
                  placement TEXT NOT NULL,
                  placement_generation INTEGER NOT NULL DEFAULT 0,
                  started_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  replay_complete INTEGER NOT NULL DEFAULT 1,
                  replay_error TEXT
                );
                INSERT INTO runtime_meta(key, value) VALUES ('schema_version', '1');
                INSERT INTO runtime_meta(key, value) VALUES ('runtime_session_id', 'cpsearly');
                INSERT INTO sessions(
                  session_id, name, lifecycle_state, placement,
                  placement_generation, started_at, updated_at, replay_complete)
                VALUES (
                  'cpsearly', 'cp', 'ready', 'local',
                  0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        SqliteTestCleanup.ReleaseDatabase(_paths.DatabasePath);

        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var check = conn.CreateCommand();
            check.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='checkpoints'";
            Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
        }

        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var check = conn.CreateCommand();
            check.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='checkpoints'";
            Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);

            // Idempotent second migrate.
            Assert.True((await migrator.MigrateAsync()).IsOk);
            await using var check2 = conn.CreateCommand();
            check2.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='checkpoints'";
            Assert.Equal(1L, (long)(await check2.ExecuteScalarAsync())!);

            await using var cols = conn.CreateCommand();
            cols.CommandText = "PRAGMA table_info(checkpoints)";
            await using var reader = await cols.ExecuteReaderAsync();
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
                names.Add(reader.GetString(1));
            Assert.Contains("project_root_device", names);
            Assert.Contains("project_root_inode", names);
            Assert.Contains("project_root_was_symlink", names);
        }
    }

    [Fact]
    public async Task Migrator_upgrades_early_v1_db_with_snapshot_columns_then_store_round_trips()
    {
        // Luna P1: schema_version==1 short-circuits to Ensure* only. Early v1
        // sessions/tabs/panes without the columns must still receive them
        // or UpsertSession/Tab/Pane fails on INSERT of the new names.
        Directory.CreateDirectory(_paths.StateDirectory);
        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var seed = conn.CreateCommand();
            seed.CommandText = """
                CREATE TABLE runtime_meta (
                  key TEXT PRIMARY KEY,
                  value TEXT NOT NULL
                );
                CREATE TABLE sessions (
                  session_id TEXT PRIMARY KEY,
                  name TEXT NOT NULL UNIQUE,
                  lifecycle_state TEXT NOT NULL,
                  placement TEXT NOT NULL,
                  placement_generation INTEGER NOT NULL DEFAULT 0,
                  started_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  replay_complete INTEGER NOT NULL DEFAULT 1,
                  replay_error TEXT
                );
                CREATE TABLE workspaces (
                  workspace_id TEXT PRIMARY KEY,
                  session_id TEXT NOT NULL REFERENCES sessions(session_id),
                  label TEXT,
                  cwd TEXT NOT NULL,
                  focused_tab_id TEXT,
                  binding_json TEXT
                );
                CREATE TABLE tabs (
                  tab_id TEXT PRIMARY KEY,
                  workspace_id TEXT NOT NULL REFERENCES workspaces(workspace_id),
                  label TEXT,
                  ordinal INTEGER NOT NULL
                );
                CREATE TABLE panes (
                  pane_id TEXT PRIMARY KEY,
                  tab_id TEXT NOT NULL REFERENCES tabs(tab_id),
                  workspace_id TEXT NOT NULL REFERENCES workspaces(workspace_id),
                  label TEXT NOT NULL,
                  cwd TEXT NOT NULL,
                  command TEXT NOT NULL,
                  cols INTEGER NOT NULL,
                  rows INTEGER NOT NULL,
                  lifecycle_state TEXT NOT NULL,
                  occupant_generation INTEGER NOT NULL DEFAULT 0,
                  pid INTEGER,
                  exit_code INTEGER,
                  agent_kind TEXT,
                  agent_status TEXT NOT NULL,
                  binding_json TEXT,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL
                );
                CREATE TABLE event_cursor (
                  session_id TEXT PRIMARY KEY REFERENCES sessions(session_id),
                  next_seq INTEGER NOT NULL
                );
                INSERT INTO runtime_meta(key, value) VALUES ('schema_version', '1');
                INSERT INTO runtime_meta(key, value) VALUES ('runtime_session_id', 'snapearly');
                INSERT INTO runtime_meta(key, value) VALUES ('event_seq', '0');
                INSERT INTO sessions(
                  session_id, name, lifecycle_state, placement,
                  placement_generation, started_at, updated_at, replay_complete)
                VALUES (
                  'snapearly', 'early-snap', 'ready', 'local',
                  0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        SqliteTestCleanup.ReleaseDatabase(_paths.DatabasePath);

        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            Assert.False(await TableHasColumnAsync(conn, "sessions", "process_tenant_id"));
            Assert.False(await TableHasColumnAsync(conn, "sessions", "process_run_id"));
            Assert.False(await TableHasColumnAsync(conn, "tabs", "focused_pane_id"));
            Assert.False(await TableHasColumnAsync(conn, "panes", "agent_message"));
        }

        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            Assert.True(await TableHasColumnAsync(conn, "sessions", "process_tenant_id"));
            Assert.True(await TableHasColumnAsync(conn, "sessions", "process_run_id"));
            Assert.True(await TableHasColumnAsync(conn, "tabs", "focused_pane_id"));
            Assert.True(await TableHasColumnAsync(conn, "panes", "agent_message"));
        }

        Assert.True((await migrator.MigrateAsync()).IsOk);

        var store = new SqliteRuntimeSessionStore(_paths);
        var sessionId = SessionId.New("h22-upgrade");
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var paneId = PaneId.New();
        var original = new SessionState
        {
            Id = sessionId,
            Name = "h22-upgrade",
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
            ProcessTenantId = "tenant_upgrade",
            ProcessRunId = "run_upgrade",
            StartedAt = DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-08-14T01:00:00Z"),
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/workspace",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [paneId],
                    FocusedPaneId = paneId,
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [paneId.Value] = new PaneState
                {
                    Id = paneId,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "shell",
                    Cwd = "/workspace",
                    Command = "bash",
                    Cols = 80,
                    Rows = 24,
                    LifecycleState = PaneLifecycle.Running,
                    OccupantGeneration = 1,
                    AgentStatus = AgentStatus.Working,
                    AgentMessage = "upgraded snapshot message",
                    CreatedAt = DateTimeOffset.Parse("2026-08-14T00:05:00Z"),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-14T00:06:00Z"),
                },
            },
        };

        var save = await store.SaveAsync(original);
        Assert.True(save.IsOk, save.IsOk ? null : save.Error.Message);

        var load = await store.TryLoadAsync("h22-upgrade");
        Assert.True(load.IsOk, load.IsOk ? null : load.Error.Message);
        Assert.NotNull(load.Value);
        var loaded = load.Value!;
        Assert.Equal("tenant_upgrade", loaded.ProcessTenantId);
        Assert.Equal("run_upgrade", loaded.ProcessRunId);
        Assert.Equal(paneId.Value, loaded.Tabs[tabId.Value].FocusedPaneId?.Value);
        Assert.Equal("upgraded snapshot message", loaded.Panes[paneId.Value].AgentMessage);
    }

    [Fact]
    public async Task Migrator_adds_pane_extras_columns_and_keeps_schema_version_1()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        Assert.True(await TableHasColumnAsync(conn, "panes", "seen"));
        Assert.True(await TableHasColumnAsync(conn, "panes", "right_click"));

        await using var version = conn.CreateCommand();
        version.CommandText = "SELECT value FROM runtime_meta WHERE key='schema_version'";
        Assert.Equal("1", (string?)await version.ExecuteScalarAsync());

        Assert.True((await migrator.MigrateAsync()).IsOk);
        Assert.True(await TableHasColumnAsync(conn, "panes", "seen"));
        Assert.True(await TableHasColumnAsync(conn, "panes", "right_click"));
    }

    private static async Task<bool> TableHasColumnAsync(
        SqliteConnection conn, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(" + table + ")";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
