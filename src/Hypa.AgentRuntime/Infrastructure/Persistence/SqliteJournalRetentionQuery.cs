using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Microsoft.Data.Sqlite;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Reads export acks and active barriers from the session database.
/// A handoff lifecycle with no prepared checkpoint holds every segment.
/// </summary>
public sealed class SqliteJournalRetentionQuery : IJournalRetentionQuery
{
    private readonly RuntimeStatePaths _paths;

    public SqliteJournalRetentionQuery(RuntimeStatePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<RuntimeResult<JournalRetentionHold>> ReadHoldAsync(
        string sessionId, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_paths.DatabasePath))
                return RuntimeResult<JournalRetentionHold>.Ok(default);

            await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
            await conn.OpenAsync(ct).ConfigureAwait(false);

            long? ack = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT MIN(last_acked_seq) FROM event_exports WHERE session_id = @sid;
                    """;
                cmd.Parameters.AddWithValue("@sid", sessionId);
                var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (obj is not null and not DBNull)
                    ack = Convert.ToInt64(obj);
            }

            long? barrier = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT MIN(barrier_seq) FROM checkpoints
                    WHERE session_id = @sid AND state = @prepared;
                    """;
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@prepared", CheckpointStates.Prepared);
                var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (obj is not null and not DBNull)
                    barrier = Convert.ToInt64(obj);
            }

            if (barrier is null)
            {
                await using var life = conn.CreateCommand();
                life.CommandText = """
                    SELECT lifecycle_state FROM sessions WHERE session_id = @sid;
                    """;
                life.Parameters.AddWithValue("@sid", sessionId);
                var state = (string?)await life.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (state is SessionLifecycle.HandoffPending
                    or SessionLifecycle.HandoffInProgress
                    or SessionLifecycle.HandoffPrepared)
                {
                    barrier = 0;
                }
            }

            return RuntimeResult<JournalRetentionHold>.Ok(new JournalRetentionHold(ack, barrier));
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            return RuntimeResult<JournalRetentionHold>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
    }
}
