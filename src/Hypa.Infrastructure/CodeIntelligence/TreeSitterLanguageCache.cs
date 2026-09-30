using System.Collections.Concurrent;
using TreeSitter;

namespace Hypa.Infrastructure.CodeIntelligence;

/// <summary>
/// Process-wide cache of tree-sitter <see cref="Language"/> instances.
/// </summary>
/// <remarks>
/// TreeSitter.DotNet wraps static grammar pointers from native libraries. Creating and
/// disposing a Language per file is unnecessary churn; the package author also warns that
/// aggressive Dispose of native objects can contribute to access violations under load
/// (github.com/mariusgreuel/tree-sitter-dotnet-bindings#11). Cached languages are never
/// disposed for the lifetime of the process.
/// </remarks>
internal static class TreeSitterLanguageCache
{
    private static readonly ConcurrentDictionary<string, Lazy<Language>> Languages =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a process-cached language for the registry key (e.g. <c>c-sharp</c>).
    /// Callers must not dispose the returned instance.
    /// </summary>
    public static Language GetOrLoad(string language)
    {
        if (!TreeSitterQueryRegistry.Grammars.TryGetValue(language, out var grammar))
            throw new NotSupportedException($"Tree-sitter grammar is not registered for language '{language}'.");

        var entry = Languages.GetOrAdd(
            language,
            static (_, g) => new Lazy<Language>(() => new Language(g.Library, g.Function)),
            grammar);

        return entry.Value;
    }

    /// <summary>Test hook: clear cache (does not dispose prior instances).</summary>
    internal static void ClearForTests() => Languages.Clear();
}
