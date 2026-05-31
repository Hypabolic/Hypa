using System.Reflection;
using System.Text;
using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Sdk.CodeIntelligence;
using Xunit;

namespace Hypa.GoldenTests;

public sealed class MarkdownGoldenTests
{
    private static readonly string FixturesPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "Fixtures", "markdown");

    [Theory]
    [InlineData("nested-headings")]
    [InlineData("special-chars")]
    public async Task ExtractMarkdown(string fixtureName)
    {
        var dir = Path.Combine(FixturesPath, fixtureName);
        var content = await File.ReadAllTextAsync(Path.Combine(dir, "input.md"));
        var meta = await File.ReadAllTextAsync(Path.Combine(dir, "meta.json"));
        Assert.False(string.IsNullOrWhiteSpace(meta));

        var identity = MakeFile($"{fixtureName}.md");
        var result = CodePatternExtractor.ExtractMarkdown(identity, content, MakeProvenance());

        await Verify(Normalize(Render(result)))
            .UseDirectory(dir)
            .UseFileName(fixtureName);
    }

    private static string Render(CodeStructureDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Frontmatter:");
        builder.AppendLine(document.FrontmatterYaml is null ? "<none>" : document.FrontmatterYaml.Trim());
        builder.AppendLine();
        builder.AppendLine("Sections:");
        foreach (var section in document.Sections)
        {
            builder.AppendLine($"- L{section.HeadingLevel} {section.HeadingText}");
            builder.AppendLine($"  Path: {section.HeadingPath}");
            builder.AppendLine($"  Anchor: {section.HeadingAnchor}");
        }

        return builder.ToString();
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();

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
