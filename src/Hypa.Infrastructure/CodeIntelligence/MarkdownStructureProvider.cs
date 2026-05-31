using Hypa.Runtime.Application.Ports;
using Hypa.Sdk.CodeIntelligence;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TreeSitter;

namespace Hypa.Infrastructure.CodeIntelligence;

/// <summary>
/// Provider for Markdown code structure extraction using tree-sitter.
/// Implements ADR-0006 parser tiers and extracts headings, code blocks, and frontmatter.
/// </summary>
public sealed class MarkdownStructureProvider : ICodeStructureProvider
{
    // Markdown grammar availability tracking for health checks
    private static readonly ConcurrentDictionary<string, bool> GrammarAvailability = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "markdown";
    public string Version => "1.0.0";
    public string QueryVersion => TreeSitterQueryRegistry.QueryVersion;

    /// <summary>
    /// Determines if this provider can handle the given language.
    /// Only handles "markdown" language.
    /// </summary>
    public bool CanHandle(string language) =>
        language.Equals("markdown", StringComparison.OrdinalIgnoreCase) &&
        GrammarAvailability.GetOrAdd("markdown", CanCreateLanguage);

    /// <summary>
    /// Performs health check by parsing a sample Markdown document.
    /// </summary>
    public CodeProviderHealth CheckHealth()
    {
        try
        {
            using var language = CreateLanguage("markdown");
            using var parser = CreateParser(language);
            using var tree = Parse(parser, "# Hello\n\nThis is a **test**.");
            var available = new[] { "markdown" }
                .Where(CanHandle)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(l => $"{l}:grammar");
            return new CodeProviderHealth
            {
                ProviderId = Id,
                Status = "ok",
                Message = "Tree-sitter markdown loaded. " + string.Join(", ", available),
            };
        }
        catch (Exception ex)
        {
            return new CodeProviderHealth
            {
                ProviderId = Id,
                Status = "warn",
                Message = ex.Message,
            };
        }
    }

    /// <summary>
    /// Parses a Markdown document and extracts structure.
    /// Uses CodePatternExtractor with markdown-specific patterns for headings and code blocks.
    /// </summary>
    public Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, string content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            using var language = CreateLanguage(file.Language);
            using var parser = CreateParser(language);
            using var _ = Parse(parser, content);

            var syntacticProvenance = new ProviderProvenance
            {
                ProviderId = Id,
                ProviderVersion = Version,
                QueryVersion = QueryVersion,
                FactKind = "syntactic",
                Confidence = 0.75,
            };

            return Task.FromResult(CodePatternExtractor.ExtractMarkdown(file, content, syntacticProvenance));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            var heuristicProvenance = new ProviderProvenance
            {
                ProviderId = Id,
                ProviderVersion = Version,
                QueryVersion = QueryVersion,
                FactKind = "heuristic",
                Confidence = 0.45,
            };

            return Task.FromResult(CodePatternExtractor.ExtractMarkdown(file, content, heuristicProvenance));
        }
    }

    /// <summary>
    /// Creates a tree-sitter language for Markdown.
    /// </summary>
    private static Language CreateLanguage(string language)
    {
        if (!TreeSitterQueryRegistry.Grammars.TryGetValue(language, out var grammar))
            throw new NotSupportedException($"Tree-sitter grammar is not registered for language '{language}'.");

        return new Language(grammar.Library, grammar.Function);
    }

    /// <summary>
    /// Creates a parser for the given language.
    /// </summary>
    private static Parser CreateParser(Language language) => new(language);

    /// <summary>
    /// Parses content using the tree-sitter parser.
    /// </summary>
    private static Tree Parse(Parser parser, string content) =>
        parser.Parse(content) ?? throw new InvalidOperationException("Tree-sitter parse returned null.");

    /// <summary>
    /// Checks if a language can be created (grammar/parser available).
    /// Used for health check availability tracking.
    /// </summary>
    private static bool CanCreateLanguage(string language)
    {
        try
        {
            using var _ = CreateLanguage(language);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
