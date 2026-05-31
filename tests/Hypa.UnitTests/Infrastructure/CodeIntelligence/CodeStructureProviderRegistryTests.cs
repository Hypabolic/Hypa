using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Runtime.Application.Services;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.CodeIntelligence;

/// <summary>
/// Verifies that <see cref="CodeStructureProviderRegistry"/> selects the correct provider
/// for each language, and that the providers' <c>CanHandle</c> contracts are mutually exclusive
/// for languages that have a dedicated provider.
/// </summary>
public sealed class CodeStructureProviderRegistryTests
{
    private static readonly CodeStructureProviderRegistry Registry = new(
    [
        new TreeSitterCodeStructureProvider(),
        new MarkdownStructureProvider(),
        new RegexFallbackCodeStructureProvider(),
    ]);

    // ── Provider selection ────────────────────────────────────────────────────

    [Fact]
    public void Select_ForMarkdown_ReturnsMarkdownProvider()
    {
        var provider = Registry.Select("markdown");

        Assert.Equal("markdown", provider.Id);
    }

    [Theory]
    [InlineData("c-sharp")]
    [InlineData("typescript")]
    [InlineData("python")]
    [InlineData("rust")]
    [InlineData("go")]
    public void Select_ForCodeLanguage_ReturnsTreeSitterProvider(string language)
    {
        var provider = Registry.Select(language);

        Assert.Equal("tree-sitter", provider.Id);
    }

    [Fact]
    public void Select_ForUnknownLanguage_ReturnsFallbackProvider()
    {
        var provider = Registry.Select("cobol");

        Assert.Equal("regex-fallback", provider.Id);
    }

    // ── CanHandle exclusivity ─────────────────────────────────────────────────

    [Fact]
    public void TreeSitterProvider_CanHandle_ReturnsFalseForMarkdown()
    {
        // Regression: TreeSitterCodeStructureProvider used to claim markdown
        // once libtree-sitter-markdown.so became loadable, causing it to win
        // over MarkdownStructureProvider in the registry's FirstOrDefault scan.
        var provider = new TreeSitterCodeStructureProvider();

        Assert.False(provider.CanHandle("markdown"));
    }

    [Fact]
    public void MarkdownProvider_CanHandle_ReturnsFalseForCodeLanguages()
    {
        var provider = new MarkdownStructureProvider();

        Assert.False(provider.CanHandle("c-sharp"));
        Assert.False(provider.CanHandle("typescript"));
        Assert.False(provider.CanHandle("python"));
    }

    [Fact]
    public void RegexFallback_CanHandle_ReturnsTrueForAnyLanguage()
    {
        var provider = new RegexFallbackCodeStructureProvider();

        Assert.True(provider.CanHandle("markdown"));
        Assert.True(provider.CanHandle("c-sharp"));
        Assert.True(provider.CanHandle("cobol"));
    }

    // ── No two non-fallback providers claim the same language ─────────────────

    [Theory]
    [InlineData("markdown")]
    [InlineData("c-sharp")]
    [InlineData("typescript")]
    [InlineData("python")]
    [InlineData("rust")]
    public void NonFallbackProviders_DoNotBothClaimSameLanguage(string language)
    {
        var nonFallback = Registry.Providers
            .Where(p => p.Id != "regex-fallback" && p.CanHandle(language))
            .ToList();

        Assert.True(nonFallback.Count <= 1,
            $"Multiple non-fallback providers claim '{language}': {string.Join(", ", nonFallback.Select(p => p.Id))}");
    }
}
