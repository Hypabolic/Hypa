using System.Text;
using Hypa.Infrastructure.Hooks;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class ReadRedirectorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hypa-read-redirector-{Guid.NewGuid():N}");

    [Fact]
    public async Task RedirectAsync_WhenMarkdownFileIsLarge_ParsesAsMarkdown()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "notes.md");
        File.WriteAllText(path, "# Notes\n");
        var content = "# Notes\n\n" + string.Join('\n', Enumerable.Repeat("Long markdown body.", 600));
        var bytes = Encoding.UTF8.GetBytes(content);
        var fileSystem = Substitute.For<IFileSystem>();
        var projectRootDetector = Substitute.For<IProjectRootDetector>();
        var provider = Substitute.For<ICodeStructureProvider>();
        provider.Id.Returns("markdown");
        provider.CanHandle("markdown").Returns(true);
        provider.ParseAsync(Arg.Is<CodeFileIdentity>(f => f.Language == "markdown"), content, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new CodeStructureDocument
            {
                File = ci.ArgAt<CodeFileIdentity>(0),
                Provenance = MakeProvenance(),
                Symbols = [MakeHeading(path)],
            }));
        fileSystem.ReadAllBytes(path).Returns(bytes);
        projectRootDetector.Detect(Arg.Any<string>()).Returns(_tempDir);
        var redirector = new ReadRedirector(fileSystem, projectRootDetector, new CodeStructureProviderRegistry([provider]));

        var redirectedPath = await redirector.RedirectAsync(path, CancellationToken.None);

        Assert.NotNull(redirectedPath);
        Assert.True(File.Exists(redirectedPath));
        var outline = await File.ReadAllTextAsync(redirectedPath);
        Assert.Contains("heading Notes", outline);
        await provider.Received(1).ParseAsync(
            Arg.Is<CodeFileIdentity>(f => f.Language == "markdown" && f.RelativePath == "notes.md"),
            content,
            Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static CodeSymbol MakeHeading(string path) => new()
    {
        Id = "heading-notes",
        FilePath = path,
        Language = "markdown",
        Name = "Notes",
        Kind = "heading",
        Span = new SourceSpan
        {
            StartLine = 1,
            StartColumn = 1,
            EndLine = 1,
            EndColumn = 8,
            StartByte = 0,
            EndByte = 7,
        },
        Provenance = MakeProvenance(),
    };

    private static ProviderProvenance MakeProvenance() => new()
    {
        ProviderId = "markdown",
        ProviderVersion = "1",
        QueryVersion = "1",
        FactKind = "syntactic",
        Confidence = 1,
    };
}
