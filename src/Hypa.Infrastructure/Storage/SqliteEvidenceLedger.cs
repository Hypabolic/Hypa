using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Storage;

public sealed class SqliteEvidenceLedger(
    HypaDataOptions options,
    SqliteSchemaInitializer schema,
    ILogger<SqliteEvidenceLedger> logger) : IEvidenceLedger
{
    public async Task RecordToolCallAsync(ToolCallRecord record, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return;
            await InsertAsync(record.Id, record.SessionId, record.RecordedAt, "ToolCall",
                JsonSerializer.Serialize(record, StorageJsonContext.Default.ToolCallRecord), ct);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            logger.LogDebug(ex, "Failed to record tool call evidence");
        }
    }

    public async Task RecordEvidenceAsync(EvidenceRecord record, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return;
            var (kind, payload) = record switch
            {
                FileTouchRecord r => ("FileTouch", JsonSerializer.Serialize(r, StorageJsonContext.Default.FileTouchRecord)),
                _ => throw new ArgumentException($"Unknown evidence type: {record.GetType().Name}"),
            };
            await InsertAsync(record.Id, record.SessionId, record.RecordedAt, kind, payload, ct);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            logger.LogDebug(ex, "Failed to record evidence");
        }
    }

    public async Task RecordArtifactAsync(ArtifactRef artifact, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return;
            await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO artifact_refs (id, session_id, created_at, mime_type, size_bytes, storage_path)
                VALUES (@id, @sessionId, @createdAt, @mimeType, @sizeBytes, @storagePath)
                """;
            cmd.Parameters.AddWithValue("@id", artifact.Id.ToString());
            cmd.Parameters.AddWithValue("@sessionId", artifact.SessionId.ToString());
            cmd.Parameters.AddWithValue("@createdAt", artifact.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@mimeType", artifact.MimeType);
            cmd.Parameters.AddWithValue("@sizeBytes", artifact.SizeBytes);
            cmd.Parameters.AddWithValue("@storagePath", artifact.StoragePath);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            logger.LogDebug(ex, "Failed to record artifact reference");
        }
    }

    public async Task RecordCommandMetricsAsync(CommandMetricsRecord record, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return;
            await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO command_metrics
                    (id, session_id, recorded_at, command, exit_code, duration_ms,
                     original_tokens, compressed_tokens, reducer_id, tee_artifact_id)
                VALUES
                    (@id, @sessionId, @recordedAt, @command, @exitCode, @durationMs,
                     @originalTokens, @compressedTokens, @reducerId, @teeArtifactId)
                """;
            cmd.Parameters.AddWithValue("@id", record.Id.ToString());
            cmd.Parameters.AddWithValue("@sessionId", record.SessionId.ToString());
            cmd.Parameters.AddWithValue("@recordedAt", record.RecordedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@command", record.Command);
            cmd.Parameters.AddWithValue("@exitCode", record.ExitCode);
            cmd.Parameters.AddWithValue("@durationMs", record.DurationMs);
            cmd.Parameters.AddWithValue("@originalTokens", record.OriginalTokens);
            cmd.Parameters.AddWithValue("@compressedTokens", record.CompressedTokens);
            cmd.Parameters.AddWithValue("@reducerId", record.ReducerId);
            cmd.Parameters.AddWithValue("@teeArtifactId",
                record.TeeArtifactId.HasValue ? record.TeeArtifactId.Value.ToString() : DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            logger.LogDebug(ex, "Failed to record command metrics");
        }
    }

    private async Task InsertAsync(Guid id, Guid sessionId, DateTimeOffset recordedAt, string kind, string payloadJson, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO evidence_records (id, session_id, recorded_at, kind, payload_json)
            VALUES (@id, @sessionId, @recordedAt, @kind, @payload)
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@sessionId", sessionId.ToString());
        cmd.Parameters.AddWithValue("@recordedAt", recordedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@payload", payloadJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
