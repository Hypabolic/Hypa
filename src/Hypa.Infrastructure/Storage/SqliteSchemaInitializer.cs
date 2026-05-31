using Hypa.Runtime.Domain.Common;
using Microsoft.Data.Sqlite;

namespace Hypa.Infrastructure.Storage;

public sealed class SqliteSchemaInitializer(HypaDataOptions options)
{
    private volatile bool _initialized;
    private Result<Unit, Error> _cachedResult;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly string[] RequiredTables =
    [
        "sessions", "evidence_records", "artifact_refs", "command_metrics",
        "trust_records", "parse_metrics", "code_files", "code_symbols",
        "code_references", "code_dependency_edges", "code_diagnostics",
        "code_provider_health", "project_registrations", "markdown_sections"
    ];

    // All columns added via AddColumnIfMissingAsync — must mirror those calls exactly.
    private static readonly (string Table, string Column)[] RequiredColumns =
    [
        ("sessions",               "external_ref"),
        ("code_dependency_edges",  "target_name"),
        ("code_dependency_edges",  "resolution_status"),
        ("code_dependency_edges",  "start_line"),
        ("code_dependency_edges",  "start_column"),
        ("code_dependency_edges",  "end_line"),
        ("code_dependency_edges",  "end_column"),
        ("code_dependency_edges",  "start_byte"),
        ("code_dependency_edges",  "end_byte"),
        ("code_files",             "frontmatter_yaml"),
        ("code_files",             "plain_text"),
        ("code_files",             "fact_kind"),
        ("code_files",             "confidence"),
        ("code_symbols",           "heading_level"),
        ("code_symbols",           "heading_path"),
        ("code_symbols",           "document_type"),
        ("markdown_sections",      "fact_kind"),
        ("markdown_sections",      "confidence"),
    ];

    public async Task<Result<Unit, Error>> InitAsync(CancellationToken ct)
    {
        if (_initialized) return _cachedResult;

        await _lock.WaitAsync(ct);
        try
        {
            if (_initialized) return _cachedResult;

            var result = await RunInitCoreAsync(ct);
            _cachedResult = result;
            _initialized = true;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Result<Unit, Error>> RunInitCoreAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(options.DatabasePath) && await IsCompatibleAsync(ct))
            {
                var versionCheck = await CheckSchemaVersionAsync(ct);
                if (!versionCheck.IsOk) return versionCheck;
                return Result<Unit, Error>.Ok(Unit.Value);
            }

            Directory.CreateDirectory(options.DataDirectory);
            await RunMigrationsAsync(ct);
            return Result<Unit, Error>.Ok(Unit.Value);
        }
        catch (SqliteException ex)
        {
            return Result<Unit, Error>.Fail(new Error("schema.db_error", ex.Message));
        }
        catch (IOException ex)
        {
            return Result<Unit, Error>.Fail(new Error("schema.io_error", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<Unit, Error>.Fail(new Error("schema.access_denied", ex.Message));
        }
    }

    private async Task<bool> IsCompatibleAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(
            $"Data Source={options.DatabasePath};Mode=ReadOnly");
        await conn.OpenAsync(ct);

        foreach (var table in RequiredTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
            cmd.Parameters.AddWithValue("@name", table);
            if ((long)(await cmd.ExecuteScalarAsync(ct))! == 0) return false;
        }

        foreach (var (table, column) in RequiredColumns)
            if (!await ColumnExistsAsync(conn, table, column, ct)) return false;

        return true;
    }

    private const int CurrentSchemaVersion = 2;

    // Phase 1 of 2: read schema_version via a read-only connection so that future-version
    // detection works even when the database or filesystem is read-only (e.g. Codex sandbox).
    // Phase 2: best-effort stamp via a writable connection when the row is absent.
    private async Task<Result<Unit, Error>> CheckSchemaVersionAsync(CancellationToken ct)
    {
        string? existingVersion = null;
        var metadataTableExists = false;

        try
        {
            await using var ro = new SqliteConnection(
                $"Data Source={options.DatabasePath};Mode=ReadOnly");
            await ro.OpenAsync(ct);

            await using var tableCheck = ro.CreateCommand();
            tableCheck.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_metadata'";
            metadataTableExists = (long)(await tableCheck.ExecuteScalarAsync(ct))! > 0;

            if (metadataTableExists)
            {
                await using var read = ro.CreateCommand();
                read.CommandText = "SELECT value FROM schema_metadata WHERE key = 'schema_version'";
                existingVersion = (string?)await read.ExecuteScalarAsync(ct);
            }
        }
        catch (SqliteException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // Reject databases stamped by a newer binary before attempting any write.
        if (existingVersion is not null &&
            int.TryParse(existingVersion, out var detected) &&
            detected > CurrentSchemaVersion)
        {
            return Result<Unit, Error>.Fail(new Error(
                "schema.future_version",
                $"Database schema version {detected} is newer than this binary supports " +
                $"({CurrentSchemaVersion}). Run a newer version of Hypa or delete " +
                $"{options.DatabasePath} to reset."));
        }

        // Best-effort stamp: only when the version row is absent.
        // Silently skips read-only or locked databases.
        if (existingVersion is null)
        {
            try
            {
                await using var rw = new SqliteConnection($"Data Source={options.DatabasePath}");
                await rw.OpenAsync(ct);

                if (!metadataTableExists)
                {
                    await using var create = rw.CreateCommand();
                    create.CommandText = """
                        CREATE TABLE IF NOT EXISTS schema_metadata (
                            key   TEXT PRIMARY KEY,
                            value TEXT NOT NULL
                        );
                        """;
                    await create.ExecuteNonQueryAsync(ct);
                }

                await UpsertSchemaVersionAsync(rw, ct);
            }
            catch (SqliteException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Result<Unit, Error>.Ok(Unit.Value);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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
                query_version    TEXT NOT NULL,
                frontmatter_yaml TEXT,
                plain_text       TEXT,
                fact_kind        TEXT NOT NULL DEFAULT 'syntactic',
                confidence       REAL NOT NULL DEFAULT 0.0
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
            CREATE TABLE IF NOT EXISTS markdown_sections (
                id               TEXT PRIMARY KEY,
                file_path        TEXT NOT NULL REFERENCES code_files(path),
                heading_level    INTEGER NOT NULL,
                heading_text     TEXT NOT NULL,
                heading_path     TEXT NOT NULL,
                start_line       INTEGER NOT NULL,
                end_line         INTEGER NOT NULL,
                start_byte       INTEGER NOT NULL,
                end_byte         INTEGER NOT NULL,
                text             TEXT,
                plain_text       TEXT,
                provider_id      TEXT NOT NULL,
                provider_version TEXT NOT NULL,
                query_version    TEXT NOT NULL,
                fact_kind        TEXT NOT NULL DEFAULT 'syntactic',
                confidence       REAL NOT NULL DEFAULT 0.0
            );
            CREATE INDEX IF NOT EXISTS idx_markdown_sections_file_path ON markdown_sections(file_path);
            CREATE INDEX IF NOT EXISTS idx_markdown_sections_heading_path ON markdown_sections(heading_path);
            CREATE INDEX IF NOT EXISTS idx_markdown_sections_level ON markdown_sections(heading_level);
            CREATE TABLE IF NOT EXISTS schema_metadata (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
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
        await AddColumnIfMissingAsync(conn, "code_files", "frontmatter_yaml", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "code_files", "plain_text", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "code_files", "fact_kind", "TEXT NOT NULL DEFAULT 'syntactic'", ct);
        await AddColumnIfMissingAsync(conn, "code_files", "confidence", "REAL NOT NULL DEFAULT 0.0", ct);
        await AddColumnIfMissingAsync(conn, "code_symbols", "heading_level", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "code_symbols", "heading_path", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "code_symbols", "document_type", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "markdown_sections", "fact_kind", "TEXT NOT NULL DEFAULT 'syntactic'", ct);
        await AddColumnIfMissingAsync(conn, "markdown_sections", "confidence", "REAL NOT NULL DEFAULT 0.0", ct);
        if (await MarkdownSectionsHasHeadingPathUniqueAsync(conn, ct))
        {
            await RebuildMarkdownSectionsWithoutHeadingPathUniqueAsync(conn, ct);
            await CreateMarkdownSectionIndexesAsync(conn, ct);
        }
        await UpsertSchemaVersionAsync(conn, ct);
    }

    private static async Task<bool> MarkdownSectionsHasHeadingPathUniqueAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='markdown_sections'";
        var sql = (string?)await cmd.ExecuteScalarAsync(ct);
        return sql?.Contains("UNIQUE(file_path, heading_path)", StringComparison.OrdinalIgnoreCase) is true;
    }

    private static async Task RebuildMarkdownSectionsWithoutHeadingPathUniqueAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys=OFF;
            CREATE TABLE markdown_sections_new (
                id               TEXT PRIMARY KEY,
                file_path        TEXT NOT NULL REFERENCES code_files(path),
                heading_level    INTEGER NOT NULL,
                heading_text     TEXT NOT NULL,
                heading_path     TEXT NOT NULL,
                start_line       INTEGER NOT NULL,
                end_line         INTEGER NOT NULL,
                start_byte       INTEGER NOT NULL,
                end_byte         INTEGER NOT NULL,
                text             TEXT,
                plain_text       TEXT,
                provider_id      TEXT NOT NULL,
                provider_version TEXT NOT NULL,
                query_version    TEXT NOT NULL,
                fact_kind        TEXT NOT NULL DEFAULT 'syntactic',
                confidence       REAL NOT NULL DEFAULT 0.0
            );
            INSERT INTO markdown_sections_new
                (id, file_path, heading_level, heading_text, heading_path,
                 start_line, end_line, start_byte, end_byte,
                 text, plain_text, provider_id, provider_version, query_version, fact_kind, confidence)
            SELECT id, file_path, heading_level, heading_text, heading_path,
                   start_line, end_line, start_byte, end_byte,
                   text, plain_text, provider_id, provider_version, query_version, fact_kind, confidence
            FROM markdown_sections;
            DROP TABLE markdown_sections;
            ALTER TABLE markdown_sections_new RENAME TO markdown_sections;
            PRAGMA foreign_keys=ON;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateMarkdownSectionIndexesAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_markdown_sections_file_path ON markdown_sections(file_path);
            CREATE INDEX IF NOT EXISTS idx_markdown_sections_heading_path ON markdown_sections(heading_path);
            CREATE INDEX IF NOT EXISTS idx_markdown_sections_level ON markdown_sections(heading_level);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertSchemaVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_metadata (key, value) VALUES ('schema_version', '2')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn, string table, string column, string type, CancellationToken ct)
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
