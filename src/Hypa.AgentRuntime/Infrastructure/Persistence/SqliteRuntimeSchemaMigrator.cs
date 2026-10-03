using Hypa.AgentRuntime.Application;
using Microsoft.Data.Sqlite;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Applies Appendix A schema v1 to <c>runtime.db</c> under the session state root.
/// </summary>
public sealed class SqliteRuntimeSchemaMigrator : IRuntimeSchemaMigrator
{
    private readonly RuntimeStatePaths _paths;

    public SqliteRuntimeSchemaMigrator(RuntimeStatePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<RuntimeResult<RuntimeUnit>> MigrateAsync(CancellationToken ct = default)
    {
        try
        {
            _paths.EnsureDirectory();

            await using var conn = Open();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await ApplyPragmasAsync(conn, ct).ConfigureAwait(false);

            var version = await ReadSchemaVersionAsync(conn, ct).ConfigureAwait(false);
            if (version is null)
            {
                await ApplyV1Async(conn, ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }

            if (version.Value == RuntimePersistenceSchema.Version)
            {
                // Additive columns / tables may be missing on early v1 DBs.
                await EnsureFocusedWorkspaceColumnAsync(conn, ct).ConfigureAwait(false);
                await EnsureSessionBindingColumnsAsync(conn, ct).ConfigureAwait(false);
                await EnsureSnapshotColumnsAsync(conn, ct).ConfigureAwait(false);
                await EnsureLayoutColumnsAsync(conn, ct).ConfigureAwait(false);
                await EnsurePaneExtrasColumnsAsync(conn, ct).ConfigureAwait(false);
                await EnsurePaneAuthorityColumnAsync(conn, ct).ConfigureAwait(false);
                await EnsurePaneHiddenColumnAsync(conn, ct).ConfigureAwait(false);
                await EnsurePaneParentColumnAsync(conn, ct).ConfigureAwait(false);
                await EnsurePaneAgentNameColumnAsync(conn, ct).ConfigureAwait(false);
                await EnsureWorkspaceOrderColumnsAsync(conn, ct).ConfigureAwait(false);
                await EnsureWorkspaceWorktreeColumnAsync(conn, ct).ConfigureAwait(false);
                await EnsureWorkspaceDefaultPanePendingColumnAsync(conn, ct).ConfigureAwait(false);
                await EnsureEventExportsTableAsync(conn, ct).ConfigureAwait(false);
                await EnsureCheckpointsTableAsync(conn, ct).ConfigureAwait(false);
                await EnsureEventCursorFloorColumnAsync(conn, ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }

            if (version.Value > RuntimePersistenceSchema.Version)
            {
                return RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Future(version.Value, RuntimePersistenceSchema.Version));
            }

            // Older than supported: treat as missing and re-apply (only v1 exists today).
            await ApplyV1Async(conn, ct).ConfigureAwait(false);
            return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
        }
        catch (SqliteException ex)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Db(ex.Message));
        }
        catch (IOException ex)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Access(ex.Message));
        }
    }

    private SqliteConnection Open() =>
        new($"Data Source={_paths.DatabasePath}");

    internal static async Task ApplyPragmasAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<int?> ReadSchemaVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using (var tableCheck = conn.CreateCommand())
        {
            tableCheck.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='runtime_meta'";
            var exists = (long)(await tableCheck.ExecuteScalarAsync(ct).ConfigureAwait(false))! > 0;
            if (!exists)
                return null;
        }

        await using var read = conn.CreateCommand();
        read.CommandText =
            "SELECT value FROM runtime_meta WHERE key = @key";
        read.Parameters.AddWithValue("@key", RuntimePersistenceSchema.SchemaVersionKey);
        var raw = (string?)await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is null || !int.TryParse(raw, out var version))
            return null;
        return version;
    }

    private static async Task ApplyV1Async(SqliteConnection conn, CancellationToken ct)
    {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = V1Ddl;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Seed meta only when missing so restarts of the same state root keep runtime_session_id.
        var runtimeSessionId = await ReadMetaAsync(conn, tx, RuntimePersistenceSchema.RuntimeSessionIdKey, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(runtimeSessionId))
            runtimeSessionId = Guid.NewGuid().ToString("N");

        await UpsertMetaAsync(conn, tx, RuntimePersistenceSchema.SchemaVersionKey,
            RuntimePersistenceSchema.Version.ToString(), ct).ConfigureAwait(false);
        await UpsertMetaAsync(conn, tx, RuntimePersistenceSchema.RuntimeSessionIdKey,
            runtimeSessionId, ct).ConfigureAwait(false);
        await UpsertMetaAsync(conn, tx, RuntimePersistenceSchema.EventSeqKey, "0", ct)
            .ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

        // Ensure additive columns/tables exist even if an older V1Ddl omitted them.
        await EnsureFocusedWorkspaceColumnAsync(conn, ct).ConfigureAwait(false);
        await EnsureSessionBindingColumnsAsync(conn, ct).ConfigureAwait(false);
        await EnsureSnapshotColumnsAsync(conn, ct).ConfigureAwait(false);
        await EnsureLayoutColumnsAsync(conn, ct).ConfigureAwait(false);
        await EnsurePaneExtrasColumnsAsync(conn, ct).ConfigureAwait(false);
        await EnsurePaneAuthorityColumnAsync(conn, ct).ConfigureAwait(false);
        await EnsurePaneHiddenColumnAsync(conn, ct).ConfigureAwait(false);
        await EnsurePaneParentColumnAsync(conn, ct).ConfigureAwait(false);
        await EnsurePaneAgentNameColumnAsync(conn, ct).ConfigureAwait(false);
        await EnsureWorkspaceOrderColumnsAsync(conn, ct).ConfigureAwait(false);
        await EnsureWorkspaceWorktreeColumnAsync(conn, ct).ConfigureAwait(false);
        await EnsureWorkspaceDefaultPanePendingColumnAsync(conn, ct).ConfigureAwait(false);
        await EnsureEventExportsTableAsync(conn, ct).ConfigureAwait(false);
        await EnsureCheckpointsTableAsync(conn, ct).ConfigureAwait(false);
        await EnsureEventCursorFloorColumnAsync(conn, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Additive column: event_cursor.floor_seq. The retention floor.
    /// Keeps schema version at 1. Safe to call repeatedly.
    /// </summary>
    internal static async Task EnsureEventCursorFloorColumnAsync(
        SqliteConnection conn, CancellationToken ct) =>
        await EnsureTableColumnAsync(conn, "event_cursor", "floor_seq", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Additive column: sessions.focused_workspace_id (nullable TEXT).
    /// Safe to call repeatedly; no-ops when the column already exists.
    /// </summary>
    internal static async Task EnsureFocusedWorkspaceColumnAsync(
        SqliteConnection conn, CancellationToken ct) =>
        await EnsureSessionsColumnAsync(conn, "focused_workspace_id", "TEXT", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Additive columns: sessions.binding_json (TEXT) and sessions.governed (INTEGER default 0).
    /// Keeps schema version at 1. Safe to call repeatedly.
    /// </summary>
    internal static async Task EnsureSessionBindingColumnsAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        await EnsureSessionsColumnAsync(conn, "binding_json", "TEXT", ct).ConfigureAwait(false);
        await EnsureSessionsColumnAsync(conn, "governed", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Additive live-snapshot columns. Keeps schema version at 1.
    /// sessions.process_tenant_id / process_run_id, tabs.focused_pane_id, panes.agent_message.
    /// Safe to call repeatedly. Does not add Atomic consumer columns.
    /// </summary>
    internal static async Task EnsureSnapshotColumnsAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        await EnsureSessionsColumnAsync(conn, "process_tenant_id", "TEXT", ct).ConfigureAwait(false);
        await EnsureSessionsColumnAsync(conn, "process_run_id", "TEXT", ct).ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "tabs", "focused_pane_id", "TEXT", ct).ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "panes", "agent_message", "TEXT", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Additive columns: tabs.layout_json, tabs.zoomed, tabs.zoomed_pane_id.
    /// Keeps schema version at 1. Safe to call repeatedly.
    /// </summary>
    internal static async Task EnsureLayoutColumnsAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        await EnsureTableColumnAsync(conn, "tabs", "identity_pane_id", "TEXT", ct).ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "workspaces", "custom_label", "INTEGER", ct).ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "tabs", "layout_json", "TEXT", ct).ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "tabs", "zoomed", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "tabs", "zoomed_pane_id", "TEXT", ct).ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "tabs", "custom_label", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "panes", "args_json", "TEXT", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Additive columns: panes.seen, panes.right_click.
    /// Keeps schema version at 1. Safe to call repeatedly.
    /// </summary>
    internal static async Task EnsurePaneExtrasColumnsAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        await EnsureTableColumnAsync(conn, "panes", "seen", "INTEGER NOT NULL DEFAULT 1", ct)
            .ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "panes", "right_click", "TEXT NOT NULL DEFAULT 'hypa'", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Additive workspace ordering column; schema version remains 1.</summary>
    internal static async Task EnsureWorkspaceOrderColumnsAsync(
        SqliteConnection conn, CancellationToken ct) =>
        await EnsureTableColumnAsync(conn, "workspaces", "ordinal", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);

    /// <summary>Additive worktree provenance column. Schema version stays 1.</summary>
    internal static async Task EnsureWorkspaceWorktreeColumnAsync(
        SqliteConnection conn, CancellationToken ct) =>
        await EnsureTableColumnAsync(conn, "workspaces", "worktree_json", "TEXT", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Additive default-pane create marker. Schema version stays 1.
    /// Historical rows default to 0 so intentional workspace-only spaces stay.
    /// </summary>
    internal static async Task EnsureWorkspaceDefaultPanePendingColumnAsync(
        SqliteConnection conn, CancellationToken ct) =>
        await EnsureTableColumnAsync(
                conn,
                "workspaces",
                "default_pane_pending",
                "INTEGER NOT NULL DEFAULT 0",
                ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Additive column: panes.agent_authority_json (nullable TEXT).
    /// Keeps schema version at 1. Safe to call repeatedly.
    /// </summary>
    internal static async Task EnsurePaneAuthorityColumnAsync(
        SqliteConnection conn, CancellationToken ct) =>
        await EnsureTableColumnAsync(conn, "panes", "agent_authority_json", "TEXT", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Additive hidden-occupancy column: panes.hidden INTEGER NOT NULL DEFAULT 0.
    /// Keeps schema version at 1. Safe to call repeatedly.
    /// </summary>
    internal static async Task EnsurePaneHiddenColumnAsync(
        SqliteConnection conn, CancellationToken ct) =>
        await EnsureTableColumnAsync(conn, "panes", "hidden", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Additive parent provenance column: panes.parent_pane_id TEXT.
    /// Keeps schema version at 1. Safe to call repeatedly.
    /// </summary>
    internal static async Task EnsurePaneParentColumnAsync(
        SqliteConnection conn, CancellationToken ct) =>
        await EnsureTableColumnAsync(conn, "panes", "parent_pane_id", "TEXT", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Additive panes.agent_name (nullable TEXT). Distinct from the pane label.
    /// Keeps schema version at 1. Safe to call repeatedly.
    /// </summary>
    internal static async Task EnsurePaneAgentNameColumnAsync(
        SqliteConnection conn, CancellationToken ct) =>
        await EnsureTableColumnAsync(conn, "panes", "agent_name", "TEXT", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Additive table: event_exports (export ack cursors).
    /// Early schema-v1 databases stamped schema_version=1 before this table existed.
    /// Safe to call repeatedly; CREATE TABLE IF NOT EXISTS + UNIQUE are idempotent.
    /// </summary>
    internal static async Task EnsureEventExportsTableAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS event_exports (
              export_id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL REFERENCES sessions(session_id),
              consumer TEXT NOT NULL,
              last_acked_seq INTEGER NOT NULL DEFAULT 0,
              updated_at TEXT NOT NULL,
              UNIQUE(session_id,consumer)
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Additive table: checkpoints (prepare/export index).
    /// Early schema-v1 databases stamped schema_version=1 before this table existed.
    /// Safe to call repeatedly. Local P0 reattach does not require rows.
    /// </summary>
    public static async Task EnsureCheckpointsTableAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS checkpoints (
              checkpoint_id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL REFERENCES sessions(session_id),
              state TEXT NOT NULL,
              barrier_seq INTEGER NOT NULL,
              next_seq_at_prepare INTEGER NOT NULL,
              session_fingerprint TEXT NOT NULL,
              reason TEXT,
              include_scrollback INTEGER NOT NULL DEFAULT 0,
              include_workspace_files INTEGER NOT NULL DEFAULT 1,
              created_at TEXT NOT NULL,
              exported_at TEXT,
              manifest_relative_path TEXT,
              manifest_sha256 TEXT,
              byte_count INTEGER,
              transfer_incomplete INTEGER NOT NULL DEFAULT 0,
              warnings_json TEXT,
              placement TEXT NOT NULL DEFAULT 'local',
              placement_generation INTEGER NOT NULL DEFAULT 0,
              replay_complete INTEGER NOT NULL DEFAULT 1,
              project_root_device INTEGER,
              project_root_inode INTEGER,
              project_root_was_symlink INTEGER
            );
            CREATE INDEX IF NOT EXISTS ix_checkpoints_session ON checkpoints(session_id);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await EnsureCheckpointsPinColumnsAsync(conn, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Additive pin columns. Early checkpoint tables omitted device/inode.
    /// Missing pin is a security property, not optional metadata.
    /// </summary>
    internal static async Task EnsureCheckpointsPinColumnsAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        await EnsureTableColumnAsync(conn, "checkpoints", "project_root_device", "INTEGER", ct)
            .ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "checkpoints", "project_root_inode", "INTEGER", ct)
            .ConfigureAwait(false);
        await EnsureTableColumnAsync(conn, "checkpoints", "project_root_was_symlink", "INTEGER", ct)
            .ConfigureAwait(false);
    }

    private static async Task EnsureSessionsColumnAsync(
        SqliteConnection conn, string columnName, string columnTypeSql, CancellationToken ct) =>
        await EnsureTableColumnAsync(conn, "sessions", columnName, columnTypeSql, ct)
            .ConfigureAwait(false);

    private static async Task EnsureTableColumnAsync(
        SqliteConnection conn,
        string tableName,
        string columnName,
        string columnTypeSql,
        CancellationToken ct)
    {
        var tableExists = false;
        await using (var tableCheck = conn.CreateCommand())
        {
            tableCheck.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
            tableCheck.Parameters.AddWithValue("@name", tableName);
            tableExists = (long)(await tableCheck.ExecuteScalarAsync(ct).ConfigureAwait(false))! > 0;
        }

        if (!tableExists)
            return;

        var hasColumn = false;
        await using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(" + tableName + ")";
            await using var reader = await info.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // PRAGMA table_info: cid, name, type, notnull, dflt_value, pk
                if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
            return;

        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnTypeSql}";
        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReadMetaAsync(
        SqliteConnection conn, SqliteTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM runtime_meta WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        try
        {
            return (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static async Task UpsertMetaAsync(
        SqliteConnection conn, SqliteTransaction tx, string key, string value, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO runtime_meta(key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Normative Appendix A DDL (tables + indexes). Meta seed is separate.</summary>
    internal const string V1Ddl = """
        CREATE TABLE IF NOT EXISTS runtime_meta (
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sessions (
          session_id TEXT PRIMARY KEY,
          name TEXT NOT NULL UNIQUE,
          lifecycle_state TEXT NOT NULL,
          placement TEXT NOT NULL,
          placement_generation INTEGER NOT NULL DEFAULT 0,
          started_at TEXT NOT NULL,
          updated_at TEXT NOT NULL,
          replay_complete INTEGER NOT NULL DEFAULT 1,
          replay_error TEXT,
          focused_workspace_id TEXT
        );

        CREATE TABLE IF NOT EXISTS workspaces (
          workspace_id TEXT PRIMARY KEY,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          label TEXT,
          cwd TEXT NOT NULL,
          focused_tab_id TEXT,
          binding_json TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_workspaces_session ON workspaces(session_id);

        CREATE TABLE IF NOT EXISTS tabs (
          tab_id TEXT PRIMARY KEY,
          workspace_id TEXT NOT NULL REFERENCES workspaces(workspace_id),
          label TEXT,
          ordinal INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS panes (
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
        CREATE INDEX IF NOT EXISTS ix_panes_workspace ON panes(workspace_id);

        CREATE TABLE IF NOT EXISTS occupants (
          pane_id TEXT NOT NULL REFERENCES panes(pane_id),
          generation INTEGER NOT NULL,
          command TEXT NOT NULL,
          cwd TEXT NOT NULL,
          env_json TEXT NOT NULL,
          harness_resume_ref TEXT,
          state TEXT NOT NULL,
          started_at TEXT NOT NULL,
          ended_at TEXT,
          PRIMARY KEY(pane_id,generation)
        );

        CREATE TABLE IF NOT EXISTS attachments (
          attachment_id TEXT PRIMARY KEY,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          actor_id TEXT NOT NULL,
          tenant_id TEXT,
          mode TEXT NOT NULL,
          connected_at TEXT NOT NULL,
          last_seen_at TEXT NOT NULL,
          closed_at TEXT
        );

        CREATE TABLE IF NOT EXISTS leases (
          lease_id TEXT PRIMARY KEY,
          attachment_id TEXT NOT NULL REFERENCES attachments(attachment_id),
          pane_id TEXT,
          scope TEXT NOT NULL,
          state TEXT NOT NULL,
          issued_at TEXT NOT NULL,
          expires_at TEXT NOT NULL,
          grace_until TEXT,
          takeover_reason TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_leases_active ON leases(scope,state,expires_at);

        CREATE TABLE IF NOT EXISTS event_cursor (
          session_id TEXT PRIMARY KEY REFERENCES sessions(session_id),
          next_seq INTEGER NOT NULL,
          floor_seq INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS journal_segments (
          segment_id TEXT PRIMARY KEY,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          relative_path TEXT NOT NULL UNIQUE,
          first_seq INTEGER NOT NULL,
          last_seq INTEGER,
          record_count INTEGER NOT NULL DEFAULT 0,
          byte_count INTEGER NOT NULL DEFAULT 0,
          checksum_sha256 TEXT,
          closed INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL,
          closed_at TEXT,
          CHECK (first_seq >= 0),
          CHECK (last_seq IS NULL OR last_seq >= first_seq),
          CHECK (byte_count >= 0)
        );
        CREATE INDEX IF NOT EXISTS ix_journal_segments_session_seq
          ON journal_segments(session_id, first_seq, last_seq);

        CREATE TABLE IF NOT EXISTS scrollback_segments (
          segment_id TEXT PRIMARY KEY,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          pane_id TEXT NOT NULL REFERENCES panes(pane_id),
          relative_path TEXT NOT NULL UNIQUE,
          first_seq INTEGER NOT NULL,
          last_seq INTEGER,
          byte_count INTEGER NOT NULL DEFAULT 0,
          checksum_sha256 TEXT,
          closed INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL,
          closed_at TEXT,
          CHECK (first_seq >= 0),
          CHECK (last_seq IS NULL OR last_seq >= first_seq),
          CHECK (byte_count >= 0)
        );
        CREATE INDEX IF NOT EXISTS ix_scrollback_segments_pane_seq
          ON scrollback_segments(pane_id, first_seq, last_seq);

        CREATE TABLE IF NOT EXISTS event_exports (
          export_id TEXT PRIMARY KEY,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          consumer TEXT NOT NULL,
          last_acked_seq INTEGER NOT NULL DEFAULT 0,
          updated_at TEXT NOT NULL,
          UNIQUE(session_id,consumer)
        );

        CREATE TABLE IF NOT EXISTS checkpoints (
          checkpoint_id TEXT PRIMARY KEY,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          state TEXT NOT NULL,
          barrier_seq INTEGER NOT NULL,
          next_seq_at_prepare INTEGER NOT NULL,
          session_fingerprint TEXT NOT NULL,
          reason TEXT,
          include_scrollback INTEGER NOT NULL DEFAULT 0,
          include_workspace_files INTEGER NOT NULL DEFAULT 1,
          created_at TEXT NOT NULL,
          exported_at TEXT,
          manifest_relative_path TEXT,
          manifest_sha256 TEXT,
          byte_count INTEGER,
          transfer_incomplete INTEGER NOT NULL DEFAULT 0,
          warnings_json TEXT,
          placement TEXT NOT NULL DEFAULT 'local',
          placement_generation INTEGER NOT NULL DEFAULT 0,
          replay_complete INTEGER NOT NULL DEFAULT 1,
          project_root_device INTEGER,
          project_root_inode INTEGER,
          project_root_was_symlink INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_checkpoints_session ON checkpoints(session_id);
        """;

    /// <summary>Normative table names for migrator tests.</summary>
    public static readonly string[] NormativeTables =
    [
        "runtime_meta",
        "sessions",
        "workspaces",
        "tabs",
        "panes",
        "occupants",
        "attachments",
        "leases",
        "event_cursor",
        "journal_segments",
        "scrollback_segments",
        "event_exports",
        "checkpoints",
    ];
}
