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
                Sections = [MakeSection("Notes", 1, 1)],
            }));
        fileSystem.ReadAllBytes(path).Returns(bytes);
        projectRootDetector.Detect(Arg.Any<string>()).Returns(_tempDir);
        var redirector = new ReadRedirector(fileSystem, projectRootDetector, new CodeStructureProviderRegistry([provider]));

        var redirectedPath = await redirector.RedirectAsync(path, CancellationToken.None);

        Assert.NotNull(redirectedPath);
        Assert.True(File.Exists(redirectedPath));
        var outline = await File.ReadAllTextAsync(redirectedPath);
        Assert.Contains("# Notes", outline);
        await provider.Received(1).ParseAsync(
            Arg.Is<CodeFileIdentity>(f => f.Language == "markdown" && f.RelativePath == "notes.md"),
            content,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RedirectAsync_SmallMarkdownFile_ReturnsNull()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "small.md");
        var content = "# Hello\n";
        var bytes = Encoding.UTF8.GetBytes(content);
        var fileSystem = Substitute.For<IFileSystem>();
        var projectRootDetector = Substitute.For<IProjectRootDetector>();
        var provider = Substitute.For<ICodeStructureProvider>();
        provider.Id.Returns("markdown");
        provider.CanHandle("markdown").Returns(true);
        fileSystem.ReadAllBytes(path).Returns(bytes);
        File.WriteAllText(path, content);
        var redirector = new ReadRedirector(fileSystem, projectRootDetector, new CodeStructureProviderRegistry([provider]));

        var result = await redirector.RedirectAsync(path, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RedirectAsync_ClaudeMdFile_ReturnsNull()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "CLAUDE.md");
        var content = "# CLAUDE\n\n" + string.Join('\n', Enumerable.Repeat("Some instruction.", 600));
        var bytes = Encoding.UTF8.GetBytes(content);
        var fileSystem = Substitute.For<IFileSystem>();
        var projectRootDetector = Substitute.For<IProjectRootDetector>();
        var provider = Substitute.For<ICodeStructureProvider>();
        provider.Id.Returns("markdown");
        provider.CanHandle("markdown").Returns(true);
        fileSystem.ReadAllBytes(path).Returns(bytes);
        File.WriteAllText(path, content);
        var redirector = new ReadRedirector(fileSystem, projectRootDetector, new CodeStructureProviderRegistry([provider]));

        var result = await redirector.RedirectAsync(path, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RedirectAsync_LargeMarkdownNoHeadings_ReturnsNull()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "noheadings.md");
        var content = string.Join('\n', Enumerable.Repeat("Just plain text with no headings.", 400));
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
            }));
        fileSystem.ReadAllBytes(path).Returns(bytes);
        File.WriteAllText(path, content);
        projectRootDetector.Detect(Arg.Any<string>()).Returns(_tempDir);
        var redirector = new ReadRedirector(fileSystem, projectRootDetector, new CodeStructureProviderRegistry([provider]));

        var result = await redirector.RedirectAsync(path, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RedirectAsync_LargeMarkdownWithHeadings_OutlineContainsHeadingHash()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "multi.md");
        var content = "# Title\n\n## Section\n\n### Sub\n\n" + string.Join('\n', Enumerable.Repeat("Body text here.", 600));
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
                Sections =
                [
                    MakeSection("Title", 1, 1),
                    MakeSection("Section", 2, 3),
                    MakeSection("Sub", 3, 5),
                ],
            }));
        fileSystem.ReadAllBytes(path).Returns(bytes);
        File.WriteAllText(path, content);
        projectRootDetector.Detect(Arg.Any<string>()).Returns(_tempDir);
        var redirector = new ReadRedirector(fileSystem, projectRootDetector, new CodeStructureProviderRegistry([provider]));

        var redirectedPath = await redirector.RedirectAsync(path, CancellationToken.None);

        Assert.NotNull(redirectedPath);
        var outline = await File.ReadAllTextAsync(redirectedPath);
        Assert.Contains("# Title", outline);
        Assert.Contains("## Section", outline);
        Assert.Contains("### Sub", outline);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static MarkdownSection MakeSection(string heading, int level, int line) => new()
    {
        Id = $"sec-{heading.ToLowerInvariant()}",
        FilePath = "notes.md",
        HeadingText = heading,
        HeadingLevel = level,
        HeadingPath = heading,
        HeadingAnchor = heading.ToLowerInvariant(),
        StartLine = line,
        EndLine = line + 5,
        StartByte = 0,
        EndByte = 100,
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
