using Microsoft.Data.Sqlite;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Releases only the test's SQLite pool. <see cref="SqliteConnection.ClearAllPools"/>
/// is process-wide and races fire-and-forget PersistGraph from other tests — that
/// native fault aborted the aggregate Checkpoint testhost (ResumeThread).
/// </summary>
internal static class SqliteTestCleanup
{
    public static void ReleaseDatabase(string? databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            return;

        try
        {
            using var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString);
            SqliteConnection.ClearPool(conn);
        }
        catch
        {
            // best-effort
        }
    }

    public static void ReleaseAndDelete(string directory, string? databasePath)
    {
        ReleaseDatabase(databasePath);
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }
}
