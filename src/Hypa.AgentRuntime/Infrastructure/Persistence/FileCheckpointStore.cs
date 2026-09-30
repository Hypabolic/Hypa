using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Microsoft.Data.Sqlite;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// File + optional SQLite index adapter for checkpoints under
/// <c>{stateDir}/checkpoints/{id}/</c>.
/// </summary>
public sealed class FileCheckpointStore : ICheckpointStore
{
    private readonly RuntimeStatePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileCheckpointStore(RuntimeStatePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<RuntimeResult<CheckpointRecord>> SaveAsync(
        CheckpointRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _paths.EnsureDirectory();
                _paths.EnsureCheckpointsDirectory();
                Directory.CreateDirectory(GetCheckpointDirectory(record.CheckpointId));

                var existing = await ReadExistingUnlockedAsync(record.CheckpointId, ct)
                    .ConfigureAwait(false);
                if (existing is not null
                    && !CheckpointStates.CanTransition(existing.State, record.State))
                {
                    return RuntimeResult<CheckpointRecord>.Fail(
                        RuntimePersistenceError.Conflict(
                            $"illegal checkpoint transition {existing.State} -> {record.State}"));
                }

                // Sidecar first (includes pin). SQLite CAS next. A rejected transition
                // never replaces the live sidecar.
                var recordPath = Path.Combine(GetCheckpointDirectory(record.CheckpointId), "record.json");
                var tmpPath = recordPath + ".tmp";
                var dto = CheckpointStorageDto.FromDomain(record);
                var json = JsonSerializer.Serialize(dto, RuntimeStorageJsonContext.Default.CheckpointStorageDto);
                await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);

                if (File.Exists(_paths.DatabasePath))
                {
                    var upserted = await UpsertSqliteAsync(record, ct).ConfigureAwait(false);
                    if (!upserted)
                    {
                        try
                        {
                            File.Delete(tmpPath);
                        }
                        catch (IOException)
                        {
                            // best-effort
                        }

                        return RuntimeResult<CheckpointRecord>.Fail(
                            RuntimePersistenceError.Conflict(
                                $"illegal checkpoint transition {existing?.State ?? "missing"} -> {record.State}"));
                    }
                }

                File.Move(tmpPath, recordPath, overwrite: true);
                return RuntimeResult<CheckpointRecord>.Ok(record);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return RuntimeResult<CheckpointRecord>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
    }

    public async Task<RuntimeResult<CheckpointRecord?>> GetAsync(
        string checkpointId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var existing = await ReadExistingUnlockedAsync(checkpointId, ct).ConfigureAwait(false);
                return RuntimeResult<CheckpointRecord?>.Ok(existing);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return RuntimeResult<CheckpointRecord?>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
    }

    public async Task<RuntimeResult<IReadOnlyList<CheckpointRecord>>> ListBySessionAsync(
        string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                if (File.Exists(_paths.DatabasePath))
                {
                    foreach (var id in await ListSqliteIdsAsync(sessionId, ct).ConfigureAwait(false))
                        ids.Add(id);
                }

                var root = _paths.CheckpointsDirectory;
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        var sidecar = Path.Combine(dir, "record.json");
                        if (!File.Exists(sidecar))
                            continue;
                        ids.Add(Path.GetFileName(dir));
                    }
                }

                var list = new List<CheckpointRecord>();
                foreach (var id in ids)
                {
                    var rec = await ReadExistingUnlockedAsync(id, ct).ConfigureAwait(false);
                    if (rec is not null
                        && string.Equals(rec.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        list.Add(rec);
                    }
                }

                return RuntimeResult<IReadOnlyList<CheckpointRecord>>.Ok(list);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return RuntimeResult<IReadOnlyList<CheckpointRecord>>.Fail(
                RuntimePersistenceError.Io(ex.Message));
        }
    }

    public async Task<RuntimeResult<CheckpointManifestWriteResult>> WriteManifestAsync(
        string checkpointId,
        string manifestJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var dir = GetCheckpointDirectory(checkpointId);
                Directory.CreateDirectory(dir);
                var abs = Path.Combine(dir, "manifest.json");
                var bytes = Encoding.UTF8.GetBytes(manifestJson);
                await File.WriteAllBytesAsync(abs, bytes, ct).ConfigureAwait(false);
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                return RuntimeResult<CheckpointManifestWriteResult>.Ok(new CheckpointManifestWriteResult
                {
                    ManifestRelativePath = GetCheckpointRelativeDirectory(checkpointId) + "/manifest.json",
                    ManifestSha256 = sha,
                    ByteCount = bytes.LongLength,
                });
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RuntimeResult<CheckpointManifestWriteResult>.Fail(
                RuntimePersistenceError.Io(ex.Message));
        }
    }

    public string GetCheckpointDirectory(string checkpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        return Path.Combine(_paths.CheckpointsDirectory, SanitizeId(checkpointId));
    }

    public string GetCheckpointRelativeDirectory(string checkpointId) =>
        RuntimeStatePaths.CheckpointsDirectoryName + "/" + SanitizeId(checkpointId);

    private static string SanitizeId(string checkpointId)
    {
        if (checkpointId.Contains("..", StringComparison.Ordinal) ||
            checkpointId.Contains('/') ||
            checkpointId.Contains('\\') ||
            checkpointId.Contains(':'))
        {
            throw new ArgumentException(
                "checkpoint_id must not contain path separators", nameof(checkpointId));
        }

        return checkpointId;
    }

    private async Task<CheckpointRecord?> ReadExistingUnlockedAsync(
        string checkpointId, CancellationToken ct)
    {
        var recordPath = Path.Combine(GetCheckpointDirectory(checkpointId), "record.json");
        if (File.Exists(recordPath))
        {
            var text = await File.ReadAllTextAsync(recordPath, ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize(
                text, RuntimeStorageJsonContext.Default.CheckpointStorageDto);
            if (dto is not null)
                return dto.ToDomain();
        }

        if (File.Exists(_paths.DatabasePath))
            return await ReadSqliteAsync(checkpointId, ct).ConfigureAwait(false);

        return null;
    }

    private async Task<bool> UpsertSqliteAsync(CheckpointRecord record, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
        await SqliteRuntimeSchemaMigrator.EnsureCheckpointsTableAsync(conn, ct).ConfigureAwait(false);

        var warningsJson = record.Warnings is { Count: > 0 }
            ? JsonSerializer.Serialize(record.Warnings.ToList(), RuntimeStorageJsonContext.Default.ListString)
            : null;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO checkpoints(
              checkpoint_id, session_id, state, barrier_seq, next_seq_at_prepare,
              session_fingerprint, reason, include_scrollback, include_workspace_files,
              created_at, exported_at, manifest_relative_path, manifest_sha256,
              byte_count, transfer_incomplete, warnings_json, placement, placement_generation,
              replay_complete, project_root_device, project_root_inode, project_root_was_symlink)
            VALUES (
              @checkpoint_id, @session_id, @state, @barrier_seq, @next_seq_at_prepare,
              @session_fingerprint, @reason, @include_scrollback, @include_workspace_files,
              @created_at, @exported_at, @manifest_relative_path, @manifest_sha256,
              @byte_count, @transfer_incomplete, @warnings_json, @placement, @placement_generation,
              @replay_complete, @project_root_device, @project_root_inode, @project_root_was_symlink)
            ON CONFLICT(checkpoint_id) DO UPDATE SET
              state = excluded.state,
              exported_at = excluded.exported_at,
              manifest_relative_path = excluded.manifest_relative_path,
              manifest_sha256 = excluded.manifest_sha256,
              byte_count = excluded.byte_count,
              transfer_incomplete = excluded.transfer_incomplete,
              warnings_json = excluded.warnings_json
            WHERE checkpoints.state = excluded.state
               OR (
                    checkpoints.state = 'prepared'
                    AND excluded.state IN ('exported', 'aborted', 'conflict', 'failed')
                  )
               OR (
                    checkpoints.state = 'failed'
                    AND excluded.state IN ('exported', 'aborted', 'conflict')
                  );
            """;
        cmd.Parameters.AddWithValue("@checkpoint_id", record.CheckpointId);
        cmd.Parameters.AddWithValue("@session_id", record.SessionId);
        cmd.Parameters.AddWithValue("@state", record.State);
        cmd.Parameters.AddWithValue("@barrier_seq", record.BarrierSeq);
        cmd.Parameters.AddWithValue("@next_seq_at_prepare", record.NextSeqAtPrepare);
        cmd.Parameters.AddWithValue("@session_fingerprint", record.SessionFingerprint);
        cmd.Parameters.AddWithValue("@reason", (object?)record.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@include_scrollback", record.IncludeScrollback ? 1 : 0);
        cmd.Parameters.AddWithValue("@include_workspace_files", record.IncludeWorkspaceFiles ? 1 : 0);
        cmd.Parameters.AddWithValue(
            "@created_at",
            record.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue(
            "@exported_at",
            (object?)record.ExportedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@manifest_relative_path", (object?)record.ManifestRelativePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@manifest_sha256", (object?)record.ManifestSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@byte_count", (object?)record.ByteCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@transfer_incomplete", record.TransferIncomplete ? 1 : 0);
        cmd.Parameters.AddWithValue("@warnings_json", (object?)warningsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@placement", record.Placement);
        cmd.Parameters.AddWithValue("@placement_generation", record.PlacementGeneration);
        cmd.Parameters.AddWithValue("@replay_complete", record.ReplayComplete ? 1 : 0);
        cmd.Parameters.AddWithValue(
            "@project_root_device",
            record.ProjectRootDevice.HasValue
                ? (object)unchecked((long)record.ProjectRootDevice.Value)
                : DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@project_root_inode",
            record.ProjectRootInode.HasValue
                ? (object)unchecked((long)record.ProjectRootInode.Value)
                : DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@project_root_was_symlink",
            record.ProjectRootWasSymlink.HasValue
                ? (object)(record.ProjectRootWasSymlink.Value ? 1 : 0)
                : DBNull.Value);
        var changed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return changed > 0;
    }

    private async Task<IReadOnlyList<string>> ListSqliteIdsAsync(string sessionId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
        await SqliteRuntimeSchemaMigrator.EnsureCheckpointsTableAsync(conn, ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT checkpoint_id FROM checkpoints WHERE session_id = @sid;
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(reader.GetString(0));
        return ids;
    }

    private async Task<CheckpointRecord?> ReadSqliteAsync(string checkpointId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
        await SqliteRuntimeSchemaMigrator.EnsureCheckpointsTableAsync(conn, ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT checkpoint_id, session_id, state, barrier_seq, next_seq_at_prepare,
                   session_fingerprint, reason, include_scrollback, include_workspace_files,
                   created_at, exported_at, manifest_relative_path, manifest_sha256,
                   byte_count, transfer_incomplete, warnings_json, placement, placement_generation,
                   replay_complete, project_root_device, project_root_inode, project_root_was_symlink
            FROM checkpoints
            WHERE checkpoint_id = @id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@id", checkpointId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var warnings = Array.Empty<string>();
        if (!reader.IsDBNull(15))
        {
            var wj = reader.GetString(15);
            var list = JsonSerializer.Deserialize(wj, RuntimeStorageJsonContext.Default.ListString);
            if (list is not null)
                warnings = list.ToArray();
        }

        return new CheckpointRecord
        {
            CheckpointId = reader.GetString(0),
            SessionId = reader.GetString(1),
            State = reader.GetString(2),
            BarrierSeq = reader.GetInt64(3),
            NextSeqAtPrepare = reader.GetInt64(4),
            SessionFingerprint = reader.GetString(5),
            Reason = reader.IsDBNull(6) ? null : reader.GetString(6),
            IncludeScrollback = reader.GetInt64(7) != 0,
            IncludeWorkspaceFiles = reader.GetInt64(8) != 0,
            CreatedAt = ParseTime(reader.GetString(9)),
            ExportedAt = reader.IsDBNull(10) ? null : ParseTime(reader.GetString(10)),
            ManifestRelativePath = reader.IsDBNull(11) ? null : reader.GetString(11),
            ManifestSha256 = reader.IsDBNull(12) ? null : reader.GetString(12),
            ByteCount = reader.IsDBNull(13) ? null : reader.GetInt64(13),
            TransferIncomplete = reader.GetInt64(14) != 0,
            Warnings = warnings,
            Placement = reader.IsDBNull(16) ? "local" : reader.GetString(16),
            PlacementGeneration = reader.IsDBNull(17) ? 0 : reader.GetInt32(17),
            ReplayComplete = reader.IsDBNull(18) || reader.GetInt64(18) != 0,
            ProjectRootDevice = ReadUlong(reader, 19),
            ProjectRootInode = ReadUlong(reader, 20),
            ProjectRootWasSymlink = reader.IsDBNull(21) ? null : reader.GetInt64(21) != 0,
        };
    }

    private static ulong? ReadUlong(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        return unchecked((ulong)reader.GetInt64(ordinal));
    }

    private static DateTimeOffset ParseTime(string raw) =>
        DurableTimestampParser.ParseOrSentinel(raw);
}
