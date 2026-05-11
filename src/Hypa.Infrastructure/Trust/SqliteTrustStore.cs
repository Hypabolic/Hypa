using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Filters;
using Microsoft.Data.Sqlite;

namespace Hypa.Infrastructure.Trust;

public sealed class SqliteTrustStore(HypaDataOptions options, SqliteSchemaInitializer schema) : ITrustStore
{
    public bool IsTrusted(string projectRoot, string filePath, string fileHash)
    {
        schema.EnsureAsync(CancellationToken.None).GetAwaiter().GetResult();
        using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_hash FROM trust_records WHERE project_root = @root AND filter_file = @file LIMIT 1";
        cmd.Parameters.AddWithValue("@root", projectRoot);
        cmd.Parameters.AddWithValue("@file", filePath);
        var result = cmd.ExecuteScalar();
        return result is string storedHash && storedHash == fileHash;
    }

    public async Task GrantAsync(TrustRecord record, CancellationToken ct)
    {
        await schema.EnsureAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO trust_records (project_root, filter_file, file_hash, granted_at)
            VALUES (@root, @file, @hash, @grantedAt)
            ON CONFLICT(project_root, filter_file) DO UPDATE SET
                file_hash = excluded.file_hash,
                granted_at = excluded.granted_at
            """;
        cmd.Parameters.AddWithValue("@root", record.ProjectRoot);
        cmd.Parameters.AddWithValue("@file", record.FilterFilePath);
        cmd.Parameters.AddWithValue("@hash", record.FileHash);
        cmd.Parameters.AddWithValue("@grantedAt", record.GrantedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<TrustRecord>> GetAllAsync(CancellationToken ct)
    {
        await schema.EnsureAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT project_root, filter_file, file_hash, granted_at FROM trust_records ORDER BY granted_at DESC";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var records = new List<TrustRecord>();
        while (await reader.ReadAsync(ct))
        {
            records.Add(new TrustRecord
            {
                ProjectRoot = reader.GetString(0),
                FilterFilePath = reader.GetString(1),
                FileHash = reader.GetString(2),
                GrantedAt = DateTimeOffset.Parse(reader.GetString(3)),
            });
        }
        return records;
    }
}
