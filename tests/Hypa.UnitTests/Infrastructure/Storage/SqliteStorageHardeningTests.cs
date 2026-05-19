using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Domain.Metrics;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Storage;

public sealed class SqliteStorageHardeningTests
{
    [Fact]
    public async Task SaveAsync_WhenDatabaseReadonly_ReturnsFail()
    {
        var dataDir = TempDir();
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var schema = new SqliteSchemaInitializer(options);
            await schema.InitAsync(CancellationToken.None);

            SqliteConnection.ClearAllPools();
            File.SetAttributes(options.DatabasePath, FileAttributes.ReadOnly);

            var readOnlySchema = new SqliteSchemaInitializer(options);
            var repo = new SqliteSessionRepository(options, readOnlySchema);

            var session = new ContextSession { ProjectRoot = "/tmp/test" };
            var result = await repo.SaveAsync(session, CancellationToken.None);

            Assert.False(result.IsOk);
        }
        finally
        {
            await CleanupAsync(dataDir);
        }
    }

    [Fact]
    public async Task RecordCommandMetrics_WhenDatabaseReadonly_DoesNotThrow()
    {
        var dataDir = TempDir();
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var schema = new SqliteSchemaInitializer(options);
            await schema.InitAsync(CancellationToken.None);

            SqliteConnection.ClearAllPools();
            File.SetAttributes(options.DatabasePath, FileAttributes.ReadOnly);

            var readOnlySchema = new SqliteSchemaInitializer(options);
            var ledger = new SqliteEvidenceLedger(options, readOnlySchema, NullLogger<SqliteEvidenceLedger>.Instance);

            var record = new CommandMetricsRecord
            {
                SessionId = Guid.NewGuid(),
                Command = "git status",
                ExitCode = 0,
                DurationMs = 10,
                OriginalTokens = 100,
                CompressedTokens = 50,
                ReducerId = "test",
            };

            var ex = await Record.ExceptionAsync(() =>
                ledger.RecordCommandMetricsAsync(record, CancellationToken.None));

            Assert.Null(ex);
        }
        finally
        {
            await CleanupAsync(dataDir);
        }
    }

    [Fact]
    public async Task RecordToolCall_WhenDatabaseReadonly_DoesNotThrow()
    {
        var dataDir = TempDir();
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var schema = new SqliteSchemaInitializer(options);
            await schema.InitAsync(CancellationToken.None);

            SqliteConnection.ClearAllPools();
            File.SetAttributes(options.DatabasePath, FileAttributes.ReadOnly);

            var readOnlySchema = new SqliteSchemaInitializer(options);
            var ledger = new SqliteEvidenceLedger(options, readOnlySchema, NullLogger<SqliteEvidenceLedger>.Instance);

            var record = new ToolCallRecord
            {
                SessionId = Guid.NewGuid(),
                ToolName = "Bash",
                Args = "git status",
            };

            var ex = await Record.ExceptionAsync(() =>
                ledger.RecordToolCallAsync(record, CancellationToken.None));

            Assert.Null(ex);
        }
        finally
        {
            await CleanupAsync(dataDir);
        }
    }

    [Fact]
    public async Task RecordParseMetrics_WhenDatabaseReadonly_DoesNotThrow()
    {
        var dataDir = TempDir();
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var schema = new SqliteSchemaInitializer(options);
            await schema.InitAsync(CancellationToken.None);

            SqliteConnection.ClearAllPools();
            File.SetAttributes(options.DatabasePath, FileAttributes.ReadOnly);

            var readOnlySchema = new SqliteSchemaInitializer(options);
            var repo = new SqliteParseMetricsRepository(options, readOnlySchema, NullLogger<SqliteParseMetricsRepository>.Instance);

            var record = new ParseMetricsRecord
            {
                RunId = Guid.NewGuid().ToString(),
                Executable = "git",
                Arguments = "status",
                ParseTier = ParseTier.Passthrough,
                RecordedAt = DateTimeOffset.UtcNow,
            };

            var ex = await Record.ExceptionAsync(() =>
                repo.RecordAsync(record, CancellationToken.None));

            Assert.Null(ex);
        }
        finally
        {
            await CleanupAsync(dataDir);
        }
    }

    [Fact]
    public async Task StoreArtifact_WhenArtifactsDirectoryReadonly_ReturnsFail()
    {
        var dataDir = TempDir();
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var schema = new SqliteSchemaInitializer(options);
            await schema.InitAsync(CancellationToken.None);

            Directory.CreateDirectory(options.ArtifactsDirectory);
            File.SetAttributes(options.ArtifactsDirectory, FileAttributes.ReadOnly);

            var repo = new SqliteArtifactRepository(options, schema);
            var result = await repo.StoreAsync("hello world", "text/plain", Guid.NewGuid(), CancellationToken.None);

            Assert.False(result.IsOk);
        }
        finally
        {
            await CleanupAsync(dataDir);
        }
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");

    private static async Task CleanupAsync(string dataDir)
    {
        if (!Directory.Exists(dataDir)) return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(dataDir, "*", SearchOption.AllDirectories))
            File.SetAttributes(entry, FileAttributes.Normal);

        SqliteConnection.ClearAllPools();

        for (var i = 1; i <= 5; i++)
        {
            try { Directory.Delete(dataDir, recursive: true); return; }
            catch (IOException) when (i < 5) { await Task.Delay(50 * i); }
            catch (UnauthorizedAccessException) when (i < 5) { await Task.Delay(50 * i); }
        }
    }
}
