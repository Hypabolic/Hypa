using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Storage;

public sealed class CodeQueryServiceSqliteTests
{
    [Fact]
    public async Task QueryMarkdownSectionsAsync_WhenSectionsExist_ReturnsSectionsForFile()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var (repository, service) = CreateStore(dataDir);
            var provenance = MakeProvenance();
            var file = MakeFile("notes.md");
            await repository.SaveDocumentsAsync(
                [
                    new CodeStructureDocument
                    {
                        File = file,
                        Provenance = provenance,
                        Sections =
                        [
                            MakeSection(file.RelativePath, "sec_2", "Second", 1, 20, provenance),
                            MakeSection(file.RelativePath, "sec_1", "First", 1, 0, provenance),
                        ],
                    },
                ],
                CancellationToken.None);

            var sections = await service.QueryMarkdownSectionsAsync(file.RelativePath, CancellationToken.None);

            Assert.Equal(2, sections.Count);
            Assert.Equal(["First", "Second"], sections.Select(s => s.HeadingText).ToArray());
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task QueryTocAsync_WhenMaxDepthTwo_ExcludesDeepHeadings()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var (repository, service) = CreateStore(dataDir);
            var provenance = MakeProvenance();
            var file = MakeFile("toc.md");
            await repository.SaveDocumentsAsync(
                [
                    new CodeStructureDocument
                    {
                        File = file,
                        Provenance = provenance,
                        Sections =
                        [
                            MakeSection(file.RelativePath, "sec_1", "One", 1, 0, provenance),
                            MakeSection(file.RelativePath, "sec_2", "Two", 2, 10, provenance),
                            MakeSection(file.RelativePath, "sec_3", "Three", 3, 20, provenance),
                        ],
                    },
                ],
                CancellationToken.None);

            var sections = await service.QueryTocAsync(file.RelativePath, maxDepth: 2, CancellationToken.None);

            Assert.Equal(2, sections.Count);
            Assert.Equal([1, 2], sections.Select(s => s.HeadingLevel).ToArray());
            Assert.Equal(["One", "Two"], sections.Select(s => s.HeadingText).ToArray());
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task QueryFrontmatterAsync_WhenFrontmatterYamlExists_ReturnsRawYaml()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var (repository, service) = CreateStore(dataDir);
            var provenance = MakeProvenance();
            var file = MakeFile("frontmatter.md");
            const string yaml = "title: Test\nauthor: Ada\ntags:\n  - docs";
            await repository.SaveDocumentsAsync(
                [
                    new CodeStructureDocument
                    {
                        File = file,
                        Provenance = provenance,
                        FrontmatterYaml = yaml,
                        PlainText = "Heading\nBody",
                        References =
                        [
                            MakeReference(file.RelativePath, "ref_1", "title", 0, provenance),
                            MakeReference(file.RelativePath, "ref_2", "author", 10, provenance),
                        ],
                    },
                ],
                CancellationToken.None);

            var frontmatter = await service.QueryFrontmatterAsync(file.RelativePath, CancellationToken.None);
            var document = await service.QueryMarkdownAsync(file.RelativePath, CancellationToken.None);

            Assert.Equal(yaml, frontmatter);
            Assert.NotNull(document);
            Assert.Equal(yaml, document.FrontmatterYaml);
            Assert.Equal("Heading\nBody", document.PlainText);
            Assert.Equal(["title", "author"], document.References.Select(r => r.Target).ToArray());
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task QueryMarkdownSectionsAsync_WhenNoSections_ReturnsEmpty()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var (repository, service) = CreateStore(dataDir);
            var provenance = MakeProvenance();
            var file = MakeFile("empty.md");
            await repository.SaveDocumentsAsync(
                [
                    new CodeStructureDocument
                    {
                        File = file,
                        Provenance = provenance,
                        Sections = [],
                    },
                ],
                CancellationToken.None);

            var sections = await service.QueryMarkdownSectionsAsync(file.RelativePath, CancellationToken.None);

            Assert.NotNull(sections);
            Assert.Empty(sections);
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    [Fact]
    public async Task QueryMarkdownSectionsAsync_WhenDuplicateHeadingPathsExist_PreservesAllSections()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        try
        {
            var (repository, service) = CreateStore(dataDir);
            var provenance = MakeProvenance();
            var file = MakeFile("duplicates.md");
            await repository.SaveDocumentsAsync(
                [
                    new CodeStructureDocument
                    {
                        File = file,
                        Provenance = provenance,
                        Sections =
                        [
                            MakeSection(file.RelativePath, "sec_1", "Overview", 2, 0, provenance),
                            MakeSection(file.RelativePath, "sec_2", "Overview", 2, 40, provenance),
                        ],
                    },
                ],
                CancellationToken.None);

            var sections = await service.QueryMarkdownSectionsAsync(file.RelativePath, CancellationToken.None);

            Assert.Equal(2, sections.Count);
            Assert.Equal([0, 40], sections.Select(s => s.StartByte).ToArray());
        }
        finally
        {
            await DeleteDataDirectoryAsync(dataDir);
        }
    }

    private static (SqliteCodeIndexRepository Repository, CodeQueryService Service) CreateStore(string dataDir)
    {
        var options = new HypaDataOptions { DataDirectory = dataDir };
        var schema = new SqliteSchemaInitializer(options);
        var repository = new SqliteCodeIndexRepository(options, schema);
        return (repository, new CodeQueryService(repository));
    }

    private static MarkdownSection MakeSection(
        string filePath,
        string id,
        string headingText,
        int headingLevel,
        int startByte,
        ProviderProvenance provenance) => new()
        {
            Id = id,
            FilePath = filePath,
            HeadingText = headingText,
            HeadingLevel = headingLevel,
            HeadingPath = headingText,
            HeadingAnchor = headingText.ToLowerInvariant(),
            StartLine = startByte + 1,
            EndLine = startByte + 2,
            StartByte = startByte,
            EndByte = startByte + 5,
            Text = headingText,
            PlainText = headingText,
            Provenance = provenance,
        };

    private static CodeReference MakeReference(
        string filePath,
        string id,
        string target,
        int startByte,
        ProviderProvenance provenance) => new()
        {
            Id = id,
            FilePath = filePath,
            Kind = "frontmatter",
            Target = target,
            Span = new SourceSpan
            {
                StartLine = 1,
                StartColumn = startByte + 1,
                EndLine = 1,
                EndColumn = startByte + target.Length + 1,
                StartByte = startByte,
                EndByte = startByte + target.Length,
            },
            Provenance = provenance,
        };

    private static CodeFileIdentity MakeFile(string relativePath) => new()
    {
        ProjectRoot = "/project",
        Path = $"/project/{relativePath}",
        RelativePath = relativePath,
        Language = "markdown",
        ContentHash = "hash",
        SizeBytes = 0,
    };

    private static ProviderProvenance MakeProvenance() => new()
    {
        ProviderId = "markdown",
        ProviderVersion = "1",
        QueryVersion = "1",
        FactKind = "syntactic",
        Confidence = 1,
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
