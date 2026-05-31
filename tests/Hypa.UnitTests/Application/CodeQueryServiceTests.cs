using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class CodeQueryServiceTests
{
    [Fact]
    public async Task QueryMarkdownSectionsAsync_DelegatesToRepository()
    {
        var repository = new FakeCodeIndexRepository
        {
            Sections = [MakeSection("notes.md", "Intro", 1, 0)],
        };
        var service = new CodeQueryService(repository);

        var sections = await service.QueryMarkdownSectionsAsync("notes.md", CancellationToken.None);

        Assert.Same(repository.Sections, sections);
        Assert.Equal("notes.md", repository.LastMarkdownSectionsPath);
    }

    [Fact]
    public async Task QueryTocAsync_WhenMaxDepthTwo_FiltersClientSide()
    {
        var repository = new FakeCodeIndexRepository
        {
            Sections =
            [
                MakeSection("notes.md", "One", 1, 0),
                MakeSection("notes.md", "Two", 2, 10),
                MakeSection("notes.md", "Three", 3, 20),
            ],
        };
        var service = new CodeQueryService(repository);

        var sections = await service.QueryTocAsync("notes.md", maxDepth: 2, CancellationToken.None);

        Assert.Equal(["One", "Two"], sections.Select(s => s.HeadingText).ToArray());
    }

    [Fact]
    public async Task QueryTocAsync_WhenMaxDepthOmitted_UsesDepthThree()
    {
        var repository = new FakeCodeIndexRepository
        {
            Sections =
            [
                MakeSection("notes.md", "One", 1, 0),
                MakeSection("notes.md", "Three", 3, 10),
                MakeSection("notes.md", "Four", 4, 20),
            ],
        };
        var service = new CodeQueryService(repository);

        var sections = await service.QueryTocAsync("notes.md");

        Assert.Equal(["One", "Three"], sections.Select(s => s.HeadingText).ToArray());
    }

    [Fact]
    public async Task QueryFrontmatterAsync_WhenDocumentHasFrontmatter_ReturnsRawYaml()
    {
        const string yaml = "title: Test\ntags:\n  - docs";
        var repository = new FakeCodeIndexRepository
        {
            MarkdownDocument = new CodeStructureDocument
            {
                File = MakeFile("notes.md"),
                Provenance = MakeProvenance(),
                FrontmatterYaml = yaml,
            },
        };
        var service = new CodeQueryService(repository);

        var frontmatter = await service.QueryFrontmatterAsync("notes.md", CancellationToken.None);

        Assert.Equal(yaml, frontmatter);
        Assert.Equal("notes.md", repository.LastMarkdownDocumentPath);
    }

    private static MarkdownSection MakeSection(string filePath, string headingText, int headingLevel, int startByte) => new()
    {
        Id = $"sec_{startByte}",
        FilePath = filePath,
        HeadingText = headingText,
        HeadingLevel = headingLevel,
        HeadingPath = headingText,
        HeadingAnchor = headingText.ToLowerInvariant(),
        StartLine = startByte + 1,
        EndLine = startByte + 2,
        StartByte = startByte,
        EndByte = startByte + 5,
        Provenance = MakeProvenance(),
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

    private sealed class FakeCodeIndexRepository : ICodeIndexRepository
    {
        public IReadOnlyList<MarkdownSection> Sections { get; init; } = [];
        public CodeStructureDocument? MarkdownDocument { get; init; }
        public string? LastMarkdownSectionsPath { get; private set; }
        public string? LastMarkdownDocumentPath { get; private set; }
        public Dictionary<string, FileIndexState> FileStates { get; init; } = [];

        public Task SaveDocumentsAsync(IReadOnlyList<CodeStructureDocument> documents, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<CodeSymbol>> QuerySymbolsAsync(CodeSymbolQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CodeSymbol>>([]);

        public Task<CodeGraphResult> QueryGraphAsync(CodeGraphQuery query, CancellationToken ct) =>
            Task.FromResult(new CodeGraphResult());

        public Task<IReadOnlyList<CodeDiagnostic>> QueryDiagnosticsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CodeDiagnostic>>([]);

        public Task<CodeStructureDocument?> QueryMarkdownAsync(string filePath, CancellationToken ct)
        {
            LastMarkdownDocumentPath = filePath;
            return Task.FromResult(MarkdownDocument);
        }

        public Task<IReadOnlyList<MarkdownSection>> QueryMarkdownSectionsAsync(string filePath, CancellationToken ct)
        {
            LastMarkdownSectionsPath = filePath;
            return Task.FromResult(Sections);
        }

        public Task<IReadOnlyList<CodeReference>> QueryReferencesAsync(string filePath, string kind, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CodeReference>>([]);

        public Task SaveProviderHealthAsync(IReadOnlyList<CodeProviderHealth> health, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<CodeProviderHealth>> GetProviderHealthAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CodeProviderHealth>>([]);

        public Task<IReadOnlyDictionary<string, FileIndexState>> QueryFileStatesAsync(
            string projectRoot, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, FileIndexState>>(
                FileStates
                    .Where(kv => kv.Key.StartsWith(projectRoot, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key, kv => kv.Value));

        public Task<FileIndexState?> QueryFileStateAsync(string absolutePath, CancellationToken ct) =>
            Task.FromResult(FileStates.GetValueOrDefault(absolutePath));

        public Task DeleteFileAsync(string absolutePath, CancellationToken ct)
        {
            FileStates.Remove(absolutePath);
            return Task.CompletedTask;
        }
    }
}
