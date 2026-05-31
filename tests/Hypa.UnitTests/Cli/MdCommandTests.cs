using System.CommandLine;
using System.Text.Json;
using Hypa.Cli.Commands;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class MdCommandTests
{
    [Fact]
    public async Task MdCommand_WhenTocFlag_CallsQueryTocAndPrintsHeadings()
    {
        var repository = Substitute.For<ICodeIndexRepository>();
        repository.QueryMarkdownSectionsAsync("notes.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MarkdownSection>>([
                MakeSection("notes.md", "Intro", 1, 0),
                MakeSection("notes.md", "Details", 2, 10),
            ]));
        var root = BuildRoot(repository);

        var (exitCode, output) = await InvokeAsync(root, ["md", "notes.md", "--toc"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Intro", output);
        Assert.Contains("Details", output);
        await repository.Received(1).QueryMarkdownSectionsAsync("notes.md", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MdCommand_WhenFrontmatterFlag_CallsQueryFrontmatterAndPrintsKeys()
    {
        var repository = Substitute.For<ICodeIndexRepository>();
        repository.QueryMarkdownAsync("notes.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CodeStructureDocument?>(new CodeStructureDocument
            {
                File = MakeFile("notes.md"),
                Provenance = MakeProvenance(),
                FrontmatterYaml = "title\nauthor",
            }));
        var root = BuildRoot(repository);

        var (exitCode, output) = await InvokeAsync(root, ["md", "notes.md", "--frontmatter"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("title", output);
        Assert.Contains("author", output);
        await repository.Received(1).QueryMarkdownAsync("notes.md", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MdCommand_WhenNoFlags_DefaultsToToc()
    {
        var repository = Substitute.For<ICodeIndexRepository>();
        repository.QueryMarkdownSectionsAsync("notes.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MarkdownSection>>([
                MakeSection("notes.md", "Intro", 1, 0),
            ]));
        var root = BuildRoot(repository);

        var (exitCode, output) = await InvokeAsync(root, ["md", "notes.md"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Intro", output);
        await repository.Received(1).QueryMarkdownSectionsAsync("notes.md", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MdCommand_WhenCombinedJson_EmitsSingleJsonObject()
    {
        var repository = Substitute.For<ICodeIndexRepository>();
        repository.QueryMarkdownAsync("notes.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CodeStructureDocument?>(new CodeStructureDocument
            {
                File = MakeFile("notes.md"),
                Provenance = MakeProvenance(),
                FrontmatterYaml = "title: Notes",
            }));
        repository.QueryMarkdownSectionsAsync("notes.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MarkdownSection>>([
                MakeSection("notes.md", "Intro", 1, 0),
                MakeSection("notes.md", "Details", 2, 10),
            ]));
        var root = BuildRoot(repository);

        var (exitCode, output) = await InvokeAsync(root, ["md", "notes.md", "--frontmatter", "--toc", "--section", "Intro", "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(output);
        Assert.Equal("notes.md", document.RootElement.GetProperty("filePath").GetString());
        Assert.Equal("title: Notes", document.RootElement.GetProperty("frontmatter").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("toc").GetArrayLength());
        Assert.True(document.RootElement.GetProperty("sectionMatched").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("sections").GetArrayLength());
    }

    [Fact]
    public async Task MdCommand_WhenSectionMiss_TextPrintsClearMessage()
    {
        var repository = Substitute.For<ICodeIndexRepository>();
        repository.QueryMarkdownSectionsAsync("notes.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MarkdownSection>>([
                MakeSection("notes.md", "Intro", 1, 0),
            ]));
        var root = BuildRoot(repository);

        var (exitCode, output) = await InvokeAsync(root, ["md", "notes.md", "--section", "Missing"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("No Markdown section matched 'Missing'.", output);
    }

    private static RootCommand BuildRoot(ICodeIndexRepository repository)
    {
        var registry = new CodeStructureProviderRegistry([]);
        var queryService = new CodeQueryService(repository);
        var indexService = new CodeIndexService(Substitute.For<IProjectRootDetector>(), registry, repository);
        var diagnosticsService = new CodeDiagnosticsService(repository, registry);
        var root = new RootCommand();
        root.AddCommand(new CodeCommand(indexService, queryService, diagnosticsService).BuildMd());
        return root;
    }

    private static async Task<(int ExitCode, string Output)> InvokeAsync(Command command, string[] args)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exitCode = await command.InvokeAsync(args);
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static MarkdownSection MakeSection(string filePath, string headingText, int headingLevel, int startByte) => new()
    {
        Id = $"section_{startByte}",
        FilePath = filePath,
        HeadingText = headingText,
        HeadingLevel = headingLevel,
        HeadingPath = headingText,
        HeadingAnchor = headingText.ToLowerInvariant(),
        StartLine = startByte + 1,
        EndLine = startByte + 2,
        StartByte = startByte,
        EndByte = startByte + 5,
        PlainText = $"{headingText} content",
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
}
