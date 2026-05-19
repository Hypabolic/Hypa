using Hypa.Infrastructure.Storage;
using Hypa.Infrastructure.Trust;
using Hypa.Runtime.Domain.Filters;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Trust;

public sealed class SqliteTrustStoreTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
    private readonly HypaDataOptions _options;
    private readonly SqliteSchemaInitializer _schema;
    private readonly SqliteTrustStore _store;

    public SqliteTrustStoreTests()
    {
        _options = new HypaDataOptions { DataDirectory = _dataDir };
        _schema = new SqliteSchemaInitializer(_options);
        _store = new SqliteTrustStore(_options, _schema);
    }

    public async Task InitializeAsync() => await _schema.InitAsync(CancellationToken.None);

    public async Task DisposeAsync() => await DeleteDataDirectoryAsync(_dataDir);

    [Fact]
    public async Task GrantAsync_Persists()
    {
        var record = new TrustRecord
        {
            ProjectRoot = "/project",
            FilterFilePath = "/project/.hypa/filters/test.json",
            FileHash = "AABBCCDD",
            GrantedAt = DateTimeOffset.UtcNow,
        };
        await _store.GrantAsync(record, CancellationToken.None);
        var all = await _store.GetAllAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal("/project", all[0].ProjectRoot);
    }

    [Fact]
    public async Task IsTrusted_MatchesHashAndPath()
    {
        var record = new TrustRecord
        {
            ProjectRoot = "/project",
            FilterFilePath = "/project/.hypa/filters/test.json",
            FileHash = "AABBCCDD",
            GrantedAt = DateTimeOffset.UtcNow,
        };
        await _store.GrantAsync(record, CancellationToken.None);
        Assert.True(_store.IsTrusted("/project", "/project/.hypa/filters/test.json", "AABBCCDD"));
    }

    [Fact]
    public async Task IsTrusted_ReturnsFalse_WhenHashDiffers()
    {
        var record = new TrustRecord
        {
            ProjectRoot = "/project",
            FilterFilePath = "/project/.hypa/filters/test.json",
            FileHash = "AABBCCDD",
            GrantedAt = DateTimeOffset.UtcNow,
        };
        await _store.GrantAsync(record, CancellationToken.None);
        Assert.False(_store.IsTrusted("/project", "/project/.hypa/filters/test.json", "DEADBEEF"));
    }

    [Fact]
    public async Task IsTrusted_ReturnsFalse_WhenNotGranted() =>
        Assert.False(_store.IsTrusted("/project", "/project/.hypa/filters/missing.json", "HASH"));

    [Fact]
    public async Task IsTrusted_InitializesSchema_WhenDatabaseIsFresh()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var store = new SqliteTrustStore(options, new SqliteSchemaInitializer(options));

            Assert.False(store.IsTrusted("/project", "/project/.hypa/filters/missing.json", "HASH"));
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAll()
    {
        await _store.GrantAsync(new TrustRecord { ProjectRoot = "/a", FilterFilePath = "/a/f.json", FileHash = "H1", GrantedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
        await _store.GrantAsync(new TrustRecord { ProjectRoot = "/b", FilterFilePath = "/b/f.json", FileHash = "H2", GrantedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
        var all = await _store.GetAllAsync(CancellationToken.None);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GrantAsync_Idempotent_DoesNotDuplicate()
    {
        var record = new TrustRecord
        {
            ProjectRoot = "/project",
            FilterFilePath = "/project/.hypa/filters/test.json",
            FileHash = "AABBCCDD",
            GrantedAt = DateTimeOffset.UtcNow,
        };
        await _store.GrantAsync(record, CancellationToken.None);
        await _store.GrantAsync(record, CancellationToken.None);
        var all = await _store.GetAllAsync(CancellationToken.None);
        Assert.Single(all);
    }

    [Fact]
    public async Task GrantAsync_UpdatesHash_WhenFilterAlreadyTrusted()
    {
        await _store.GrantAsync(new TrustRecord
        {
            ProjectRoot = "/project",
            FilterFilePath = "/project/.hypa/filters/test.json",
            FileHash = "OLDHASH",
            GrantedAt = DateTimeOffset.UtcNow.AddHours(-1),
        }, CancellationToken.None);

        var refreshedAt = DateTimeOffset.UtcNow;
        await _store.GrantAsync(new TrustRecord
        {
            ProjectRoot = "/project",
            FilterFilePath = "/project/.hypa/filters/test.json",
            FileHash = "NEWHASH",
            GrantedAt = refreshedAt,
        }, CancellationToken.None);

        var all = await _store.GetAllAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal("NEWHASH", all[0].FileHash);
        Assert.Equal(refreshedAt.ToString("O"), all[0].GrantedAt.ToString("O"));
        Assert.False(_store.IsTrusted("/project", "/project/.hypa/filters/test.json", "OLDHASH"));
        Assert.True(_store.IsTrusted("/project", "/project/.hypa/filters/test.json", "NEWHASH"));
    }

    [Fact]
    public async Task TrustStore_IsTrusted_WhenInitFails_ReturnsFalse()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-file-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(dataDir, "not a directory");
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var store = new SqliteTrustStore(options, new SqliteSchemaInitializer(options));

            Assert.False(store.IsTrusted("/project", "/project/.hypa/filters/test.json", "HASH"));
        }
        finally
        {
            if (File.Exists(dataDir))
                File.Delete(dataDir);
        }
    }

    [Fact]
    public async Task TrustStore_GrantAsync_WhenDatabaseReadonly_DoesNotThrow()
    {
        SqliteConnection.ClearAllPools();
        File.SetAttributes(_options.DatabasePath, FileAttributes.ReadOnly);
        var store = new SqliteTrustStore(_options, new SqliteSchemaInitializer(_options));

        var ex = await Record.ExceptionAsync(() => store.GrantAsync(new TrustRecord
        {
            ProjectRoot = "/project",
            FilterFilePath = "/project/.hypa/filters/test.json",
            FileHash = "HASH",
            GrantedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None));

        Assert.Null(ex);
    }

    private static async Task DeleteDataDirectoryAsync(string dataDir)
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(dataDir))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(dataDir, "*", SearchOption.AllDirectories))
            File.SetAttributes(entry, FileAttributes.Normal);

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(dataDir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(50 * attempt);
            }
        }
    }
}
