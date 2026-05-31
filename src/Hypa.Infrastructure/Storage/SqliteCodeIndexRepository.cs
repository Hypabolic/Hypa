using Hypa.Runtime.Application.Ports;
using Hypa.Sdk.CodeIntelligence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace Hypa.Infrastructure.Storage;

public sealed class SqliteCodeIndexRepository(
    HypaDataOptions options,
    SqliteSchemaInitializer schema,
    ILogger<SqliteCodeIndexRepository>? logger = null) : ICodeIndexRepository
{
    private readonly ILogger<SqliteCodeIndexRepository> _logger = logger ?? NullLogger<SqliteCodeIndexRepository>.Instance;

    public async Task SaveDocumentsAsync(IReadOnlyList<CodeStructureDocument> documents, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk)
            {
                _logger.LogDebug("Failed to initialize code index storage: {Error}", init.Error.Message);
                return;
            }

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            foreach (var document in documents)
            {
                await DeleteFileFactsAsync(conn, document.File.RelativePath, ct);
                await ExecuteAsync(conn, """
                    INSERT OR REPLACE INTO code_files
                        (path, project_root, absolute_path, language, content_hash, size_bytes, indexed_at,
                         provider_id, provider_version, query_version, frontmatter_yaml, plain_text, fact_kind, confidence,
                         git_blob_oid, mtime_ms)
                    VALUES
                        (@path, @projectRoot, @absolutePath, @language, @contentHash, @sizeBytes, @indexedAt,
                         @providerId, @providerVersion, @queryVersion, @frontmatterYaml, @plainText, @factKind, @confidence,
                         @gitBlobOid, @mtimeMs)
                    """, ct,
                    ("@path", document.File.RelativePath),
                    ("@projectRoot", document.File.ProjectRoot),
                    ("@absolutePath", document.File.Path),
                    ("@language", document.File.Language),
                    ("@contentHash", document.File.ContentHash),
                    ("@sizeBytes", document.File.SizeBytes),
                    ("@indexedAt", document.File.IndexedAt.ToString("O")),
                    ("@providerId", document.Provenance.ProviderId),
                    ("@providerVersion", document.Provenance.ProviderVersion),
                    ("@queryVersion", document.Provenance.QueryVersion),
                    ("@frontmatterYaml", document.FrontmatterYaml),
                    ("@plainText", document.PlainText),
                    ("@factKind", document.Provenance.FactKind),
                    ("@confidence", document.Provenance.Confidence),
                    ("@gitBlobOid", document.File.GitBlobOid),
                    ("@mtimeMs", document.File.MTimeMs));

                foreach (var symbol in document.Symbols)
                    await ExecuteAsync(conn, """
                        INSERT OR REPLACE INTO code_symbols
                            (id, file_path, language, name, kind, parent_id, start_line, start_column, end_line, end_column, start_byte, end_byte, provider_id, provider_version, query_version, fact_kind, confidence)
                        VALUES
                            (@id, @filePath, @language, @name, @kind, @parentId, @startLine, @startColumn, @endLine, @endColumn, @startByte, @endByte, @providerId, @providerVersion, @queryVersion, @factKind, @confidence)
                        """, ct, SymbolParams(symbol));

                foreach (var reference in document.References)
                    await ExecuteAsync(conn, """
                        INSERT OR REPLACE INTO code_references
                            (id, file_path, kind, target, start_line, start_column, end_line, end_column, start_byte, end_byte, provider_id, provider_version, query_version, fact_kind, confidence)
                        VALUES
                            (@id, @filePath, @kind, @target, @startLine, @startColumn, @endLine, @endColumn, @startByte, @endByte, @providerId, @providerVersion, @queryVersion, @factKind, @confidence)
                        """, ct, ReferenceParams(reference));

                foreach (var edge in document.DependencyEdges)
                    await ExecuteAsync(conn, """
                        INSERT OR REPLACE INTO code_dependency_edges
                            (id, source_id, target_id, kind, target_name, resolution_status, start_line, start_column, end_line, end_column, start_byte, end_byte,
                             provider_id, provider_version, query_version, fact_kind, confidence)
                        VALUES
                            (@id, @sourceId, @targetId, @kind, @targetName, @resolutionStatus, @startLine, @startColumn, @endLine, @endColumn, @startByte, @endByte,
                             @providerId, @providerVersion, @queryVersion, @factKind, @confidence)
                        """, ct, EdgeParams(edge));

                foreach (var diagnostic in document.Diagnostics)
                    await ExecuteAsync(conn, """
                        INSERT OR REPLACE INTO code_diagnostics
                            (id, file_path, severity, code, message, start_line, start_column, end_line, end_column, start_byte, end_byte, provider_id, provider_version, query_version, fact_kind, confidence)
                        VALUES
                            (@id, @filePath, @severity, @code, @message, @startLine, @startColumn, @endLine, @endColumn, @startByte, @endByte, @providerId, @providerVersion, @queryVersion, @factKind, @confidence)
                        """, ct, DiagnosticParams(diagnostic));

                foreach (var section in document.Sections)
                    await ExecuteAsync(conn, """
                        INSERT OR REPLACE INTO markdown_sections
                            (id, file_path, heading_level, heading_text, heading_path,
                             start_line, end_line, start_byte, end_byte,
                             text, plain_text, provider_id, provider_version, query_version, fact_kind, confidence)
                        VALUES
                            (@id, @filePath, @headingLevel, @headingText, @headingPath,
                             @startLine, @endLine, @startByte, @endByte,
                             @text, @plainText, @providerId, @providerVersion, @queryVersion, @factKind, @confidence)
                        """, ct, SectionParams(section));
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to save code index documents");
        }
    }

    public async Task<IReadOnlyList<CodeSymbol>> QuerySymbolsAsync(CodeSymbolQuery query, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return [];

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, file_path, language, name, kind, parent_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                       provider_id, provider_version, query_version, fact_kind, confidence
                FROM code_symbols
                WHERE (@query IS NULL OR name LIKE '%' || @query || '%')
                  AND (@path IS NULL OR file_path LIKE @path || '%')
                  AND (@kind IS NULL OR kind = @kind)
                ORDER BY file_path, start_byte
                LIMIT 500
                """;
            cmd.Parameters.AddWithValue("@query", (object?)query.Query ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@path", (object?)query.Path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@kind", (object?)query.Kind ?? DBNull.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var symbols = new List<CodeSymbol>();
            while (await reader.ReadAsync(ct))
                symbols.Add(ReadSymbol(reader));
            return symbols;
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to query code symbols");
            return [];
        }
    }

    public async Task<CodeGraphResult> QueryGraphAsync(CodeGraphQuery query, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return new CodeGraphResult();

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
            SELECT id, source_id, target_id, kind, target_name, resolution_status, start_line, start_column, end_line, end_column, start_byte, end_byte,
                   provider_id, provider_version, query_version, fact_kind, confidence
            FROM code_dependency_edges
            WHERE (@symbol IS NULL OR source_id = @symbol OR target_id = @symbol)
              AND (@path IS NULL OR source_id LIKE @path || '%' OR target_id LIKE @path || '%')
              AND (@edgeKind IS NULL OR kind = @edgeKind)
              AND (@from IS NULL OR source_id = @from)
              AND (@to IS NULL OR target_id = @to OR target_name = @to)
              AND (@callers IS NULL OR (kind = 'calls' AND (target_id = @callers OR target_name = @callers)))
              AND (@callees IS NULL OR (kind = 'calls' AND source_id = @callees))
            ORDER BY CASE kind
                WHEN 'imports' THEN 0
                WHEN 'inherits' THEN 1
                WHEN 'implements' THEN 2
                WHEN 'overrides' THEN 3
                WHEN 'contains' THEN 4
                WHEN 'calls' THEN 5
                ELSE 6
              END,
            kind, source_id, target_id, start_byte
            LIMIT 1000
            """;
            cmd.Parameters.AddWithValue("@symbol", (object?)query.SymbolId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@path", (object?)query.Path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@edgeKind", (object?)query.EdgeKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@from", (object?)query.From ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@to", (object?)query.To ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@callers", (object?)query.Callers ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@callees", (object?)query.Callees ?? DBNull.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var edges = new List<CodeDependencyEdge>();
            while (await reader.ReadAsync(ct))
                edges.Add(ReadEdge(reader));

            var references = new List<CodeReference>();
            if (!string.IsNullOrWhiteSpace(query.References))
                references.AddRange(await QueryReferencesAsync(conn, query.References, query.Path, ct));

            var symbolIds = edges.Select(e => e.SourceId).Concat(edges.Select(e => e.TargetId)).Where(id => id.StartsWith("sym_", StringComparison.Ordinal)).Distinct().ToArray();
            var symbols = new List<CodeSymbol>();
            foreach (var id in symbolIds)
            {
                await using var symbolCmd = conn.CreateCommand();
                symbolCmd.CommandText = """
                    SELECT id, file_path, language, name, kind, parent_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                           provider_id, provider_version, query_version, fact_kind, confidence
                    FROM code_symbols WHERE id = @id
                    """;
                symbolCmd.Parameters.AddWithValue("@id", id);
                await using var symbolReader = await symbolCmd.ExecuteReaderAsync(ct);
                if (await symbolReader.ReadAsync(ct))
                    symbols.Add(ReadSymbol(symbolReader));
            }

            return new CodeGraphResult { Symbols = symbols, Edges = edges, References = references };
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to query code graph");
            return new CodeGraphResult();
        }
    }

    public async Task<IReadOnlyList<CodeDiagnostic>> QueryDiagnosticsAsync(CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return [];

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, file_path, severity, code, message, start_line, start_column, end_line, end_column, start_byte, end_byte,
                       provider_id, provider_version, query_version, fact_kind, confidence
                FROM code_diagnostics
                ORDER BY file_path, start_byte
                LIMIT 500
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var diagnostics = new List<CodeDiagnostic>();
            while (await reader.ReadAsync(ct))
                diagnostics.Add(ReadDiagnostic(reader));
            return diagnostics;
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to query code diagnostics");
            return [];
        }
    }

    public async Task<CodeStructureDocument?> QueryMarkdownAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return null;

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);

            var document = await QueryMarkdownDocumentAsync(conn, filePath, ct);
            if (document is null) return null;

            return document with
            {
                Symbols = await QuerySymbolsForFileAsync(conn, filePath, ct),
                References = await QueryReferencesForFileAsync(conn, filePath, ct),
                Diagnostics = await QueryDiagnosticsForFileAsync(conn, filePath, ct),
                Sections = await QueryMarkdownSectionsAsync(conn, filePath, ct),
            };
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to query markdown document");
            return null;
        }
    }

    public async Task<IReadOnlyList<MarkdownSection>> QueryMarkdownSectionsAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return [];

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            return await QueryMarkdownSectionsAsync(conn, filePath, ct);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to query markdown sections");
            return [];
        }
    }

    public async Task<IReadOnlyList<CodeReference>> QueryReferencesAsync(string filePath, string kind, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return [];

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, file_path, kind, target, start_line, start_column, end_line, end_column,
                       start_byte, end_byte, provider_id, provider_version, query_version, fact_kind, confidence
                FROM code_references
                WHERE file_path = @filePath AND kind = @kind
                ORDER BY start_byte
                """;
            cmd.Parameters.AddWithValue("@filePath", filePath);
            cmd.Parameters.AddWithValue("@kind", kind);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var references = new List<CodeReference>();
            while (await reader.ReadAsync(ct))
                references.Add(ReadReference(reader));
            return references;
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to query code references");
            return [];
        }
    }

    public async Task SaveProviderHealthAsync(IReadOnlyList<CodeProviderHealth> health, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk)
            {
                _logger.LogDebug("Failed to initialize code provider health storage: {Error}", init.Error.Message);
                return;
            }

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            foreach (var item in health)
                await ExecuteAsync(conn, """
                    INSERT OR REPLACE INTO code_provider_health (provider_id, status, message, checked_at)
                    VALUES (@providerId, @status, @message, @checkedAt)
                    """, ct,
                    ("@providerId", item.ProviderId),
                    ("@status", item.Status),
                    ("@message", item.Message),
                    ("@checkedAt", item.CheckedAt.ToString("O")));
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to save code provider health");
        }
    }

    public async Task<IReadOnlyList<CodeProviderHealth>> GetProviderHealthAsync(CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return [];

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT provider_id, status, message, checked_at FROM code_provider_health ORDER BY provider_id";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var health = new List<CodeProviderHealth>();
            while (await reader.ReadAsync(ct))
                health.Add(new CodeProviderHealth
                {
                    ProviderId = reader.GetString(0),
                    Status = reader.GetString(1),
                    Message = reader.GetString(2),
                    CheckedAt = DateTimeOffset.Parse(reader.GetString(3)),
                });
            return health;
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to query code provider health");
            return [];
        }
    }

    public async Task<IReadOnlyDictionary<string, FileIndexState>> QueryFileStatesAsync(
        string projectRoot, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return new Dictionary<string, FileIndexState>();

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT absolute_path, git_blob_oid, mtime_ms, size_bytes
                FROM code_files
                WHERE project_root = @projectRoot
                """;
            cmd.Parameters.AddWithValue("@projectRoot", projectRoot);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var result = new Dictionary<string, FileIndexState>();
            while (await reader.ReadAsync(ct))
            {
                var state = new FileIndexState
                {
                    AbsolutePath = reader.GetString(0),
                    GitBlobOid = reader.IsDBNull(1) ? null : reader.GetString(1),
                    MTimeMs = reader.GetInt64(2),
                    SizeBytes = reader.GetInt64(3),
                };
                result[state.AbsolutePath] = state;
            }
            return result;
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to query file states");
            return new Dictionary<string, FileIndexState>();
        }
    }

    public async Task<FileIndexState?> QueryFileStateAsync(string absolutePath, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return null;

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT absolute_path, git_blob_oid, mtime_ms, size_bytes
                FROM code_files
                WHERE absolute_path = @absolutePath
                """;
            cmd.Parameters.AddWithValue("@absolutePath", absolutePath);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new FileIndexState
            {
                AbsolutePath = reader.GetString(0),
                GitBlobOid = reader.IsDBNull(1) ? null : reader.GetString(1),
                MTimeMs = reader.GetInt64(2),
                SizeBytes = reader.GetInt64(3),
            };
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to query file state");
            return null;
        }
    }

    public async Task DeleteFileAsync(string absolutePath, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return;

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);

            string? relativePath;
            await using (var lookup = conn.CreateCommand())
            {
                lookup.CommandText = "SELECT path FROM code_files WHERE absolute_path = @absolutePath";
                lookup.Parameters.AddWithValue("@absolutePath", absolutePath);
                relativePath = (string?)await lookup.ExecuteScalarAsync(ct);
            }

            if (relativePath is null) return;

            await using var tx = await conn.BeginTransactionAsync(ct);
            await DeleteFileFactsAsync(conn, relativePath, ct);
            await ExecuteAsync(conn, "DELETE FROM code_files WHERE path = @path", ct, ("@path", relativePath));
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            _logger.LogDebug(ex, "Failed to delete file from code index");
        }
    }

    private async Task DeleteFileFactsAsync(SqliteConnection conn, string filePath, CancellationToken ct)
    {
        var symbolIds = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM code_symbols WHERE file_path = @filePath";
            cmd.Parameters.AddWithValue("@filePath", filePath);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                symbolIds.Add(reader.GetString(0));
        }

        foreach (var id in symbolIds)
            await ExecuteAsync(conn, "DELETE FROM code_dependency_edges WHERE source_id = @id OR target_id = @id", ct, ("@id", id));

        foreach (var table in new[] { "code_symbols", "code_references", "code_diagnostics" })
            await ExecuteAsync(conn, $"DELETE FROM {table} WHERE file_path = @filePath", ct, ("@filePath", filePath));
        await ExecuteAsync(conn, "DELETE FROM markdown_sections WHERE file_path = @filePath", ct, ("@filePath", filePath));
        await ExecuteAsync(conn, "DELETE FROM code_dependency_edges WHERE source_id = @filePath OR target_id = @filePath", ct, ("@filePath", filePath));
    }

    private SqliteConnection OpenConnection() => new($"Data Source={options.DatabasePath}");

    private static async Task<CodeStructureDocument?> QueryMarkdownDocumentAsync(SqliteConnection conn, string filePath, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT path, project_root, absolute_path, language, content_hash, size_bytes, indexed_at,
                   provider_id, provider_version, query_version, fact_kind, confidence, frontmatter_yaml, plain_text
            FROM code_files
            WHERE path = @filePath AND language = 'markdown'
            """;
        cmd.Parameters.AddWithValue("@filePath", filePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadMarkdownDocument(reader) : null;
    }

    private static async Task<IReadOnlyList<CodeSymbol>> QuerySymbolsForFileAsync(SqliteConnection conn, string filePath, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_path, language, name, kind, parent_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                   provider_id, provider_version, query_version, fact_kind, confidence
            FROM code_symbols
            WHERE file_path = @filePath
            ORDER BY start_byte
            """;
        cmd.Parameters.AddWithValue("@filePath", filePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var symbols = new List<CodeSymbol>();
        while (await reader.ReadAsync(ct))
            symbols.Add(ReadSymbol(reader));
        return symbols;
    }

    private static async Task<IReadOnlyList<CodeReference>> QueryReferencesForFileAsync(SqliteConnection conn, string filePath, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_path, kind, target, start_line, start_column, end_line, end_column,
                   start_byte, end_byte, provider_id, provider_version, query_version, fact_kind, confidence
            FROM code_references
            WHERE file_path = @filePath
            ORDER BY start_byte
            """;
        cmd.Parameters.AddWithValue("@filePath", filePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var references = new List<CodeReference>();
        while (await reader.ReadAsync(ct))
            references.Add(ReadReference(reader));
        return references;
    }

    private static async Task<IReadOnlyList<CodeDiagnostic>> QueryDiagnosticsForFileAsync(SqliteConnection conn, string filePath, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_path, severity, code, message, start_line, start_column, end_line, end_column, start_byte, end_byte,
                   provider_id, provider_version, query_version, fact_kind, confidence
            FROM code_diagnostics
            WHERE file_path = @filePath
            ORDER BY start_byte
            """;
        cmd.Parameters.AddWithValue("@filePath", filePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var diagnostics = new List<CodeDiagnostic>();
        while (await reader.ReadAsync(ct))
            diagnostics.Add(ReadDiagnostic(reader));
        return diagnostics;
    }

    private static async Task<IReadOnlyList<MarkdownSection>> QueryMarkdownSectionsAsync(SqliteConnection conn, string filePath, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_path, heading_level, heading_text, heading_path,
                   start_line, end_line, start_byte, end_byte,
                   text, plain_text, provider_id, provider_version, query_version, fact_kind, confidence
            FROM markdown_sections
            WHERE file_path = @filePath
            ORDER BY start_byte
            """;
        cmd.Parameters.AddWithValue("@filePath", filePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var sections = new List<MarkdownSection>();
        while (await reader.ReadAsync(ct))
            sections.Add(ReadSection(reader));
        return sections;
    }

    private static async Task<IReadOnlyList<CodeReference>> QueryReferencesAsync(SqliteConnection conn, string target, string? path, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_path, kind, target, start_line, start_column, end_line, end_column, start_byte, end_byte,
                   provider_id, provider_version, query_version, fact_kind, confidence
            FROM code_references
            WHERE target = @target
              AND (@path IS NULL OR file_path LIKE @path || '%')
            ORDER BY file_path, start_byte
            LIMIT 1000
            """;
        cmd.Parameters.AddWithValue("@target", target);
        cmd.Parameters.AddWithValue("@path", (object?)path ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var references = new List<CodeReference>();
        while (await reader.ReadAsync(ct))
            references.Add(ReadReference(reader));
        return references;
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static (string, object?)[] SymbolParams(CodeSymbol s) =>
    [
        ("@id", s.Id), ("@filePath", s.FilePath), ("@language", s.Language), ("@name", s.Name), ("@kind", s.Kind), ("@parentId", s.ParentId),
        ("@startLine", s.Span.StartLine), ("@startColumn", s.Span.StartColumn), ("@endLine", s.Span.EndLine), ("@endColumn", s.Span.EndColumn),
        ("@startByte", s.Span.StartByte), ("@endByte", s.Span.EndByte),
        .. ProvenanceParams(s.Provenance),
    ];

    private static (string, object?)[] ReferenceParams(CodeReference r) =>
    [
        ("@id", r.Id), ("@filePath", r.FilePath), ("@kind", r.Kind), ("@target", r.Target),
        ("@startLine", r.Span.StartLine), ("@startColumn", r.Span.StartColumn), ("@endLine", r.Span.EndLine), ("@endColumn", r.Span.EndColumn),
        ("@startByte", r.Span.StartByte), ("@endByte", r.Span.EndByte),
        .. ProvenanceParams(r.Provenance),
    ];

    private static (string, object?)[] EdgeParams(CodeDependencyEdge e) =>
    [
        ("@id", e.Id), ("@sourceId", e.SourceId), ("@targetId", e.TargetId), ("@kind", e.Kind),
        ("@targetName", e.TargetName), ("@resolutionStatus", e.TargetResolutionStatus),
        ("@startLine", e.SourceSpan?.StartLine), ("@startColumn", e.SourceSpan?.StartColumn), ("@endLine", e.SourceSpan?.EndLine), ("@endColumn", e.SourceSpan?.EndColumn),
        ("@startByte", e.SourceSpan?.StartByte), ("@endByte", e.SourceSpan?.EndByte),
        .. ProvenanceParams(e.Provenance),
    ];

    private static (string, object?)[] DiagnosticParams(CodeDiagnostic d) =>
    [
        ("@id", d.Id), ("@filePath", d.FilePath), ("@severity", d.Severity), ("@code", d.Code), ("@message", d.Message),
        ("@startLine", d.Span?.StartLine), ("@startColumn", d.Span?.StartColumn), ("@endLine", d.Span?.EndLine), ("@endColumn", d.Span?.EndColumn),
        ("@startByte", d.Span?.StartByte), ("@endByte", d.Span?.EndByte),
        .. ProvenanceParams(d.Provenance),
    ];

    private static (string, object?)[] SectionParams(MarkdownSection s) =>
    [
        ("@id", s.Id), ("@filePath", s.FilePath), ("@headingLevel", s.HeadingLevel), ("@headingText", s.HeadingText), ("@headingPath", s.HeadingPath),
        ("@startLine", s.StartLine), ("@endLine", s.EndLine), ("@startByte", s.StartByte), ("@endByte", s.EndByte),
        ("@text", s.Text), ("@plainText", s.PlainText),
        ("@providerId", s.Provenance.ProviderId), ("@providerVersion", s.Provenance.ProviderVersion), ("@queryVersion", s.Provenance.QueryVersion),
        ("@factKind", s.Provenance.FactKind), ("@confidence", s.Provenance.Confidence),
    ];

    private static (string, object?)[] ProvenanceParams(ProviderProvenance p) =>
    [
        ("@providerId", p.ProviderId), ("@providerVersion", p.ProviderVersion), ("@queryVersion", p.QueryVersion), ("@factKind", p.FactKind), ("@confidence", p.Confidence),
    ];

    private static CodeStructureDocument ReadMarkdownDocument(SqliteDataReader r) => new()
    {
        File = new CodeFileIdentity
        {
            RelativePath = r.GetString(0),
            ProjectRoot = r.GetString(1),
            Path = r.GetString(2),
            Language = r.GetString(3),
            ContentHash = r.GetString(4),
            SizeBytes = r.GetInt64(5),
            IndexedAt = DateTimeOffset.Parse(r.GetString(6)),
        },
        Provenance = ReadProvenance(r, 7),
        FrontmatterYaml = r.IsDBNull(12) ? null : r.GetString(12),
        PlainText = r.IsDBNull(13) ? null : r.GetString(13),
    };

    private static CodeSymbol ReadSymbol(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        FilePath = r.GetString(1),
        Language = r.GetString(2),
        Name = r.GetString(3),
        Kind = r.GetString(4),
        ParentId = r.IsDBNull(5) ? null : r.GetString(5),
        Span = ReadSpan(r, 6),
        Provenance = ReadProvenance(r, 12),
    };

    private static CodeDependencyEdge ReadEdge(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        SourceId = r.GetString(1),
        TargetId = r.GetString(2),
        Kind = r.GetString(3),
        TargetName = r.IsDBNull(4) ? null : r.GetString(4),
        TargetResolutionStatus = r.GetString(5),
        SourceSpan = r.IsDBNull(6) ? null : ReadSpan(r, 6),
        Provenance = ReadProvenance(r, 12),
    };

    private static CodeReference ReadReference(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        FilePath = r.GetString(1),
        Kind = r.GetString(2),
        Target = r.GetString(3),
        Span = ReadSpan(r, 4),
        Provenance = ReadProvenance(r, 10),
    };

    private static CodeDiagnostic ReadDiagnostic(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        FilePath = r.GetString(1),
        Severity = r.GetString(2),
        Code = r.GetString(3),
        Message = r.GetString(4),
        Span = r.IsDBNull(5) ? null : ReadSpan(r, 5),
        Provenance = ReadProvenance(r, 11),
    };

    private static MarkdownSection ReadSection(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        FilePath = r.GetString(1),
        HeadingLevel = r.GetInt32(2),
        HeadingText = r.GetString(3),
        HeadingPath = r.GetString(4),
        HeadingAnchor = ToAnchor(r.GetString(3)),
        StartLine = r.GetInt32(5),
        EndLine = r.GetInt32(6),
        StartByte = r.GetInt32(7),
        EndByte = r.GetInt32(8),
        Text = r.IsDBNull(9) ? null : r.GetString(9),
        PlainText = r.IsDBNull(10) ? null : r.GetString(10),
        Provenance = ReadProvenance(r, 11),
    };

    private static string ToAnchor(string text)
    {
        var lower = text.ToLowerInvariant().Trim();
        var builder = new StringBuilder(lower.Length);
        var previousWhitespace = false;
        foreach (var c in lower)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWhitespace)
                    builder.Append('-');
                previousWhitespace = true;
            }
            else
            {
                previousWhitespace = false;
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')
                    builder.Append(c);
            }
        }

        return builder.ToString().Trim('-');
    }

    private static SourceSpan ReadSpan(SqliteDataReader r, int start) => new()
    {
        StartLine = r.GetInt32(start),
        StartColumn = r.GetInt32(start + 1),
        EndLine = r.GetInt32(start + 2),
        EndColumn = r.GetInt32(start + 3),
        StartByte = r.GetInt32(start + 4),
        EndByte = r.GetInt32(start + 5),
    };

    private static ProviderProvenance ReadProvenance(SqliteDataReader r, int start) => new()
    {
        ProviderId = r.GetString(start),
        ProviderVersion = r.GetString(start + 1),
        QueryVersion = r.GetString(start + 2),
        FactKind = r.GetString(start + 3),
        Confidence = r.GetDouble(start + 4),
    };
}
