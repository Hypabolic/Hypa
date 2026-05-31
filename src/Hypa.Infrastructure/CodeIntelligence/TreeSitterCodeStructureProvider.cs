using Hypa.Runtime.Application.Ports;
using Hypa.Sdk.CodeIntelligence;
using System.Collections.Concurrent;
using TreeSitter;

namespace Hypa.Infrastructure.CodeIntelligence;

public sealed class TreeSitterCodeStructureProvider : ICodeStructureProvider
{
    private static readonly ConcurrentDictionary<string, bool> GrammarAvailability = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "tree-sitter";
    public string Version => "1.3.0";
    public string QueryVersion => TreeSitterQueryRegistry.QueryVersion;

    public bool CanHandle(string language) =>
        // markdown has its own dedicated MarkdownStructureProvider
        !language.Equals("markdown", StringComparison.OrdinalIgnoreCase) &&
        TreeSitterQueryRegistry.Grammars.ContainsKey(language) &&
        GrammarAvailability.GetOrAdd(language, CanCreateLanguage);

    public CodeProviderHealth CheckHealth()
    {
        try
        {
            using var language = CreateLanguage("javascript");
            using var parser = CreateParser(language);
            _ = Parse(parser, "const ok = true;");
            var available = TreeSitterQueryRegistry.Grammars.Keys
                .Where(CanHandle)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(l => TreeSitterQueryRegistry.QueryPacks.ContainsKey(l) ? $"{l}:grammar+queries" : $"{l}:grammar");
            return new CodeProviderHealth { ProviderId = Id, Status = "ok", Message = "TreeSitter.DotNet loaded. " + string.Join(", ", available) };
        }
        catch (Exception ex)
        {
            return new CodeProviderHealth { ProviderId = Id, Status = "warn", Message = ex.Message };
        }
    }

    public Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, string content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var language = CreateLanguage(file.Language);
        using var parser = CreateParser(language);
        using var tree = Parse(parser, content);
        var provenance = new ProviderProvenance
        {
            ProviderId = Id,
            ProviderVersion = Version,
            QueryVersion = QueryVersion,
            FactKind = "syntactic",
            Confidence = 0.8,
        };
        var document = CodePatternExtractor.Extract(file, content, provenance);
        return Task.FromResult(document);
    }

    private static Language CreateLanguage(string language)
    {
        if (!TreeSitterQueryRegistry.Grammars.TryGetValue(language, out var grammar))
            throw new NotSupportedException($"Tree-sitter grammar is not registered for language '{language}'.");

        return new Language(grammar.Library, grammar.Function);
    }

    private static Parser CreateParser(Language language) => new(language);

    private static Tree Parse(Parser parser, string content) =>
        parser.Parse(content) ?? throw new InvalidOperationException("Tree-sitter parse returned null.");

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
