using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Projects;
using Microsoft.Data.Sqlite;

namespace Hypa.Infrastructure.Storage;

public sealed class SqliteProjectRegistry(HypaDataOptions options, SqliteSchemaInitializer schema) : IProjectRegistry
{
    public async Task<Result<Unit, Error>> RegisterAsync(string rootPath, string agentKey, CancellationToken ct = default)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return Result<Unit, Error>.Fail(StorageFailure.ToError(init.Error));

            await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO project_registrations (root_path, agent_key, installed_at)
                VALUES (@root, @agent, @installedAt)
                ON CONFLICT(root_path, agent_key) DO UPDATE SET installed_at = @installedAt
                """;
            cmd.Parameters.AddWithValue("@root", rootPath);
            cmd.Parameters.AddWithValue("@agent", agentKey);
            cmd.Parameters.AddWithValue("@installedAt", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
            return Result<Unit, Error>.Ok(Unit.Value);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            return Result<Unit, Error>.Fail(StorageFailure.ToError(ex));
        }
    }

    public async Task<Result<Unit, Error>> UnregisterAsync(string rootPath, string agentKey, CancellationToken ct = default)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return Result<Unit, Error>.Fail(StorageFailure.ToError(init.Error));

            await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM project_registrations WHERE root_path = @root AND agent_key = @agent";
            cmd.Parameters.AddWithValue("@root", rootPath);
            cmd.Parameters.AddWithValue("@agent", agentKey);
            await cmd.ExecuteNonQueryAsync(ct);
            return Result<Unit, Error>.Ok(Unit.Value);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            return Result<Unit, Error>.Fail(StorageFailure.ToError(ex));
        }
    }

    public async Task<IReadOnlyList<ProjectRegistration>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return [];

            await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT root_path, agent_key, installed_at FROM project_registrations ORDER BY installed_at";
            return await ReadRegistrationsAsync(cmd, ct);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<ProjectRegistration>> GetByAgentAsync(string agentKey, CancellationToken ct = default)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return [];

            await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT root_path, agent_key, installed_at FROM project_registrations WHERE agent_key = @agent ORDER BY installed_at";
            cmd.Parameters.AddWithValue("@agent", agentKey);
            return await ReadRegistrationsAsync(cmd, ct);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<ProjectRegistration>> ReadRegistrationsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<ProjectRegistration>();
        while (await reader.ReadAsync(ct))
            results.Add(new ProjectRegistration(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2))));
        return results;
    }
}
