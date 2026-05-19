using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Metrics;
using Hypa.Runtime.Domain.Parsers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Storage;

public sealed class SqliteParseMetricsRepository(
    HypaDataOptions options,
    SqliteSchemaInitializer schema,
    ILogger<SqliteParseMetricsRepository> logger) : IParseMetricsRepository
{
    public async Task RecordAsync(ParseMetricsRecord record, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return;
            await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO parse_metrics (run_id, executable, arguments, parse_tier, filter_id, recorded_at)
                VALUES (@runId, @executable, @arguments, @parseTier, @filterId, @recordedAt)
                """;
            cmd.Parameters.AddWithValue("@runId", record.RunId);
            cmd.Parameters.AddWithValue("@executable", record.Executable);
            cmd.Parameters.AddWithValue("@arguments", record.Arguments);
            cmd.Parameters.AddWithValue("@parseTier", record.ParseTier.ToString());
            cmd.Parameters.AddWithValue("@filterId", (object?)record.FilterId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@recordedAt", record.RecordedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            logger.LogDebug(ex, "Failed to record parse metrics");
        }
    }

    public async Task<IReadOnlyList<ParseMetricsRecord>> QueryAsync(int limit, CancellationToken ct)
    {
        try
        {
            var init = await schema.InitAsync(ct);
            if (!init.IsOk) return [];

            await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT run_id, executable, arguments, parse_tier, filter_id, recorded_at FROM parse_metrics ORDER BY recorded_at DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var records = new List<ParseMetricsRecord>();
            while (await reader.ReadAsync(ct))
            {
                records.Add(new ParseMetricsRecord
                {
                    RunId = reader.GetString(0),
                    Executable = reader.GetString(1),
                    Arguments = reader.GetString(2),
                    ParseTier = Enum.Parse<ParseTier>(reader.GetString(3)),
                    FilterId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    RecordedAt = DateTimeOffset.Parse(reader.GetString(5)),
                });
            }
            return records;
        }
        catch (Exception ex) when (StorageFailure.IsExpected(ex))
        {
            logger.LogDebug(ex, "Failed to query parse metrics");
            return [];
        }
    }
}
