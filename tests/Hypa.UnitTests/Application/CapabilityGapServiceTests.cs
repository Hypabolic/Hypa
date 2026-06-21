using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;
using Hypa.Sdk.CodeIntelligence;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class CapabilityGapServiceTests
{
    // Use an OS-appropriate absolute root and build child paths with the same
    // Path APIs the services use (Path.GetFullPath / Combine / GetRelativePath).
    // On Windows "/project" resolves to "C:\project" and relative paths use '\',
    // so hardcoded forward-slash strings would never match the mocks. Deriving
    // them this way keeps these tests green on every OS.
    private static readonly string ProjectRoot =
        Path.GetFullPath(OperatingSystem.IsWindows() ? @"C:\project" : "/project");

    private static string ProjectPath(params string[] segments) =>
        Path.GetFullPath(Path.Combine([ProjectRoot, .. segments]));

    private static string OsPath(string forwardSlash) =>
        forwardSlash.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public async Task FileReadAsync_WhenPathEscapesProjectRoot_ReturnsError()
    {
        var service = MakeFileReadService(out _, out _, out _);

        var result = await service.ReadAsync("../../secret.txt", mode: "full", maxTokens: null, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("PATH_ESCAPE", result.Error.Code);
    }

    [Fact]
    public async Task FileReadAsync_WhenFileIsRead_RecordsEvidence()
    {
        var service = MakeFileReadService(out var fileSystem, out _, out var ledger);
        fileSystem.ReadAllBytes(ProjectPath("src", "App.cs"))
            .Returns(System.Text.Encoding.UTF8.GetBytes("namespace Demo { public class App {} }"));

        var result = await service.ReadAsync("src/App.cs", mode: "full", maxTokens: null, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains("SUMMARY", result.Value.Text);
        await ledger.Received(1).RecordToolCallAsync(
            Arg.Is<ToolCallRecord>(r => r.ToolName == "hypa_read" && r.SessionId == Guid.Empty),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FileReadAsync_SmartMode_AliasesToFullWithoutParsingStructure()
    {
        var provider = Substitute.For<ICodeStructureProvider>();
        provider.Id.Returns("regex-fallback");
        provider.CanHandle(Arg.Any<string>()).Returns(true);
        provider.ParseAsync(Arg.Any<CodeFileIdentity>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MakeDocument());

        var service = MakeFileReadService(out var fileSystem, out _, out _, provider);
        fileSystem.ReadAllBytes(ProjectPath("src", "App.cs"))
            .Returns(System.Text.Encoding.UTF8.GetBytes("class App { }\n"));

        var result = await service.ReadAsync("src/App.cs", mode: "smart", maxTokens: null, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains("Mode: smart currently aliases to full.", result.Value.Text);
        await provider.DidNotReceive().ParseAsync(
            Arg.Any<CodeFileIdentity>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompressAsync_WithNoMatchingCompressor_ReturnsPassthroughAndRecordsEvidence()
    {
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(10);
        var ledger = MakeLedger();
        var service = new CompressService(
            [],
            tokenCounter,
            ledger,
            MakeUnresolvedSessionResolver(),
            NullLogger<CompressService>.Instance);

        var result = await service.CompressAsync("verbose log output", "log", command: null, maxTokens: null, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains("reducer=passthrough", result.Value.Text);
        await ledger.Received(1).RecordToolCallAsync(
            Arg.Is<ToolCallRecord>(r => r.ToolName == "hypa_compress"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompressAsync_WhenKindIsProvided_SelectsMappedCompressorBeforeCanHandleScan()
    {
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(10);
        var generic = MakeCompressor("generic", canHandle: false, text: "generic text");
        var fallback = MakeCompressor("fallback", canHandle: true, text: "fallback text");
        var service = new CompressService(
            [fallback, generic],
            tokenCounter,
            MakeLedger(),
            MakeUnresolvedSessionResolver(),
            NullLogger<CompressService>.Instance);

        var result = await service.CompressAsync("verbose log output", "log", command: null, maxTokens: 25, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("generic", result.Value.ReducerId);
        generic.Received(1).Compress(
            Arg.Any<CommandInvocation>(),
            Arg.Any<CommandOutput>(),
            Arg.Is<CompressionOptions>(o => o.MaxTotalLines == 5));
        fallback.DidNotReceive().Compress(
            Arg.Any<CommandInvocation>(), Arg.Any<CommandOutput>(), Arg.Any<CompressionOptions>());
    }

    [Fact]
    public async Task SearchAsync_InvalidRegex_ReturnsExpectedError()
    {
        var service = MakeSearchService(out _, out _, out _, out _);

        var result = await service.SearchAsync("[unclosed", scope: "project", kind: "regex", maxResults: 20, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("INVALID_REGEX", result.Error.Code);
    }

    [Fact]
    public async Task SearchAsync_TextMatch_ReturnsReferenceAndRecordsEvidence()
    {
        var service = MakeSearchService(out var fileSystem, out _, out _, out var ledger);
        fileSystem.GetFiles(ProjectRoot, "*.*", recursive: true).Returns([ProjectPath("src", "App.cs")]);
        fileSystem.ReadAllBytes(ProjectPath("src", "App.cs"))
            .Returns(System.Text.Encoding.UTF8.GetBytes("class App\n"));

        var result = await service.SearchAsync("App", scope: "project", kind: "text", maxResults: 20, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains(OsPath("src/App.cs:1"), result.Value.Text);
        await ledger.Received(1).RecordToolCallAsync(
            Arg.Is<ToolCallRecord>(r => r.ToolName == "hypa_search"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_DocsScope_SearchesOnlyDocumentationFiles()
    {
        var service = MakeSearchService(out var fileSystem, out _, out _, out _);
        fileSystem.GetFiles(ProjectRoot, "*.*", recursive: true).Returns([
            ProjectPath("docs", "guide.md"),
            ProjectPath("src", "App.cs"),
        ]);
        fileSystem.ReadAllBytes(ProjectPath("docs", "guide.md"))
            .Returns(System.Text.Encoding.UTF8.GetBytes("InitService docs\n"));

        var result = await service.SearchAsync("InitService", scope: "docs", kind: "text", maxResults: 20, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains(OsPath("docs/guide.md:1"), result.Value.Text);
        Assert.DoesNotContain(OsPath("src/App.cs"), result.Value.Text);
        fileSystem.DidNotReceive().ReadAllBytes(ProjectPath("src", "App.cs"));
    }

    [Fact]
    public async Task SearchAsync_CodeScopeWithTextKind_SearchesCodeFilesInsteadOfSymbols()
    {
        var service = MakeSearchService(out var fileSystem, out _, out var codeIndex, out _);
        fileSystem.GetFiles(ProjectRoot, "*.*", recursive: true).Returns([
            ProjectPath("src", "App.cs"),
            ProjectPath("docs", "guide.md"),
        ]);
        fileSystem.ReadAllBytes(ProjectPath("src", "App.cs"))
            .Returns(System.Text.Encoding.UTF8.GetBytes("class App\n"));

        var result = await service.SearchAsync("class App", scope: "code", kind: "text", maxResults: 20, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains(OsPath("src/App.cs:1"), result.Value.Text);
        Assert.DoesNotContain(OsPath("REFERENCES\n  docs/guide.md"), result.Value.Text);
        await codeIndex.DidNotReceive().QuerySymbolsAsync(
            Arg.Any<CodeSymbolQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_NoTextMatches_OmitsEmptyReferencesSection()
    {
        var service = MakeSearchService(out var fileSystem, out _, out _, out _);
        fileSystem.GetFiles(ProjectRoot, "*.*", recursive: true).Returns([ProjectPath("src", "App.cs")]);
        fileSystem.ReadAllBytes(ProjectPath("src", "App.cs"))
            .Returns(System.Text.Encoding.UTF8.GetBytes("class App\n"));

        var result = await service.SearchAsync("missing", scope: "project", kind: "text", maxResults: 20, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.DoesNotContain("REFERENCES", result.Value.Text);
    }

    [Fact]
    public async Task SearchAsync_WhenFileLimitReached_SurfacesIncompleteResultWarning()
    {
        var service = MakeSearchService(out var fileSystem, out _, out _, out _);
        var files = Enumerable.Range(0, 1001)
            .Select(i => ProjectPath($"file{i}.txt"))
            .ToArray();
        fileSystem.GetFiles(ProjectRoot, "*.*", recursive: true).Returns(files);
        fileSystem.ReadAllBytes(Arg.Any<string>())
            .Returns(System.Text.Encoding.UTF8.GetBytes("needle\n"));

        var result = await service.SearchAsync("needle", scope: "project", kind: "text", maxResults: 2000, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Contains("Searched 1000 of 1001 files (limit reached). Results may be incomplete.", result.Value.Text);
    }

    private static FileReadService MakeFileReadService(
        out IFileSystem fileSystem,
        out IProjectRootDetector rootDetector,
        out IEvidenceLedger ledger,
        ICodeStructureProvider? provider = null)
    {
        fileSystem = Substitute.For<IFileSystem>();
        fileSystem.GetCurrentDirectory().Returns(ProjectRoot);
        fileSystem.FileExists(Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(0) == ProjectPath("src", "App.cs"));
        rootDetector = Substitute.For<IProjectRootDetector>();
        rootDetector.Detect(Arg.Any<string>()).Returns(ProjectRoot);
        ledger = MakeLedger();
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(10);

        return new FileReadService(
            fileSystem,
            rootDetector,
            new CodeStructureProviderRegistry([provider ?? new RegexFallbackCodeStructureProvider()]),
            ledger,
            MakeUnresolvedSessionResolver(),
            tokenCounter,
            NullLogger<FileReadService>.Instance);
    }

    private static SearchService MakeSearchService(
        out IFileSystem fileSystem,
        out IProjectRootDetector rootDetector,
        out ICodeIndexRepository codeIndex,
        out IEvidenceLedger ledger)
    {
        fileSystem = Substitute.For<IFileSystem>();
        fileSystem.GetCurrentDirectory().Returns(ProjectRoot);
        fileSystem.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()).Returns([]);
        rootDetector = Substitute.For<IProjectRootDetector>();
        rootDetector.Detect(Arg.Any<string>()).Returns(ProjectRoot);
        codeIndex = Substitute.For<ICodeIndexRepository>();
        codeIndex.QuerySymbolsAsync(Arg.Any<CodeSymbolQuery>(), Arg.Any<CancellationToken>())
            .Returns([]);
        ledger = MakeLedger();

        return new SearchService(
            fileSystem,
            rootDetector,
            codeIndex,
            ledger,
            MakeUnresolvedSessionResolver(),
            NullLogger<SearchService>.Instance);
    }

    private static IEvidenceLedger MakeLedger()
    {
        var ledger = Substitute.For<IEvidenceLedger>();
        ledger.RecordToolCallAsync(Arg.Any<ToolCallRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return ledger;
    }

    private static IOutputCompressor MakeCompressor(string id, bool canHandle, string text)
    {
        var compressor = Substitute.For<IOutputCompressor>();
        compressor.Id.Returns(id);
        compressor.CanHandle(Arg.Any<CommandInvocation>()).Returns(canHandle);
        compressor.Compress(Arg.Any<CommandInvocation>(), Arg.Any<CommandOutput>(), Arg.Any<CompressionOptions>())
            .Returns(CompressionResult.From(text, 10, 5, id, [], false));
        return compressor;
    }

    private static CodeStructureDocument MakeDocument() => new()
    {
        File = new CodeFileIdentity
        {
            ProjectRoot = ProjectRoot,
            Path = ProjectPath("src", "App.cs"),
            RelativePath = "src/App.cs",
            Language = "csharp",
            ContentHash = "hash",
        },
        Provenance = new ProviderProvenance
        {
            ProviderId = "test",
            ProviderVersion = "1",
            QueryVersion = "1",
            FactKind = "syntactic",
            Confidence = 1,
        },
    };

    private static ISessionResolver MakeUnresolvedSessionResolver()
    {
        var resolver = Substitute.For<ISessionResolver>();
        resolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NONE", "no session")));
        return resolver;
    }
}
