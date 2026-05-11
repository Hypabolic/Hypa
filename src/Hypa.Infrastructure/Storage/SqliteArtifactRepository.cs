using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Data.Sqlite;

namespace Hypa.Infrastructure.Storage;

public sealed class SqliteArtifactRepository(HypaDataOptions options, SqliteSchemaInitializer schema) : IArtifactRepository
{
    public async Task<Result<ArtifactRef, Error>> StoreAsync(string content, string mimeType, Guid sessionId, CancellationToken ct)
    {
        await schema.EnsureAsync(ct);
        Directory.CreateDirectory(options.ArtifactsDirectory);

        var id = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes(content);
        var storagePath = Path.Combine(options.ArtifactsDirectory, id.ToString());
        await File.WriteAllBytesAsync(storagePath, bytes, ct);

        var artifact = new ArtifactRef
        {
            Id = id,
            SessionId = sessionId,
            MimeType = mimeType,
            SizeBytes = bytes.Length,
            StoragePath = storagePath,
        };

        await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO artifact_refs (id, session_id, created_at, mime_type, size_bytes, storage_path)
            VALUES (@id, @sessionId, @createdAt, @mimeType, @sizeBytes, @storagePath)
            """;
        cmd.Parameters.AddWithValue("@id", artifact.Id.ToString());
        cmd.Parameters.AddWithValue("@sessionId", artifact.SessionId.ToString());
        cmd.Parameters.AddWithValue("@createdAt", artifact.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@mimeType", artifact.MimeType);
        cmd.Parameters.AddWithValue("@sizeBytes", artifact.SizeBytes);
        cmd.Parameters.AddWithValue("@storagePath", artifact.StoragePath);
        await cmd.ExecuteNonQueryAsync(ct);

        return Result<ArtifactRef, Error>.Ok(artifact);
    }

    public async Task<Result<string, Error>> LoadAsync(Guid artifactId, CancellationToken ct)
    {
        await schema.EnsureAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT storage_path FROM artifact_refs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", artifactId.ToString());
        var path = (string?)await cmd.ExecuteScalarAsync(ct);
        if (path is null)
            return Result<string, Error>.Fail(new Error("ARTIFACT_NOT_FOUND", $"Artifact {artifactId} not found."));
        if (!File.Exists(path))
            return Result<string, Error>.Fail(new Error("ARTIFACT_FILE_MISSING", $"Artifact file missing at {path}."));
        return Result<string, Error>.Ok(await File.ReadAllTextAsync(path, ct));
    }

    public async Task<Result<IReadOnlyList<ArtifactRef>, Error>> ListForSessionAsync(Guid sessionId, CancellationToken ct)
    {
        await schema.EnsureAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, session_id, created_at, mime_type, size_bytes, storage_path FROM artifact_refs WHERE session_id = @sessionId ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@sessionId", sessionId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var artifacts = new List<ArtifactRef>();
        while (await reader.ReadAsync(ct))
        {
            artifacts.Add(new ArtifactRef
            {
                Id = Guid.Parse(reader.GetString(0)),
                SessionId = Guid.Parse(reader.GetString(1)),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(2)),
                MimeType = reader.GetString(3),
                SizeBytes = reader.GetInt64(4),
                StoragePath = reader.GetString(5),
            });
        }
        return Result<IReadOnlyList<ArtifactRef>, Error>.Ok(artifacts);
    }
}
