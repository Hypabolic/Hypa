using Hypa.Infrastructure.Storage;
using Hypa.Sdk.CodeIntelligence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Storage;

public sealed class SqliteCodeIndexRepositoryFreshnessTests
{
    [Fact]
    public async Task QueryFileStateAsync_WhenFileNotIndexed_ReturnsNull()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var (repo, _) = CreateStore(dataDir);

            var state = await repo.QueryFileStateAsync("/project/missing.md", CancellationToken.None);

            Assert.Null(state);
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task QueryFileStateAsync_WhenFileIndexed_ReturnsMtimeAndSize()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var (repo, _) = CreateStore(dataDir);
            await repo.SaveDocumentsAsync([MakeDocument("notes.md", mtimeMs: 12345L, sizeBytes: 999L)], CancellationToken.None);

            var state = await repo.QueryFileStateAsync("/project/notes.md", CancellationToken.None);

            Assert.NotNull(state);
            Assert.Equal("/project/notes.md", state.AbsolutePath);
            Assert.Equal(12345L, state.MTimeMs);
            Assert.Equal(999L, state.SizeBytes);
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task QueryFileStateAsync_WhenFileIndexedWithGitOid_ReturnsOid()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var (repo, _) = CreateStore(dataDir);
            await repo.SaveDocumentsAsync(
                [MakeDocument("notes.md", gitBlobOid: "abc123def456", mtimeMs: 0L, sizeBytes: 100L)],
                CancellationToken.None);

            var state = await repo.QueryFileStateAsync("/project/notes.md", CancellationToken.None);

            Assert.NotNull(state);
            Assert.Equal("abc123def456", state.GitBlobOid);
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task QueryFileStatesAsync_ReturnsAllFilesUnderRoot()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var (repo, _) = CreateStore(dataDir);
            await repo.SaveDocumentsAsync(
                [
                    MakeDocument("a.md", projectRoot: "/project", mtimeMs: 1L, sizeBytes: 10L),
                    MakeDocument("b.md", projectRoot: "/project", mtimeMs: 2L, sizeBytes: 20L),
                    MakeDocument("c.md", projectRoot: "/other", mtimeMs: 3L, sizeBytes: 30L),
                ],
                CancellationToken.None);

            var states = await repo.QueryFileStatesAsync("/project", CancellationToken.None);

            Assert.Equal(2, states.Count);
            Assert.True(states.ContainsKey("/project/a.md"));
            Assert.True(states.ContainsKey("/project/b.md"));
            Assert.False(states.ContainsKey("/other/c.md"));
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task DeleteFileAsync_RemovesFileAndDerivedFacts()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var (repo, _) = CreateStore(dataDir);
            var provenance = MakeProvenance();
            var file = MakeFileIdentity("notes.md");
            var section = new MarkdownSection
            {
                Id = "sec_1",
                FilePath = file.RelativePath,
                HeadingText = "Intro",
                HeadingLevel = 1,
                HeadingPath = "Intro",
                HeadingAnchor = "intro",
                StartLine = 1,
                EndLine = 5,
                StartByte = 0,
                EndByte = 50,
                Provenance = provenance,
            };
            await repo.SaveDocumentsAsync(
                [new CodeStructureDocument { File = file, Provenance = provenance, Sections = [section] }],
                CancellationToken.None);

            await repo.DeleteFileAsync(file.Path, CancellationToken.None);

            await using var conn = new SqliteConnection(
                $"Data Source={Path.Combine(dataDir, "hypa.db")}");
            await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM code_files WHERE absolute_path = @p";
                cmd.Parameters.AddWithValue("@p", file.Path);
                Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM markdown_sections WHERE file_path = @p";
                cmd.Parameters.AddWithValue("@p", file.RelativePath);
                Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    private static (SqliteCodeIndexRepository repo, object _) CreateStore(string dataDir)
    {
        var options = new HypaDataOptions { DataDirectory = dataDir };
        var schema = new SqliteSchemaInitializer(options);
        return (new SqliteCodeIndexRepository(options, schema), new object());
    }

    private static CodeStructureDocument MakeDocument(
        string relativePath,
        string projectRoot = "/project",
        string? gitBlobOid = null,
        long mtimeMs = 0L,
        long sizeBytes = 0L) =>
        new()
        {
            File = new CodeFileIdentity
            {
                ProjectRoot = projectRoot,
                Path = $"{projectRoot}/{relativePath}",
                RelativePath = relativePath,
                Language = "markdown",
                ContentHash = "hash",
                SizeBytes = sizeBytes,
                GitBlobOid = gitBlobOid,
                MTimeMs = mtimeMs,
            },
            Provenance = MakeProvenance(),
        };

    private static CodeFileIdentity MakeFileIdentity(string relativePath, string projectRoot = "/project") => new()
    {
        ProjectRoot = projectRoot,
        Path = $"{projectRoot}/{relativePath}",
        RelativePath = relativePath,
        Language = "markdown",
        ContentHash = "hash",
        SizeBytes = 100L,
    };

    private static ProviderProvenance MakeProvenance() => new()
    {
        ProviderId = "markdown",
        ProviderVersion = "1",
        QueryVersion = "1",
        FactKind = "syntactic",
        Confidence = 1.0,
    };

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
