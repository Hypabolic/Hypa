using System.Globalization;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Microsoft.Data.Sqlite;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// SQLite adapter for <c>journal_segments</c> and <c>event_cursor</c>.
/// </summary>
public sealed class SqliteJournalManifestStore : IJournalManifestStore, IAsyncDisposable
{
    private readonly RuntimeStatePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _connectionsOpened;

    /// <summary>Test counter: connections opened by this store.</summary>
    internal int ConnectionsOpened => Volatile.Read(ref _connectionsOpened);

    public SqliteJournalManifestStore(RuntimeStatePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<RuntimeResult<RuntimeUnit>> UpsertSegmentAsync(
        JournalSegmentManifest segment, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO journal_segments(
                      segment_id, session_id, relative_path, first_seq, last_seq,
                      record_count, byte_count, checksum_sha256, closed, created_at, closed_at)
                    VALUES (
                      @segment_id, @session_id, @relative_path, @first_seq, @last_seq,
                      @record_count, @byte_count, @checksum_sha256, @closed, @created_at, @closed_at)
                    ON CONFLICT(segment_id) DO UPDATE SET
                      relative_path = excluded.relative_path,
                      first_seq = excluded.first_seq,
                      last_seq = excluded.last_seq,
                      record_count = excluded.record_count,
                      byte_count = excluded.byte_count,
                      checksum_sha256 = excluded.checksum_sha256,
                      closed = excluded.closed,
                      closed_at = excluded.closed_at;
                    """;
                BindSegment(cmd, segment);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return FailUnit(ex);
        }
    }

    public async Task<RuntimeResult<IReadOnlyList<JournalSegmentManifest>>> ListSegmentsAsync(
        string sessionId, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_paths.DatabasePath))
                    return RuntimeResult<IReadOnlyList<JournalSegmentManifest>>.Ok([]);

                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT segment_id, session_id, relative_path, first_seq, last_seq,
                           record_count, byte_count, checksum_sha256, closed, created_at, closed_at
                    FROM journal_segments
                    WHERE session_id = @sid
                    ORDER BY first_seq ASC;
                    """;
                cmd.Parameters.AddWithValue("@sid", sessionId);
                var list = new List<JournalSegmentManifest>();
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    list.Add(ReadSegment(reader));
                return RuntimeResult<IReadOnlyList<JournalSegmentManifest>>.Ok(list);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return RuntimeResult<IReadOnlyList<JournalSegmentManifest>>.Fail(MapError(ex));
        }
    }

    public async Task<RuntimeResult<JournalSegmentManifest?>> GetOpenSegmentAsync(
        string sessionId, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_paths.DatabasePath))
                    return RuntimeResult<JournalSegmentManifest?>.Ok(null);

                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT segment_id, session_id, relative_path, first_seq, last_seq,
                           record_count, byte_count, checksum_sha256, closed, created_at, closed_at
                    FROM journal_segments
                    WHERE session_id = @sid AND closed = 0
                    ORDER BY first_seq DESC
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("@sid", sessionId);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return RuntimeResult<JournalSegmentManifest?>.Ok(null);
                return RuntimeResult<JournalSegmentManifest?>.Ok(ReadSegment(reader));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return RuntimeResult<JournalSegmentManifest?>.Fail(MapError(ex));
        }
    }

    public async Task<RuntimeResult<RuntimeUnit>> CloseSegmentAsync(
        string segmentId,
        long? lastSeq,
        int recordCount,
        long byteCount,
        string checksumSha256,
        DateTimeOffset closedAt,
        CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE journal_segments SET
                      last_seq = @last_seq,
                      record_count = @record_count,
                      byte_count = @byte_count,
                      checksum_sha256 = @checksum,
                      closed = 1,
                      closed_at = @closed_at
                    WHERE segment_id = @segment_id;
                    """;
                cmd.Parameters.AddWithValue("@segment_id", segmentId);
                cmd.Parameters.AddWithValue("@last_seq", (object?)lastSeq ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@record_count", recordCount);
                cmd.Parameters.AddWithValue("@byte_count", byteCount);
                cmd.Parameters.AddWithValue("@checksum", checksumSha256);
                cmd.Parameters.AddWithValue("@closed_at", FormatTs(closedAt));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return FailUnit(ex);
        }
    }

    public async Task<RuntimeResult<long>> ReadEventCursorAsync(
        string sessionId, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_paths.DatabasePath))
                    return RuntimeResult<long>.Ok(0);

                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT next_seq FROM event_cursor WHERE session_id = @sid";
                cmd.Parameters.AddWithValue("@sid", sessionId);
                var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (obj is null or DBNull)
                    return RuntimeResult<long>.Ok(0);
                return RuntimeResult<long>.Ok(Convert.ToInt64(obj, CultureInfo.InvariantCulture));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return RuntimeResult<long>.Fail(MapError(ex));
        }
    }

    public Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
        JournalSegmentManifest segment,
        string sessionId,
        long nextSeq,
        CancellationToken ct = default) =>
        FlushSegmentProgressCoreAsync(segment, sessionId, nextSeq, floorSeq: 0, ct);

    public Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
        JournalSegmentManifest segment,
        string sessionId,
        long nextSeq,
        long floorSeq,
        CancellationToken ct = default) =>
        FlushSegmentProgressCoreAsync(segment, sessionId, nextSeq, floorSeq, ct);

    private async Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressCoreAsync(
        JournalSegmentManifest segment,
        string sessionId,
        long nextSeq,
        long floorSeq,
        CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsureEventCursorFloorColumnAsync(conn, ct)
                    .ConfigureAwait(false);
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct)
                    .ConfigureAwait(false);

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO journal_segments(
                          segment_id, session_id, relative_path, first_seq, last_seq,
                          record_count, byte_count, checksum_sha256, closed, created_at, closed_at)
                        VALUES (
                          @segment_id, @session_id, @relative_path, @first_seq, @last_seq,
                          @record_count, @byte_count, @checksum_sha256, @closed, @created_at, @closed_at)
                        ON CONFLICT(segment_id) DO UPDATE SET
                          relative_path = excluded.relative_path,
                          first_seq = excluded.first_seq,
                          last_seq = excluded.last_seq,
                          record_count = excluded.record_count,
                          byte_count = excluded.byte_count,
                          checksum_sha256 = excluded.checksum_sha256,
                          closed = excluded.closed,
                          closed_at = excluded.closed_at
                        WHERE journal_segments.closed = 0;
                        """;
                    BindSegment(cmd, segment);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO event_cursor(session_id, next_seq, floor_seq)
                        VALUES (@session_id, @next_seq, @floor_seq)
                        ON CONFLICT(session_id) DO UPDATE SET
                          next_seq = CASE
                            WHEN event_cursor.next_seq <= excluded.next_seq THEN excluded.next_seq
                            ELSE event_cursor.next_seq END,
                          floor_seq = MAX(event_cursor.floor_seq, excluded.floor_seq);
                        """;
                    cmd.Parameters.AddWithValue("@session_id", sessionId);
                    cmd.Parameters.AddWithValue("@next_seq", nextSeq);
                    cmd.Parameters.AddWithValue("@floor_seq", floorSeq);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var meta = conn.CreateCommand())
                {
                    meta.Transaction = tx;
                    meta.CommandText = """
                        INSERT INTO runtime_meta(key, value) VALUES (@key, @value)
                        ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                        """;
                    meta.Parameters.AddWithValue("@key", RuntimePersistenceSchema.EventSeqKey);
                    meta.Parameters.AddWithValue(
                        "@value", nextSeq.ToString(CultureInfo.InvariantCulture));
                    await meta.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return FailUnit(ex);
        }
    }

    public async Task<RuntimeResult<RuntimeUnit>> WriteEventCursorAsync(
        string sessionId, long nextSeq, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct)
                    .ConfigureAwait(false);

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO event_cursor(session_id, next_seq)
                        VALUES (@session_id, @next_seq)
                        ON CONFLICT(session_id) DO UPDATE SET next_seq = excluded.next_seq;
                        """;
                    cmd.Parameters.AddWithValue("@session_id", sessionId);
                    cmd.Parameters.AddWithValue("@next_seq", nextSeq);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var meta = conn.CreateCommand())
                {
                    meta.Transaction = tx;
                    meta.CommandText = """
                        INSERT INTO runtime_meta(key, value) VALUES (@key, @value)
                        ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                        """;
                    meta.Parameters.AddWithValue("@key", RuntimePersistenceSchema.EventSeqKey);
                    meta.Parameters.AddWithValue(
                        "@value", nextSeq.ToString(CultureInfo.InvariantCulture));
                    await meta.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return FailUnit(ex);
        }
    }

    public async Task<RuntimeResult<long>> ReadRetentionFloorAsync(
        string sessionId, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_paths.DatabasePath))
                    return RuntimeResult<long>.Ok(0);

                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsureEventCursorFloorColumnAsync(conn, ct)
                    .ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT floor_seq FROM event_cursor WHERE session_id = @sid;
                    """;
                cmd.Parameters.AddWithValue("@sid", sessionId);
                var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (obj is null or DBNull)
                    return RuntimeResult<long>.Ok(0);
                return RuntimeResult<long>.Ok(Convert.ToInt64(obj, CultureInfo.InvariantCulture));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return RuntimeResult<long>.Fail(MapError(ex));
        }
    }

    public async Task<RuntimeResult<RuntimeUnit>> RaiseRetentionFloorAsync(
        string sessionId, long floorSeq, long nextSeq, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsureEventCursorFloorColumnAsync(conn, ct)
                    .ConfigureAwait(false);
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct)
                    .ConfigureAwait(false);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO event_cursor(session_id, next_seq, floor_seq)
                        VALUES (@session_id, @next_seq, @floor_seq)
                        ON CONFLICT(session_id) DO UPDATE SET
                          next_seq = MAX(event_cursor.next_seq, excluded.next_seq),
                          floor_seq = MAX(event_cursor.floor_seq, excluded.floor_seq);
                        """;
                    cmd.Parameters.AddWithValue("@session_id", sessionId);
                    cmd.Parameters.AddWithValue("@next_seq", nextSeq);
                    cmd.Parameters.AddWithValue("@floor_seq", floorSeq);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await WriteEventSeqMetaAsync(conn, tx, nextSeq, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return FailUnit(ex);
        }
    }

    public async Task<RuntimeResult<RuntimeUnit>> DeleteClosedSegmentAsync(
        string segmentId, string sessionId, long floorSeq, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsureEventCursorFloorColumnAsync(conn, ct)
                    .ConfigureAwait(false);
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct)
                    .ConfigureAwait(false);
                int deleted;
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        DELETE FROM journal_segments
                        WHERE segment_id = @id AND session_id = @sid AND closed = 1;
                        """;
                    cmd.Parameters.AddWithValue("@id", segmentId);
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                if (deleted == 0)
                {
                    return RuntimeResult<RuntimeUnit>.Fail(
                        RuntimePersistenceError.Io("closed segment manifest row was not deleted"));
                }

                await using (var floor = conn.CreateCommand())
                {
                    floor.Transaction = tx;
                    floor.CommandText = """
                        INSERT INTO event_cursor(session_id, next_seq, floor_seq)
                        VALUES (@sid, 0, @floor)
                        ON CONFLICT(session_id) DO UPDATE SET
                          floor_seq = MAX(event_cursor.floor_seq, excluded.floor_seq);
                        """;
                    floor.Parameters.AddWithValue("@sid", sessionId);
                    floor.Parameters.AddWithValue("@floor", floorSeq);
                    await floor.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return FailUnit(ex);
        }
    }

    private static async Task WriteEventSeqMetaAsync(
        SqliteConnection conn, SqliteTransaction tx, long nextSeq, CancellationToken ct)
    {
        await using var meta = conn.CreateCommand();
        meta.Transaction = tx;
        meta.CommandText = """
            INSERT INTO runtime_meta(key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        meta.Parameters.AddWithValue("@key", RuntimePersistenceSchema.EventSeqKey);
        meta.Parameters.AddWithValue("@value", nextSeq.ToString(CultureInfo.InvariantCulture));
        await meta.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private SqliteConnection Open()
    {
        _paths.EnsureDirectory();
        Interlocked.Increment(ref _connectionsOpened);
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        }.ToString());
    }

    private static void BindSegment(SqliteCommand cmd, JournalSegmentManifest s)
    {
        cmd.Parameters.AddWithValue("@segment_id", s.SegmentId);
        cmd.Parameters.AddWithValue("@session_id", s.SessionId);
        cmd.Parameters.AddWithValue("@relative_path", s.RelativePath);
        cmd.Parameters.AddWithValue("@first_seq", s.FirstSeq);
        cmd.Parameters.AddWithValue("@last_seq", (object?)s.LastSeq ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@record_count", s.RecordCount);
        cmd.Parameters.AddWithValue("@byte_count", s.ByteCount);
        cmd.Parameters.AddWithValue("@checksum_sha256", (object?)s.ChecksumSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@closed", s.Closed ? 1 : 0);
        cmd.Parameters.AddWithValue("@created_at", FormatTs(s.CreatedAt));
        cmd.Parameters.AddWithValue("@closed_at", s.ClosedAt is { } ca ? FormatTs(ca) : DBNull.Value);
    }

    private static JournalSegmentManifest ReadSegment(SqliteDataReader reader) => new()
    {
        SegmentId = reader.GetString(0),
        SessionId = reader.GetString(1),
        RelativePath = reader.GetString(2),
        FirstSeq = reader.GetInt64(3),
        LastSeq = reader.IsDBNull(4) ? null : reader.GetInt64(4),
        RecordCount = reader.GetInt32(5),
        ByteCount = reader.GetInt64(6),
        ChecksumSha256 = reader.IsDBNull(7) ? null : reader.GetString(7),
        Closed = reader.GetInt64(8) != 0,
        CreatedAt = ParseTs(reader.GetString(9)),
        ClosedAt = reader.IsDBNull(10) ? null : ParseTs(reader.GetString(10)),
    };

    private static string FormatTs(DateTimeOffset dto) =>
        dto.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTs(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static RuntimeResult<RuntimeUnit> FailUnit(Exception ex) =>
        RuntimeResult<RuntimeUnit>.Fail(MapError(ex));

    private static RuntimePersistenceError MapError(Exception ex) => ex switch
    {
        SqliteException se => RuntimePersistenceError.Db(se.Message),
        UnauthorizedAccessException ua => RuntimePersistenceError.Access(ua.Message),
        _ => RuntimePersistenceError.Io(ex.Message),
    };

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
