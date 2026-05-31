using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class CodeIndexServiceTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"hypa-code-db-{Guid.NewGuid():N}");
    private readonly string _projectDir = Path.Combine(Path.GetTempPath(), $"hypa-code-project-{Guid.NewGuid():N}");
    private readonly HypaDataOptions _options;
    private readonly SqliteSchemaInitializer _schema;
    private readonly SqliteCodeIndexRepository _repository;

    public CodeIndexServiceTests()
    {
        _options = new HypaDataOptions { DataDirectory = _dataDir };
        _schema = new SqliteSchemaInitializer(_options);
        _repository = new SqliteCodeIndexRepository(_options, _schema);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_projectDir);
        await _schema.InitAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await DeleteDirectoryAsync(_dataDir);
        await DeleteDirectoryAsync(_projectDir);
    }

    [Fact]
    public async Task IndexAsync_PersistsSymbolsReferencesAndImportEdges()
    {
        var source = Path.Combine(_projectDir, "Sample.cs");
        await File.WriteAllTextAsync(source, """
            using System;

            namespace Demo;

            public class Sample
            {
                public void Run()
                {
                }
            }
            """);

        var service = MakeService();
        var result = await service.IndexAsync(_projectDir, CancellationToken.None);
        var symbols = await _repository.QuerySymbolsAsync(new CodeSymbolQuery { Path = "Sample.cs" }, CancellationToken.None);
        var graph = await _repository.QueryGraphAsync(new CodeGraphQuery { Path = "Sample.cs" }, CancellationToken.None);

        Assert.Equal(1, result.FilesIndexed);
        Assert.Contains(symbols, s => s.Name == "Sample" && s.Kind == "class");
        Assert.Contains(symbols, s => s.Name == "Run" && s.Kind == "method");
        Assert.Contains(graph.Edges, e => e.Kind == "imports" && e.TargetId == "System");
    }

    [Fact]
    public async Task QueryGraphAsync_FiltersSyntacticGraphFacts()
    {
        var source = Path.Combine(_projectDir, "Graph.cs");
        await File.WriteAllTextAsync(source, """
            public class Graph
            {
                public void Run()
                {
                    Helper();
                }

                private void Helper()
                {
                }
            }
            """);

        await MakeService().IndexAsync(_projectDir, CancellationToken.None);
        var run = (await _repository.QuerySymbolsAsync(new CodeSymbolQuery { Query = "Run" }, CancellationToken.None)).Single();
        var calls = await _repository.QueryGraphAsync(new CodeGraphQuery { EdgeKind = "calls" }, CancellationToken.None);
        var callees = await _repository.QueryGraphAsync(new CodeGraphQuery { Callees = run.Id }, CancellationToken.None);
        var references = await _repository.QueryGraphAsync(new CodeGraphQuery { References = "Helper" }, CancellationToken.None);

        Assert.Contains(calls.Edges, e => e.Kind == "calls" && e.TargetName == "Helper");
        Assert.Contains(callees.Edges, e => e.SourceId == run.Id && e.TargetName == "Helper");
        Assert.Contains(references.References, r => r.Kind == "call" && r.Target == "Helper");
    }

    [Fact]
    public async Task QueryGraphAsync_DefaultOrderingShowsImportsBeforeCalls()
    {
        var source = Path.Combine(_projectDir, "Ordered.cs");
        await File.WriteAllTextAsync(source, """
            using System;
            using Microsoft.Data.Sqlite;

            public class Ordered
            {
                public void Run()
                {
                    One();
                    Two();
                    Three();
                }

                private void One() { }
                private void Two() { }
                private void Three() { }
            }
            """);

        await MakeService().IndexAsync(_projectDir, CancellationToken.None);

        var graph = await _repository.QueryGraphAsync(new CodeGraphQuery(), CancellationToken.None);

        Assert.NotEmpty(graph.Edges);
        Assert.Equal("imports", graph.Edges[0].Kind);
        Assert.Contains(graph.Edges, e => e.Kind == "calls");
    }

    [Fact]
    public async Task IndexAsync_SkipsIgnoredDirectoriesAndLargeFiles()
    {
        Directory.CreateDirectory(Path.Combine(_projectDir, "bin"));
        await File.WriteAllTextAsync(Path.Combine(_projectDir, "bin", "Ignored.cs"), "public class Ignored {}");
        await File.WriteAllTextAsync(Path.Combine(_projectDir, "Large.cs"), new string('x', 1_000_001));

        var result = await MakeService().IndexAsync(_projectDir, CancellationToken.None);

        Assert.Equal(0, result.FilesIndexed);
        Assert.Equal(1, result.FilesSkipped);
    }

    [Fact]
    public void CodeLanguageRegistry_GetLanguage_MapsMarkdownExtension()
    {
        var language = CodeLanguageRegistry.GetLanguage("notes.md");

        Assert.Equal("markdown", language);
    }

    [Fact]
    public void CodeStructureProviderRegistry_Select_RoutesMarkdownToMarkdownProvider()
    {
        var markdownProvider = Substitute.For<ICodeStructureProvider>();
        markdownProvider.Id.Returns("markdown");
        markdownProvider.CanHandle("markdown").Returns(true);

        var treeSitterProvider = Substitute.For<ICodeStructureProvider>();
        treeSitterProvider.Id.Returns("tree-sitter");
        treeSitterProvider.CanHandle("markdown").Returns(false);

        var fallbackProvider = Substitute.For<ICodeStructureProvider>();
        fallbackProvider.Id.Returns("regex-fallback");

        var registry = new CodeStructureProviderRegistry([treeSitterProvider, markdownProvider, fallbackProvider]);

        var selected = registry.Select("markdown");

        Assert.Equal("markdown", selected.Id);
    }

    [Fact]
    public async Task IndexAsync_ReindexesChangedFileWithStableSymbolIds()
    {
        var source = Path.Combine(_projectDir, "Stable.cs");
        await File.WriteAllTextAsync(source, "public class Stable { }");
        var service = MakeService();

        await service.IndexAsync(_projectDir, CancellationToken.None);
        var first = await _repository.QuerySymbolsAsync(new CodeSymbolQuery { Query = "Stable" }, CancellationToken.None);

        await File.WriteAllTextAsync(source, "public class Stable { public void Run() { } }");
        await service.IndexAsync(_projectDir, CancellationToken.None);
        var second = await _repository.QuerySymbolsAsync(new CodeSymbolQuery { Query = "Stable" }, CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second, s => s.Name == "Stable");
        Assert.Equal(first[0].Id, second.Single(s => s.Name == "Stable").Id);
    }

    [Fact]
    public async Task RegexFallback_RecordsHeuristicProvenanceAndSourceSpan()
    {
        var provider = new RegexFallbackCodeStructureProvider();
        var file = new CodeFileIdentity
        {
            ProjectRoot = _projectDir,
            Path = Path.Combine(_projectDir, "tool.py"),
            RelativePath = "tool.py",
            Language = "python",
            ContentHash = "hash",
            SizeBytes = 32,
        };

        var document = await provider.ParseAsync(file, "class Tool:\n    pass\n", CancellationToken.None);
        var symbol = Assert.Single(document.Symbols);

        Assert.Equal("heuristic", symbol.Provenance.FactKind);
        Assert.Equal(1, symbol.Span.StartLine);
        Assert.Equal(1, symbol.Span.StartColumn);
    }

    [Fact]
    public async Task CodeIndex_SaveDocuments_WhenDatabaseReadonly_DoesNotThrow()
    {
        SqliteConnection.ClearAllPools();
        File.SetAttributes(_options.DatabasePath, FileAttributes.ReadOnly);
        var repository = new SqliteCodeIndexRepository(_options, new SqliteSchemaInitializer(_options));

        var ex = await Record.ExceptionAsync(() =>
            repository.SaveDocumentsAsync([MakeDocument()], CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task CodeIndex_QuerySymbols_WhenInitFails_ReturnsEmpty()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"hypa-code-file-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(dataDir, "not a directory");
            var options = new HypaDataOptions { DataDirectory = dataDir };
            var repository = new SqliteCodeIndexRepository(options, new SqliteSchemaInitializer(options));

            var symbols = await repository.QuerySymbolsAsync(new CodeSymbolQuery { Query = "Anything" }, CancellationToken.None);

            Assert.Empty(symbols);
        }
        finally
        {
            if (File.Exists(dataDir))
                File.Delete(dataDir);
        }
    }

    private CodeIndexService MakeService()
    {
        var rootDetector = Substitute.For<IProjectRootDetector>();
        rootDetector.Detect(Arg.Any<string>()).Returns(_projectDir);
        var registry = new CodeStructureProviderRegistry([new RegexFallbackCodeStructureProvider()]);
        return new CodeIndexService(rootDetector, registry, _repository);
    }

    private static CodeStructureDocument MakeDocument()
    {
        var provenance = new ProviderProvenance
        {
            ProviderId = "test",
            ProviderVersion = "1",
            QueryVersion = "1",
            FactKind = "syntactic",
            Confidence = 1,
        };

        return new CodeStructureDocument
        {
            File = new CodeFileIdentity
            {
                ProjectRoot = "/project",
                Path = "/project/File.cs",
                RelativePath = "File.cs",
                Language = "csharp",
                ContentHash = "hash",
                SizeBytes = 10,
            },
            Provenance = provenance,
            Symbols = [
                new CodeSymbol
                {
                    Id = "sym_file_type",
                    FilePath = "File.cs",
                    Language = "csharp",
                    Name = "File",
                    Kind = "class",
                    Span = new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 10, StartByte = 0, EndByte = 10 },
                    Provenance = provenance,
                },
            ],
        };
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(entry, FileAttributes.Normal);

        for (var i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (i < 4)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException) when (i < 4)
            {
                await Task.Delay(50);
            }
        }
    }
}
