using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Sdk.CodeIntelligence;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.CodeIntelligence;

/// <summary>
/// Integration tests that exercise <see cref="MarkdownStructureProvider"/> with the
/// real tree-sitter native library. Tests gracefully skip when the native grammar is
/// not available (e.g. developer machines that haven't run build-tree-sitter-markdown.sh).
/// On CI the grammar is always built before the .NET build, so tests always run there.
/// </summary>
public sealed class MarkdownStructureProviderIntegrationTests
{
    private static readonly MarkdownStructureProvider Provider = new();

    [Fact]
    public void CheckHealth_WhenGrammarAvailable_ReturnsOk()
    {
        if (!Provider.CanHandle("markdown"))
            return; // native library not present — acceptable on dev machines

        var health = Provider.CheckHealth();

        Assert.Equal("markdown", health.ProviderId);
        Assert.Equal("ok", health.Status);
    }

    [Fact]
    public async Task ParseAsync_WhenGrammarAvailable_ExtractsSections()
    {
        if (!Provider.CanHandle("markdown"))
            return;

        var content = """
            # Introduction

            Some body text.

            ## Installation

            Run the installer.

            ### Prerequisites

            You need .NET 10.
            """;

        var doc = await Provider.ParseAsync(MakeFile("guide.md"), content, CancellationToken.None);

        Assert.True(doc.Sections.Count >= 3, $"Expected at least 3 sections, got {doc.Sections.Count}");
        Assert.Contains(doc.Sections, s => s.HeadingText == "Introduction" && s.HeadingLevel == 1);
        Assert.Contains(doc.Sections, s => s.HeadingText == "Installation" && s.HeadingLevel == 2);
        Assert.Contains(doc.Sections, s => s.HeadingText == "Prerequisites" && s.HeadingLevel == 3);
    }

    [Fact]
    public async Task ParseAsync_WhenGrammarAvailable_SectionProviderIdIsMarkdown()
    {
        if (!Provider.CanHandle("markdown"))
            return;

        var doc = await Provider.ParseAsync(MakeFile("notes.md"), "# Heading\n\nBody.\n", CancellationToken.None);

        var section = Assert.Single(doc.Sections);
        Assert.Equal("markdown", section.Provenance.ProviderId);
    }

    [Fact]
    public async Task ParseAsync_WhenGrammarAvailable_ExtractsFrontmatter()
    {
        if (!Provider.CanHandle("markdown"))
            return;

        var content = """
            ---
            title: My Doc
            version: 1.0
            ---

            # Heading

            Body.
            """;

        var doc = await Provider.ParseAsync(MakeFile("meta.md"), content, CancellationToken.None);

        Assert.NotNull(doc.FrontmatterYaml);
        Assert.Contains("title", doc.FrontmatterYaml, StringComparison.Ordinal);
        Assert.Contains("My Doc", doc.FrontmatterYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CanHandle_ReturnsFalseForCodeLanguages()
    {
        Assert.False(Provider.CanHandle("c-sharp"));
        Assert.False(Provider.CanHandle("typescript"));
        Assert.False(Provider.CanHandle("python"));
        Assert.False(Provider.CanHandle("rust"));
    }

    private static CodeFileIdentity MakeFile(string name) => new()
    {
        ProjectRoot = "/project",
        Path = $"/project/{name}",
        RelativePath = name,
        Language = "markdown",
        ContentHash = "hash",
        SizeBytes = 0,
    };
}
