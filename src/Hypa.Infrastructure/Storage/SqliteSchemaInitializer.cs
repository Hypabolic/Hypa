using Microsoft.Data.Sqlite;

namespace Hypa.Infrastructure.Storage;

public sealed class SqliteSchemaInitializer(HypaDataOptions options)
{
    private volatile bool _initialized;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task InitAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(options.DataDirectory);
            await RunMigrationsAsync(ct);
            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RunMigrationsAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id                      TEXT PRIMARY KEY,
                project_root            TEXT NOT NULL,
                atomic_agent_session_id TEXT,
                external_ref            TEXT,
                created_at              TEXT NOT NULL,
                updated_at              TEXT NOT NULL,
                checkpointed_at         TEXT,
                stats_json              TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS evidence_records (
                id           TEXT PRIMARY KEY,
                session_id   TEXT NOT NULL REFERENCES sessions(id),
                recorded_at  TEXT NOT NULL,
                kind         TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS artifact_refs (
                id           TEXT PRIMARY KEY,
                session_id   TEXT NOT NULL REFERENCES sessions(id),
                created_at   TEXT NOT NULL,
                mime_type    TEXT NOT NULL,
                size_bytes   INTEGER NOT NULL,
                storage_path TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS command_metrics (
                id                TEXT PRIMARY KEY,
                session_id        TEXT NOT NULL REFERENCES sessions(id),
                recorded_at       TEXT NOT NULL,
                command           TEXT NOT NULL,
                exit_code         INTEGER NOT NULL,
                duration_ms       INTEGER NOT NULL,
                original_tokens   INTEGER NOT NULL,
                compressed_tokens INTEGER NOT NULL,
                reducer_id        TEXT NOT NULL,
                tee_artifact_id   TEXT
            );
            CREATE TABLE IF NOT EXISTS trust_records (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                project_root TEXT NOT NULL,
                filter_file  TEXT NOT NULL,
                file_hash    TEXT NOT NULL,
                granted_at   TEXT NOT NULL,
                UNIQUE(project_root, filter_file)
            );
            CREATE TABLE IF NOT EXISTS parse_metrics (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id       TEXT NOT NULL,
                executable   TEXT NOT NULL,
                arguments    TEXT NOT NULL,
                parse_tier   TEXT NOT NULL,
                filter_id    TEXT,
                recorded_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS code_files (
                path             TEXT PRIMARY KEY,
                project_root     TEXT NOT NULL,
                absolute_path    TEXT NOT NULL,
                language         TEXT NOT NULL,
                content_hash     TEXT NOT NULL,
                size_bytes       INTEGER NOT NULL,
                indexed_at       TEXT NOT NULL,
                provider_id      TEXT NOT NULL,
                provider_version TEXT NOT NULL,
                query_version    TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS code_symbols (
                id               TEXT PRIMARY KEY,
                file_path        TEXT NOT NULL,
                language         TEXT NOT NULL,
                name             TEXT NOT NULL,
                kind             TEXT NOT NULL,
                parent_id        TEXT,
                start_line       INTEGER NOT NULL,
                start_column     INTEGER NOT NULL,
                end_line         INTEGER NOT NULL,
                end_column       INTEGER NOT NULL,
                start_byte       INTEGER NOT NULL,
                end_byte         INTEGER NOT NULL,
                provider_id      TEXT NOT NULL,
                provider_version TEXT NOT NULL,
                query_version    TEXT NOT NULL,
                fact_kind        TEXT NOT NULL,
                confidence       REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_code_symbols_file_path ON code_symbols(file_path);
            CREATE INDEX IF NOT EXISTS ix_code_symbols_name ON code_symbols(name);
            CREATE INDEX IF NOT EXISTS ix_code_symbols_name_kind ON code_symbols(name, kind);
            CREATE TABLE IF NOT EXISTS code_references (
                id               TEXT PRIMARY KEY,
                file_path        TEXT NOT NULL,
                kind             TEXT NOT NULL,
                target           TEXT NOT NULL,
                start_line       INTEGER NOT NULL,
                start_column     INTEGER NOT NULL,
                end_line         INTEGER NOT NULL,
                end_column       INTEGER NOT NULL,
                start_byte       INTEGER NOT NULL,
                end_byte         INTEGER NOT NULL,
                provider_id      TEXT NOT NULL,
                provider_version TEXT NOT NULL,
                query_version    TEXT NOT NULL,
                fact_kind        TEXT NOT NULL,
                confidence       REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_code_references_kind_target ON code_references(kind, target);
            CREATE TABLE IF NOT EXISTS code_dependency_edges (
                id               TEXT PRIMARY KEY,
                source_id        TEXT NOT NULL,
                target_id        TEXT NOT NULL,
                kind             TEXT NOT NULL,
                target_name      TEXT,
                resolution_status TEXT NOT NULL DEFAULT 'unresolved',
                start_line       INTEGER,
                start_column     INTEGER,
                end_line         INTEGER,
                end_column       INTEGER,
                start_byte       INTEGER,
                end_byte         INTEGER,
                provider_id      TEXT NOT NULL,
                provider_version TEXT NOT NULL,
                query_version    TEXT NOT NULL,
                fact_kind        TEXT NOT NULL,
                confidence       REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_code_edges_source ON code_dependency_edges(source_id);
            CREATE INDEX IF NOT EXISTS ix_code_edges_target ON code_dependency_edges(target_id);
            CREATE INDEX IF NOT EXISTS ix_code_edges_kind ON code_dependency_edges(kind);
            CREATE INDEX IF NOT EXISTS ix_code_edges_source_kind ON code_dependency_edges(source_id, kind);
            CREATE INDEX IF NOT EXISTS ix_code_edges_target_kind ON code_dependency_edges(target_id, kind);
            CREATE TABLE IF NOT EXISTS code_diagnostics (
                id               TEXT PRIMARY KEY,
                file_path        TEXT NOT NULL,
                severity         TEXT NOT NULL,
                code             TEXT NOT NULL,
                message          TEXT NOT NULL,
                start_line       INTEGER,
                start_column     INTEGER,
                end_line         INTEGER,
                end_column       INTEGER,
                start_byte       INTEGER,
                end_byte         INTEGER,
                provider_id      TEXT NOT NULL,
                provider_version TEXT NOT NULL,
                query_version    TEXT NOT NULL,
                fact_kind        TEXT NOT NULL,
                confidence       REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS code_provider_health (
                provider_id TEXT PRIMARY KEY,
                status      TEXT NOT NULL,
                message     TEXT NOT NULL,
                checked_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS project_registrations (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                root_path    TEXT NOT NULL,
                agent_key    TEXT NOT NULL,
                installed_at TEXT NOT NULL,
                UNIQUE(root_path, agent_key)
            );
            CREATE INDEX IF NOT EXISTS ix_project_registrations_agent ON project_registrations(agent_key);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        await AddColumnIfMissingAsync(conn, "sessions", "external_ref", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "code_dependency_edges", "target_name", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "code_dependency_edges", "resolution_status", "TEXT NOT NULL DEFAULT 'unresolved'", ct);
        await AddColumnIfMissingAsync(conn, "code_dependency_edges", "start_line", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "code_dependency_edges", "start_column", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "code_dependency_edges", "end_line", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "code_dependency_edges", "end_column", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "code_dependency_edges", "start_byte", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "code_dependency_edges", "end_byte", "INTEGER", ct);
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection conn, string table, string column, string type, CancellationToken ct)
    {
        try
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists — nothing to do.
        }
    }
}
