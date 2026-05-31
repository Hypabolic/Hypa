using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Sdk.CodeIntelligence;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.CodeIntelligence;

public sealed class MarkdownStructureProviderTests
{
    [Fact]
    public void ExtractMarkdown_SingleHeading_PopulatesSectionMetadata()
    {
        var document = CodePatternExtractor.ExtractMarkdown(MakeFile("single.md"), "# Heading\n\nBody\n", MakeProvenance());

        var section = Assert.Single(document.Sections);
        Assert.Equal(1, section.HeadingLevel);
        Assert.Equal("Heading", section.HeadingText);
        Assert.Equal("Heading", section.HeadingPath);
        Assert.Equal("heading", section.HeadingAnchor);
    }

    [Fact]
    public void ExtractMarkdown_NestedHeadings_PopulatesSectionPaths()
    {
        var content = "# H1\n\n## H2\n\n### H3\n\ntext\n";
        var document = CodePatternExtractor.ExtractMarkdown(MakeFile("nested.md"), content, MakeProvenance());

        Assert.Equal(3, document.Sections.Count);
        Assert.Equal("H1", document.Sections[0].HeadingPath);
        Assert.Equal("H1/H2", document.Sections[1].HeadingPath);
        Assert.Equal("H1/H2/H3", document.Sections[2].HeadingPath);
    }

    [Fact]
    public void ExtractMarkdown_Frontmatter_PopulatesFrontmatterYaml()
    {
        var content = "---\ntitle: Test\n---\n\n# Heading\n";
        var document = CodePatternExtractor.ExtractMarkdown(MakeFile("frontmatter.md"), content, MakeProvenance());

        Assert.NotNull(document.FrontmatterYaml);
        Assert.Contains("title: Test", document.FrontmatterYaml);
    }

    [Fact]
    public void ExtractMarkdown_NoHeadings_ReturnsEmptySections()
    {
        var document = CodePatternExtractor.ExtractMarkdown(MakeFile("plain.md"), "Just text\n", MakeProvenance());

        Assert.NotNull(document);
        Assert.Empty(document.Sections);
    }

    [Fact]
    public void ExtractMarkdown_SecondHeading_SectionTextIsBoundedToSecondSection()
    {
        var content = "# First\n\nfirst content\n\n## Second\n\nsecond content\n";
        var document = CodePatternExtractor.ExtractMarkdown(MakeFile("bounded.md"), content, MakeProvenance());

        var second = Assert.Single(document.Sections.Where(s => s.HeadingText == "Second"));
        Assert.NotNull(second.Text);
        Assert.Contains("second content", second.Text);
        Assert.DoesNotContain("first content", second.Text);
    }

    [Fact]
    public void ExtractMarkdown_OutOfOrderHeadingLevels_UsesActualAncestorChain()
    {
        var content = "# H1\n\n### H3\n\ntext\n";
        var document = CodePatternExtractor.ExtractMarkdown(MakeFile("out-of-order.md"), content, MakeProvenance());

        Assert.Equal(2, document.Sections.Count);
        Assert.Equal("H1", document.Sections[0].HeadingPath);
        Assert.Equal("H1/H3", document.Sections[1].HeadingPath);
    }

    [Fact]
    public void ExtractMarkdown_SpecialCharactersInHeading_NormalizesAnchor()
    {
        var document = CodePatternExtractor.ExtractMarkdown(MakeFile("anchor.md"), "# API / REST Basics\n\nBody\n", MakeProvenance());

        var section = Assert.Single(document.Sections);
        Assert.Equal("API / REST Basics", section.HeadingText);
        Assert.Equal("api--rest-basics", section.HeadingAnchor);
    }

    [Fact]
    public void ExtractMarkdown_SetextHeading_IsNotExtracted()
    {
        var content = "Introduction\n============\n\nBody\n";
        var document = CodePatternExtractor.ExtractMarkdown(MakeFile("setext.md"), content, MakeProvenance());

        Assert.Empty(document.Sections);
    }

    [Fact]
    public void ExtractMarkdown_PlainText_StripsMarkdownSyntax()
    {
        var content = "# Heading\n\nBody with **bold**, `inline code`, and a [link](https://example.com).\n";
        var document = CodePatternExtractor.ExtractMarkdown(MakeFile("plain-text.md"), content, MakeProvenance());

        var section = Assert.Single(document.Sections);
        Assert.NotNull(section.PlainText);
        Assert.Contains("Body with bold, inline code, and a link.", section.PlainText);
        Assert.DoesNotContain("**", section.PlainText);
        Assert.DoesNotContain("`", section.PlainText);
        Assert.DoesNotContain("[link](https://example.com)", section.PlainText);
    }

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
