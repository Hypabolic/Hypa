using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Infrastructure.CodeIntelligence;

/// <summary>
/// Provider for Markdown structure extraction (headings, code blocks, frontmatter, section edges).
/// </summary>
/// <remarks>
/// Extraction is managed (<see cref="CodePatternExtractor.ExtractMarkdown"/>). A prior path ran
/// tree-sitter-markdown only as a validity gate and then discarded the tree. That native gate
/// was pathological on large research docs (multi-second / multi-ten-second parses) and is the
/// primary source of SIGSEGV on Mosaic markdown fixtures under constrained hosts (issue #92).
/// Native tree-sitter is no longer invoked on the parse path; isolation workers therefore never
/// enter native code for markdown either.
/// </remarks>
public sealed class MarkdownStructureProvider : ICodeStructureProvider
{
    public string Id => "markdown";
    public string Version => "1.1.0";

    /// <summary>
    /// Markdown extraction version — independent of the C# AST query pack version
    /// (<see cref="TreeSitterQueryRegistry.QueryVersion"/>).
    /// Bumped to <c>markdown-3</c> when the discarded tree-sitter gate was removed so indexes
    /// stamped <c>markdown-2</c> reparse under the managed-only path.
    /// </summary>
    public string QueryVersion => "markdown-3";

    /// <summary>
    /// Table-only selection for markdown. No native grammar load.
    /// </summary>
    public bool CanHandle(string language) =>
        language.Equals("markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Health check for the managed markdown extractor (no native load).
    /// </summary>
    public CodeProviderHealth CheckHealth()
    {
        try
        {
            var probe = CodePatternExtractor.ExtractMarkdown(
                new CodeFileIdentity
                {
                    ProjectRoot = "/",
                    Path = "/health.md",
                    RelativePath = "health.md",
                    Language = "markdown",
                    ContentHash = "sha256:0",
                    SizeBytes = 8,
                    IndexedAt = DateTimeOffset.UnixEpoch,
                },
                SourceText.FromString("# Health\n"),
                new ProviderProvenance
                {
                    ProviderId = Id,
                    ProviderVersion = Version,
                    QueryVersion = QueryVersion,
                    FactKind = "markdown-section",
                    Confidence = 0.75,
                });

            if (probe.Sections.Count == 0)
            {
                return new CodeProviderHealth
                {
                    ProviderId = Id,
                    Status = "warn",
                    Message = "Managed markdown extractor returned no sections for probe input.",
                };
            }

            return new CodeProviderHealth
            {
                ProviderId = Id,
                Status = "ok",
                Message = "Managed markdown extractor ready (no native tree-sitter gate).",
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
    /// Parses a Markdown document and extracts structure via managed patterns.
    /// </summary>
    public Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, string content, CancellationToken ct) =>
        ParseAsync(file, SourceText.FromString(content), ct);

    public Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, SourceText source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var provenance = new ProviderProvenance
        {
            ProviderId = Id,
            ProviderVersion = Version,
            QueryVersion = QueryVersion,
            FactKind = "markdown-section",
            // Pattern-derived structural extraction on a successfully read file (contract band 0.50–0.79).
            Confidence = 0.75,
        };

        // Managed-only: never call tree-sitter-markdown here. See type remarks / issue #92.
        return Task.FromResult(
            CodePatternExtractor.ExtractMarkdown(file, source, provenance) with { ParseGateValid = true });
    }
}
