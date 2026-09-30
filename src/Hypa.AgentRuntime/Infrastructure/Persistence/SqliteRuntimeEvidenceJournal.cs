using System.Globalization;
using Hypa.AgentRuntime.Application;
using Microsoft.Data.Sqlite;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// SQLite adapter for <c>event_exports</c> (Appendix A). Create-on-ack upsert.
/// One cursor row per (session_id, consumer); <c>export_id</c> is updated on upsert.
/// <c>last_acked_seq</c> is a non-decreasing high-water mark.
/// </summary>
public sealed class SqliteRuntimeEvidenceJournal : IRuntimeEvidenceJournal
{
    private readonly RuntimeStatePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteRuntimeEvidenceJournal(RuntimeStatePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<RuntimeResult<ExportAckRecord>> AckAsync(
        string sessionId,
        string exportId,
        long lastSeq,
        string consumer,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
        var c = string.IsNullOrWhiteSpace(consumer) ? "atomic" : consumer.Trim();
        var now = DateTimeOffset.UtcNow;
        var nowText = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _paths.EnsureDirectory();
                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);

                // One row per (session_id, consumer). On conflict update export_id and
                // take MAX(last_acked_seq) so retries cannot lower the cursor.
                // Also handle export_id PK reuse for the same session/consumer.
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO event_exports(export_id, session_id, consumer, last_acked_seq, updated_at)
                    VALUES (@export_id, @session_id, @consumer, @last_acked_seq, @updated_at)
                    ON CONFLICT(session_id, consumer) DO UPDATE SET
                      export_id = excluded.export_id,
                      last_acked_seq = MAX(event_exports.last_acked_seq, excluded.last_acked_seq),
                      updated_at = excluded.updated_at;
                    """;
                cmd.Parameters.AddWithValue("@export_id", exportId);
                cmd.Parameters.AddWithValue("@session_id", sessionId);
                cmd.Parameters.AddWithValue("@consumer", c);
                cmd.Parameters.AddWithValue("@last_acked_seq", lastSeq);
                cmd.Parameters.AddWithValue("@updated_at", nowText);

                try
                {
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (
                    ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */ &&
                    ex.Message.Contains("export_id", StringComparison.OrdinalIgnoreCase))
                {
                    // export_id already exists (possibly under different session/consumer).
                    // If same session+consumer, update seq monotonically; else fail.
                    await using var byExport = conn.CreateCommand();
                    byExport.CommandText = """
                        SELECT session_id, consumer, last_acked_seq
                        FROM event_exports
                        WHERE export_id = @export_id
                        LIMIT 1;
                        """;
                    byExport.Parameters.AddWithValue("@export_id", exportId);
                    await using var reader = await byExport.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                        return RuntimeResult<ExportAckRecord>.Fail(RuntimePersistenceError.Db(ex.Message));

                    var existingSession = reader.GetString(0);
                    var existingConsumer = reader.GetString(1);
                    var existingSeq = reader.GetInt64(2);
                    await reader.DisposeAsync().ConfigureAwait(false);

                    if (!string.Equals(existingSession, sessionId, StringComparison.Ordinal) ||
                        !string.Equals(existingConsumer, c, StringComparison.Ordinal))
                    {
                        return RuntimeResult<ExportAckRecord>.Fail(
                            RuntimePersistenceError.Db(
                                "export_id already bound to a different session/consumer"));
                    }

                    var nextSeq = Math.Max(existingSeq, lastSeq);
                    await using var upd = conn.CreateCommand();
                    upd.CommandText = """
                        UPDATE event_exports
                        SET last_acked_seq = @last_acked_seq, updated_at = @updated_at
                        WHERE export_id = @export_id;
                        """;
                    upd.Parameters.AddWithValue("@last_acked_seq", nextSeq);
                    upd.Parameters.AddWithValue("@updated_at", nowText);
                    upd.Parameters.AddWithValue("@export_id", exportId);
                    await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // Read back the stored high-water (may be > requested lastSeq).
                await using var getCmd = conn.CreateCommand();
                getCmd.CommandText = """
                    SELECT export_id, session_id, consumer, last_acked_seq, updated_at
                    FROM event_exports
                    WHERE session_id = @session_id AND consumer = @consumer
                    LIMIT 1;
                    """;
                getCmd.Parameters.AddWithValue("@session_id", sessionId);
                getCmd.Parameters.AddWithValue("@consumer", c);
                await using var getReader = await getCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await getReader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return RuntimeResult<ExportAckRecord>.Fail(
                        RuntimePersistenceError.Db("export ack upsert did not persist a row"));
                }

                var updatedRaw = getReader.GetString(4);
                var updated = DurableTimestampParser.ParseOrSentinel(updatedRaw);

                var record = new ExportAckRecord
                {
                    ExportId = getReader.GetString(0),
                    SessionId = getReader.GetString(1),
                    Consumer = getReader.GetString(2),
                    LastAckedSeq = getReader.GetInt64(3),
                    UpdatedAt = updated,
                };
                return RuntimeResult<ExportAckRecord>.Ok(record);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (SqliteException ex)
        {
            return RuntimeResult<ExportAckRecord>.Fail(RuntimePersistenceError.Db(ex.Message));
        }
        catch (IOException ex)
        {
            return RuntimeResult<ExportAckRecord>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return RuntimeResult<ExportAckRecord>.Fail(RuntimePersistenceError.Access(ex.Message));
        }
    }

    public async Task<RuntimeResult<ExportAckRecord?>> GetAsync(
        string exportId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_paths.DatabasePath))
                    return RuntimeResult<ExportAckRecord?>.Ok(null);

                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT export_id, session_id, consumer, last_acked_seq, updated_at
                    FROM event_exports
                    WHERE export_id = @export_id
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("@export_id", exportId);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return RuntimeResult<ExportAckRecord?>.Ok(null);

                var updatedRaw = reader.GetString(4);
                var updated = DurableTimestampParser.ParseOrSentinel(updatedRaw);

                var record = new ExportAckRecord
                {
                    ExportId = reader.GetString(0),
                    SessionId = reader.GetString(1),
                    Consumer = reader.GetString(2),
                    LastAckedSeq = reader.GetInt64(3),
                    UpdatedAt = updated,
                };
                return RuntimeResult<ExportAckRecord?>.Ok(record);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (SqliteException ex)
        {
            return RuntimeResult<ExportAckRecord?>.Fail(RuntimePersistenceError.Db(ex.Message));
        }
        catch (IOException ex)
        {
            return RuntimeResult<ExportAckRecord?>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return RuntimeResult<ExportAckRecord?>.Fail(RuntimePersistenceError.Access(ex.Message));
        }
    }

    private SqliteConnection Open() => new($"Data Source={_paths.DatabasePath}");
}
