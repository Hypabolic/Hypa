using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;
using TreeSitter;

namespace Hypa.Infrastructure.CodeIntelligence;

public sealed class TreeSitterCodeStructureProvider : ICodeStructureProvider
{
    private static int _astQueryFallbackLogged;

    public string Id => "tree-sitter";
    public string Version => "1.3.0";

    /// <summary>
    /// Neutral multi-language provider version. Per-fact provenance uses
    /// <see cref="TreeSitterQueryRegistry.QueryVersion"/> on the C# AST path and
    /// <see cref="TreeSitterQueryRegistry.RegexQueryVersion"/> on the regex path.
    /// </summary>
    public string QueryVersion => TreeSitterQueryRegistry.ProviderQueryVersion;

    /// <summary>
    /// Table-only selection: no native grammar load. Grammar load runs only in
    /// <see cref="ParseAsync"/> (worker process when export isolation is on).
    /// </summary>
    public bool CanHandle(string language) =>
        // markdown has its own dedicated MarkdownStructureProvider
        !language.Equals("markdown", StringComparison.OrdinalIgnoreCase) &&
        TreeSitterQueryRegistry.Grammars.ContainsKey(language);

    public CodeProviderHealth CheckHealth()
    {
        try
        {
            // Language is process-cached — do not dispose it.
            var language = CreateLanguage("javascript");
            using var parser = CreateParser(language);
            using var tree = Parse(parser, "const ok = true;");
            _ = tree.RootNode;
            var available = TreeSitterQueryRegistry.Grammars.Keys
                .Where(CanHandle)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(TreeSitterQueryRegistry.HealthLabel);

            var astFailed = TreeSitterQueryRegistry.Grammars.Keys
                .Where(TreeSitterQueryRegistry.HasAstQueryPack)
                .Select(l => (Lang: l, Err: TreeSitterQueryRegistry.GetAstQueryError(l)))
                .Where(x => x.Err is not null)
                .ToArray();

            var message = "TreeSitter.DotNet loaded. " + string.Join(", ", available);
            if (astFailed.Length > 0)
            {
                message += " | AST query failures: "
                    + string.Join("; ", astFailed.Select(f => $"{f.Lang}: {f.Err}"));
                return new CodeProviderHealth { ProviderId = Id, Status = "warn", Message = message };
            }

            return new CodeProviderHealth { ProviderId = Id, Status = "ok", Message = message };
        }
        catch (Exception ex)
        {
            return new CodeProviderHealth { ProviderId = Id, Status = "warn", Message = ex.Message };
        }
    }

    public Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, string content, CancellationToken ct) =>
        ParseAsync(file, SourceText.FromString(content), ct);

    public Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, SourceText source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // C# / TypeScript / TSX: execute tree-sitter queries against the AST when parse-valid.
        if (TreeSitterQueryRegistry.HasAstQueryPack(file.Language))
        {
            var cached = TreeSitterQueryRegistry.GetAstQuery(file.Language);
            if (cached is not null)
            {
                using var parser = CreateParser(cached.Language);
                using var tree = Parse(parser, source.Text);
                var parseValid = !tree.RootNode.HasError;

                if (parseValid)
                {
                    var queryProvenance = new ProviderProvenance
                    {
                        ProviderId = TreeSitterQueryRegistry.TreeSitterQueryProviderId,
                        ProviderVersion = Version,
                        QueryVersion = TreeSitterQueryRegistry.AstQueryVersion(file.Language),
                        FactKind = "symbol-declaration",
                        Confidence = 0.92,
                    };
                    var document = TreeSitterQueryExtractor.Extract(file, source, tree, cached.Query, queryProvenance);
                    return Task.FromResult(document with { ParseGateValid = true });
                }

                // Parse-invalid: keep regex fallback (unchanged behavior).
                var fallbackProvenance = new ProviderProvenance
                {
                    ProviderId = "regex-fallback",
                    ProviderVersion = Version,
                    QueryVersion = TreeSitterQueryRegistry.RegexQueryVersion,
                    FactKind = "symbol-declaration",
                    Confidence = 0.45,
                };
                var fallback = CodePatternExtractor.Extract(file, source, fallbackProvenance);
                return Task.FromResult(fallback with { ParseGateValid = false });
            }

            // Pack present but failed to compile — fall through to regex once-noted.
            if (Interlocked.Exchange(ref _astQueryFallbackLogged, 1) == 0)
            {
                var err = TreeSitterQueryRegistry.GetAstQueryError(file.Language) ?? "unknown";
                Console.Error.WriteLine(
                    $"hypa: tree-sitter AST query pack for '{file.Language}' unavailable ({err}); using regex extraction.");
            }
        }

        // Languages without an AST pack (or missing/failed pack): parse-gate then regex extraction.
        // Language is process-cached — do not dispose it.
        var language = CreateLanguage(file.Language);
        using var legacyParser = CreateParser(language);
        using var legacyTree = Parse(legacyParser, source.Text);
        var legacyValid = !legacyTree.RootNode.HasError;
        var provenance = legacyValid
            ? new ProviderProvenance
            {
                ProviderId = "hypa-pattern",
                ProviderVersion = Version,
                QueryVersion = TreeSitterQueryRegistry.RegexQueryVersion,
                FactKind = "symbol-declaration",
                Confidence = 0.79,
            }
            : new ProviderProvenance
            {
                ProviderId = "regex-fallback",
                ProviderVersion = Version,
                QueryVersion = TreeSitterQueryRegistry.RegexQueryVersion,
                FactKind = "symbol-declaration",
                Confidence = 0.45,
            };
        var legacyDocument = CodePatternExtractor.Extract(file, source, provenance);
        return Task.FromResult(legacyDocument with { ParseGateValid = legacyValid });
    }

    /// <summary>Process-cached language; callers must not dispose.</summary>
    private static Language CreateLanguage(string language) =>
        TreeSitterLanguageCache.GetOrLoad(language);

    private static Parser CreateParser(Language language) => new(language);

    private static Tree Parse(Parser parser, string content) =>
        parser.Parse(content) ?? throw new InvalidOperationException("Tree-sitter parse returned null.");
}
