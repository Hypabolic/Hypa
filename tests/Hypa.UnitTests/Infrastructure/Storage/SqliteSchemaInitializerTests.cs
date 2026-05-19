using Hypa.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Storage;

public sealed class SqliteSchemaInitializerTests
{
    [Fact]
    public async Task SchemaInitializer_WhenCompatibleExistingDb_ReadOnly_ReturnsOkWithoutDdl()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var firstSchema = new SqliteSchemaInitializer(options);
            var firstResult = await firstSchema.InitAsync(CancellationToken.None);
            Assert.True(firstResult.IsOk);

            SqliteConnection.ClearAllPools();
            File.SetAttributes(options.DatabasePath, FileAttributes.ReadOnly);

            var secondSchema = new SqliteSchemaInitializer(options);
            var result = await secondSchema.InitAsync(CancellationToken.None);

            Assert.True(result.IsOk);
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task SchemaInitializer_WhenMissingRequiredColumn_ReturnsFailIfCannotMigrate()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dataDir);
            var options = new HypaDataOptions { DataDirectory = dataDir };

            await using (var conn = new SqliteConnection($"Data Source={options.DatabasePath}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE sessions (
                        id           TEXT PRIMARY KEY,
                        project_root TEXT NOT NULL,
                        created_at   TEXT NOT NULL,
                        updated_at   TEXT NOT NULL,
                        stats_json   TEXT NOT NULL
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            SqliteConnection.ClearAllPools();
            File.SetAttributes(options.DatabasePath, FileAttributes.ReadOnly);

            var schema = new SqliteSchemaInitializer(options);
            var result = await schema.InitAsync(CancellationToken.None);

            Assert.False(result.IsOk);
            Assert.Contains(result.Error.Code, (string[])["schema.db_error", "schema.access_denied"]);
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task SchemaInitializer_WhenFreshWritableDb_RunsMigrationsAndSetsVersion()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var schema = new SqliteSchemaInitializer(options);

            var result = await schema.InitAsync(CancellationToken.None);

            Assert.True(result.IsOk);

            await using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM schema_metadata WHERE key = 'schema_version'";
            var value = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("1", value);
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task SchemaInitializer_WhenFutureSchemaVersion_ReturnsFail()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var first = new SqliteSchemaInitializer(options);
            await first.InitAsync(CancellationToken.None);

            SqliteConnection.ClearAllPools();
            await using (var conn = new SqliteConnection($"Data Source={options.DatabasePath}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO schema_metadata (key, value) VALUES ('schema_version', '99')
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            var second = new SqliteSchemaInitializer(options);
            var result = await second.InitAsync(CancellationToken.None);

            Assert.False(result.IsOk);
            Assert.Equal("schema.future_version", result.Error.Code);

            // Verify the version was not downgraded.
            await using var verify = new SqliteConnection($"Data Source={options.DatabasePath}");
            await verify.OpenAsync();
            await using var check = verify.CreateCommand();
            check.CommandText = "SELECT value FROM schema_metadata WHERE key = 'schema_version'";
            Assert.Equal("99", (string?)await check.ExecuteScalarAsync());
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task SchemaInitializer_WhenReadOnlyDbWithFutureVersion_ReturnsFail()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var first = new SqliteSchemaInitializer(options);
            await first.InitAsync(CancellationToken.None);

            SqliteConnection.ClearAllPools();
            await using (var conn = new SqliteConnection($"Data Source={options.DatabasePath}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO schema_metadata (key, value) VALUES ('schema_version', '99')
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            // Simulate Codex sandbox: DB is structurally compatible but read-only.
            File.SetAttributes(options.DatabasePath, FileAttributes.ReadOnly);

            var second = new SqliteSchemaInitializer(options);
            var result = await second.InitAsync(CancellationToken.None);

            Assert.False(result.IsOk);
            Assert.Equal("schema.future_version", result.Error.Code);
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task SchemaInitializer_WhenCompatibleWritableDb_StampsSchemaVersion()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };

            // Provision with first initializer, then drop schema_metadata to simulate
            // a pre-Step-3 DB that has all functional tables but no version record.
            var first = new SqliteSchemaInitializer(options);
            await first.InitAsync(CancellationToken.None);

            SqliteConnection.ClearAllPools();
            await using (var conn = new SqliteConnection($"Data Source={options.DatabasePath}"))
            {
                await conn.OpenAsync();
                await using var drop = conn.CreateCommand();
                drop.CommandText = "DROP TABLE schema_metadata";
                await drop.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            // A new initializer sees a compatible DB without version metadata.
            var second = new SqliteSchemaInitializer(options);
            var result = await second.InitAsync(CancellationToken.None);

            Assert.True(result.IsOk);

            await using var verify = new SqliteConnection($"Data Source={options.DatabasePath}");
            await verify.OpenAsync();
            await using var cmd = verify.CreateCommand();
            cmd.CommandText = "SELECT value FROM schema_metadata WHERE key = 'schema_version'";
            Assert.Equal("1", (string?)await cmd.ExecuteScalarAsync());
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task SchemaInitializer_WhenInitFails_CachesDegradedResult()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dataDir);
            var options = new HypaDataOptions { DataDirectory = dataDir };
            await File.WriteAllBytesAsync(options.DatabasePath, [0x00, 0x01, 0x02, 0xFF, 0xFE]);

            var schema = new SqliteSchemaInitializer(options);

            var first = await schema.InitAsync(CancellationToken.None);
            var second = await schema.InitAsync(CancellationToken.None);

            Assert.False(first.IsOk);
            Assert.False(second.IsOk);
            Assert.Equal(first.Error.Code, second.Error.Code);
            Assert.Equal(first.Error.Message, second.Error.Message);
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    private static async Task DeleteDataDirectoryAsync(string dataDir)
    {
        if (Directory.Exists(dataDir))
            foreach (var f in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);

        SqliteConnection.ClearAllPools();

        if (!Directory.Exists(dataDir)) return;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try { Directory.Delete(dataDir, recursive: true); return; }
            catch (IOException) when (attempt < 5) { await Task.Delay(50 * attempt); }
            catch (UnauthorizedAccessException) when (attempt < 5) { await Task.Delay(50 * attempt); }
        }
    }
}
