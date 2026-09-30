using System.Collections.Concurrent;
using TreeSitter;

namespace Hypa.Infrastructure.CodeIntelligence;

/// <summary>
/// Grammar + query-pack registry. C# and TypeScript/TSX ship real tree-sitter
/// <see cref="Query"/> packs (embedded .scm); other languages keep the legacy boolean
/// syntactic toggles used by <see cref="CodePatternExtractor"/> until later slices.
/// </summary>
internal static class TreeSitterQueryRegistry
{
    /// <summary>Fact-level version for C# AST query extraction (symbol surface metadata).</summary>
    public const string QueryVersion = "tree-sitter-query-csharp-5";

    /// <summary>Fact-level version for TypeScript / TSX AST query extraction (symbol surface metadata).</summary>
    public const string TypescriptQueryVersion = "tree-sitter-query-typescript-3";

    /// <summary>
    /// Provider-level version advertised on <c>ICodeStructureProvider.QueryVersion</c> /
    /// health metadata. Neutral across languages (C#/TS use AST queries; others still regex).
    /// </summary>
    public const string ProviderQueryVersion = "tree-sitter-1";

    /// <summary>Legacy regex-path version retained for non-query languages' fact provenance.</summary>
    public const string RegexQueryVersion = "tree-sitter-syntactic-graph-1";

    public const string TreeSitterQueryProviderId = "tree-sitter-query";

    private static readonly ConcurrentDictionary<string, Lazy<AstQueryCacheEntry>> AstQueries =
        new(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<string, TreeSitterGrammar> Grammars = new Dictionary<string, TreeSitterGrammar>(StringComparer.OrdinalIgnoreCase)
    {
        ["c-sharp"] = new("tree-sitter-c-sharp", "tree_sitter_c_sharp"),
        ["typescript"] = new("tree-sitter-typescript", "tree_sitter_typescript"),
        ["tsx"] = new("tree-sitter-tsx", "tree_sitter_tsx"),
        ["javascript"] = new("tree-sitter-javascript", "tree_sitter_javascript"),
        ["jsx"] = new("tree-sitter-javascript", "tree_sitter_javascript"),
        ["python"] = new("tree-sitter-python", "tree_sitter_python"),
        ["go"] = new("tree-sitter-go", "tree_sitter_go"),
        ["rust"] = new("tree-sitter-rust", "tree_sitter_rust"),
        ["java"] = new("tree-sitter-java", "tree_sitter_java"),
        ["c"] = new("tree-sitter-c", "tree_sitter_c"),
        ["cpp"] = new("tree-sitter-cpp", "tree_sitter_cpp"),
        ["bash"] = new("tree-sitter-bash", "tree_sitter_bash"),
        ["json"] = new("tree-sitter-json", "tree_sitter_json"),
        ["yaml"] = new("tree-sitter-yaml", "tree_sitter_yaml"),
        ["toml"] = new("tree-sitter-toml", "tree_sitter_toml"),
        ["markdown"] = new("tree-sitter-markdown", "tree_sitter_markdown"),
    };

    public static readonly IReadOnlyDictionary<string, SyntacticQueryPack> QueryPacks = new Dictionary<string, SyntacticQueryPack>(StringComparer.OrdinalIgnoreCase)
    {
        ["c-sharp"] = SyntacticQueryPack.Full,
        ["typescript"] = SyntacticQueryPack.Full,
        ["tsx"] = SyntacticQueryPack.Full,
        ["javascript"] = SyntacticQueryPack.CallsAndReferences,
        ["jsx"] = SyntacticQueryPack.CallsAndReferences,
        ["python"] = SyntacticQueryPack.Full,
        ["go"] = SyntacticQueryPack.Full,
        ["rust"] = SyntacticQueryPack.Full,
        ["java"] = SyntacticQueryPack.Full,
        ["c"] = SyntacticQueryPack.CallsAndReferences,
        ["cpp"] = SyntacticQueryPack.Full,
        ["bash"] = SyntacticQueryPack.CallsAndReferences,
        ["json"] = SyntacticQueryPack.Config,
        ["yaml"] = SyntacticQueryPack.Config,
        ["toml"] = SyntacticQueryPack.Config,
        ["markdown"] = SyntacticQueryPack.Markdown,
    };

    /// <summary>Languages with a real AST query pack (C# Slice 1; TypeScript/TSX Slice 4).</summary>
    public static bool HasAstQueryPack(string language) =>
        language.Equals("c-sharp", StringComparison.OrdinalIgnoreCase)
        || language.Equals("typescript", StringComparison.OrdinalIgnoreCase)
        || language.Equals("tsx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fact-level query version for AST provenance of the given language.</summary>
    public static string AstQueryVersion(string language) =>
        language.Equals("c-sharp", StringComparison.OrdinalIgnoreCase)
            ? QueryVersion
            : TypescriptQueryVersion;

    /// <summary>
    /// Returns a long-lived language + compiled query for AST extraction, or null when the
    /// language has no pack / the pack failed to compile. Callers must NOT dispose the returned
    /// language or query — they are process-cached for AOT-safe reuse.
    /// </summary>
    public static CachedLanguageQuery? GetAstQuery(string language)
    {
        if (!HasAstQueryPack(language))
            return null;

        return GetAstQueryEntry(language).Query;
    }

    /// <summary>Compile error message when an AST pack failed to load, else null.</summary>
    public static string? GetAstQueryError(string language)
    {
        if (!HasAstQueryPack(language))
            return null;
        return GetAstQueryEntry(language).Error;
    }

    /// <summary>Health label fragment for a language (grammar / queries / failure).</summary>
    public static string HealthLabel(string language)
    {
        if (HasAstQueryPack(language))
        {
            var entry = GetAstQueryEntry(language);
            return entry.Query is not null
                ? $"{language}:grammar+ast-queries"
                : $"{language}:grammar+ast-queries-failed";
        }

        return QueryPacks.ContainsKey(language)
            ? $"{language}:grammar+queries"
            : $"{language}:grammar";
    }

    private static AstQueryCacheEntry GetAstQueryEntry(string language)
    {
        var cached = AstQueries.GetOrAdd(language, static lang => new Lazy<AstQueryCacheEntry>(() => TryCompile(lang)));
        return cached.Value;
    }

    private static AstQueryCacheEntry TryCompile(string language)
    {
        if (!Grammars.TryGetValue(language, out var grammar))
            return new AstQueryCacheEntry(null, $"No grammar registered for '{language}'.");

        string? scm;
        try
        {
            scm = LoadEmbeddedQuerySource(language);
        }
        catch (Exception ex)
        {
            return new AstQueryCacheEntry(null, $"Failed to load embedded query pack: {ex.Message}");
        }

        if (scm is null)
            return new AstQueryCacheEntry(null, "Embedded query pack resource not found.");

        try
        {
            // Use process-cached Language so we never create/dispose static grammar wrappers
            // just to compile the query pack (issue #92 / TreeSitter.DotNet dispose guidance).
            var lang = TreeSitterLanguageCache.GetOrLoad(language);
            var query = new Query(lang, scm);
            return new AstQueryCacheEntry(new CachedLanguageQuery(lang, query, scm), null);
        }
        catch (Exception ex)
        {
            return new AstQueryCacheEntry(null, $"Query compile failed: {ex.Message}");
        }
    }

    private static string? LoadEmbeddedQuerySource(string language)
    {
        // LogicalName set in Hypa.Infrastructure.csproj — stable under AOT.
        // typescript and tsx share one pack (node types align across both grammars).
        var resourceName = language.ToLowerInvariant() switch
        {
            "c-sharp" => "Hypa.Infrastructure.CodeIntelligence.Queries.c_sharp.tags.scm",
            "typescript" or "tsx" => "Hypa.Infrastructure.CodeIntelligence.Queries.typescript.tags.scm",
            _ => null,
        };
        if (resourceName is null)
            return null;

        var assembly = typeof(TreeSitterQueryRegistry).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Fallback: scan for tags.scm matching the language token under AOT renames.
            var token = language.Equals("c-sharp", StringComparison.OrdinalIgnoreCase)
                ? "c_sharp"
                : "typescript";
            var fallback = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.Contains("tags.scm", StringComparison.OrdinalIgnoreCase)
                                     && n.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (fallback is null)
                return null;
            using var fb = assembly.GetManifestResourceStream(fallback);
            if (fb is null)
                return null;
            using var fbReader = new StreamReader(fb);
            return fbReader.ReadToEnd();
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Visible for tests: list embedded resource names.</summary>
    internal static IReadOnlyList<string> EmbeddedResourceNames =>
        typeof(TreeSitterQueryRegistry).Assembly.GetManifestResourceNames();
}

internal sealed record TreeSitterGrammar(string Library, string Function);

internal sealed record SyntacticQueryPack(bool Symbols, bool Imports, bool Calls, bool References, bool Inheritance, bool Implements, bool Overrides)
{
    public static SyntacticQueryPack Full { get; } = new(true, true, true, true, true, true, true);
    public static SyntacticQueryPack CallsAndReferences { get; } = new(true, true, true, true, false, false, false);
    public static SyntacticQueryPack Config { get; } = new(true, true, false, false, false, false, false);
    public static SyntacticQueryPack Markdown { get; } = new(true, true, false, true, false, false, false);
}

/// <summary>Process-cached language + compiled query. Do not dispose.</summary>
internal sealed class CachedLanguageQuery(Language language, Query query, string source)
{
    public Language Language { get; } = language;
    public Query Query { get; } = query;
    public string Source { get; } = source;
}

internal sealed class AstQueryCacheEntry(CachedLanguageQuery? query, string? error)
{
    public CachedLanguageQuery? Query { get; } = query;
    public string? Error { get; } = error;
}
