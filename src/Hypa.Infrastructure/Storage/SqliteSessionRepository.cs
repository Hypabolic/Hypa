using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Data.Sqlite;

namespace Hypa.Infrastructure.Storage;

public sealed class SqliteSessionRepository(HypaDataOptions options, SqliteSchemaInitializer schema) : ISessionRepository
{
    public async Task<Result<ContextSession, Error>> LoadAsync(Guid id, CancellationToken ct)
    {
        await schema.EnsureAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_root, atomic_agent_session_id, external_ref, created_at, updated_at, checkpointed_at, stats_json FROM sessions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return Result<ContextSession, Error>.Fail(new Error("SESSION_NOT_FOUND", $"Session {id} not found."));
        return Result<ContextSession, Error>.Ok(MapRow(reader));
    }

    public async Task<Result<ContextSession, Error>> LoadLatestForProjectAsync(string projectRoot, CancellationToken ct)
    {
        await schema.EnsureAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_root, atomic_agent_session_id, external_ref, created_at, updated_at, checkpointed_at, stats_json FROM sessions WHERE project_root = @root ORDER BY updated_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@root", projectRoot);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return Result<ContextSession, Error>.Fail(new Error("SESSION_NOT_FOUND", $"No session found for project {projectRoot}."));
        return Result<ContextSession, Error>.Ok(MapRow(reader));
    }

    public async Task<Result<Unit, Error>> SaveAsync(ContextSession session, CancellationToken ct)
    {
        await schema.EnsureAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (id, project_root, atomic_agent_session_id, external_ref, created_at, updated_at, checkpointed_at, stats_json)
            VALUES (@id, @root, @atomicId, @externalRef, @createdAt, @updatedAt, @checkpointedAt, @statsJson)
            ON CONFLICT(id) DO UPDATE SET
                project_root            = @root,
                atomic_agent_session_id = @atomicId,
                external_ref            = @externalRef,
                updated_at              = @updatedAt,
                checkpointed_at         = @checkpointedAt,
                stats_json              = @statsJson
            """;
        cmd.Parameters.AddWithValue("@id", session.Id.ToString());
        cmd.Parameters.AddWithValue("@root", session.ProjectRoot);
        cmd.Parameters.AddWithValue("@atomicId", (object?)session.Binding?.AtomicAgentSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@externalRef", (object?)session.Binding?.ExternalRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", session.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updatedAt", session.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@checkpointedAt", (object?)session.CheckpointedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@statsJson", JsonSerializer.Serialize(session.Stats, StorageJsonContext.Default.SessionStats));
        await cmd.ExecuteNonQueryAsync(ct);
        return Result<Unit, Error>.Ok(Unit.Value);
    }

    public async Task<Result<IReadOnlyList<ContextSession>, Error>> ListForProjectAsync(string projectRoot, CancellationToken ct)
    {
        await schema.EnsureAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_root, atomic_agent_session_id, external_ref, created_at, updated_at, checkpointed_at, stats_json FROM sessions WHERE project_root = @root ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("@root", projectRoot);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var sessions = new List<ContextSession>();
        while (await reader.ReadAsync(ct))
            sessions.Add(MapRow(reader));
        return Result<IReadOnlyList<ContextSession>, Error>.Ok(sessions);
    }

    private SqliteConnection OpenConnection() => new($"Data Source={options.DatabasePath}");

    private static ContextSession MapRow(SqliteDataReader r)
    {
        var atomicId = r.IsDBNull(2) ? null : r.GetString(2);
        var externalRef = r.IsDBNull(3) ? null : r.GetString(3);
        var checkpointedAt = r.IsDBNull(6) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(6));
        var stats = JsonSerializer.Deserialize(r.GetString(7), StorageJsonContext.Default.SessionStats) ?? new SessionStats();
        SessionBinding? binding = (atomicId, externalRef) switch
        {
            (null, null) => null,
            _ => new SessionBinding { AtomicAgentSessionId = atomicId, ExternalRef = externalRef },
        };
        return new ContextSession
        {
            Id = Guid.Parse(r.GetString(0)),
            ProjectRoot = r.GetString(1),
            Binding = binding,
            Stats = stats,
            CreatedAt = DateTimeOffset.Parse(r.GetString(4)),
            UpdatedAt = DateTimeOffset.Parse(r.GetString(5)),
            CheckpointedAt = checkpointedAt,
        };
    }
}
