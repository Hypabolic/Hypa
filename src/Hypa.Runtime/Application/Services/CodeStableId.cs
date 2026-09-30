using System.Security.Cryptography;
using System.Text;

namespace Hypa.Runtime.Application.Services;

/// <summary>
/// Deterministic fact IDs for the code index. Symbol IDs are derived from
/// <see cref="CodeSymbolMoniker"/> (scope-path monikers); reference/edge/diagnostic IDs
/// remain occurrence-addressed and may include source spans.
/// </summary>
public static class CodeStableId
{
    /// <summary>
    /// Hash a canonical symbol moniker into a <c>sym_</c>-prefixed id.
    /// Prefer building the moniker via <see cref="CodeSymbolMoniker.Build"/> so call sites
    /// share one shape.
    /// </summary>
    public static string ForSymbol(string moniker) =>
        "sym_" + Hash(moniker);

    /// <summary>
    /// Convenience: build a moniker then hash it. Pass <paramref name="parameterTypes"/> for
    /// methods/constructors (empty string ⇒ <c>()</c>); omit for non-callables.
    /// </summary>
    public static string ForSymbol(string filePath, string kind, string scopePath, string? parameterTypes = null) =>
        ForSymbol(CodeSymbolMoniker.Build(filePath, kind, scopePath, parameterTypes));

    public static string ForReference(string filePath, string kind, string target, int startByte) =>
        "ref_" + Hash($"{Normalize(filePath)}|{kind}|{target}|{startByte}");

    public static string ForEdge(string sourceId, string targetId, string kind) =>
        "edge_" + Hash($"{sourceId}|{targetId}|{kind}");

    public static string ForEdge(string sourceId, string targetId, string kind, int startByte) =>
        "edge_" + Hash($"{sourceId}|{targetId}|{kind}|{startByte}");

    public static string ForDiagnostic(string filePath, string code, int startByte) =>
        "diag_" + Hash($"{Normalize(filePath)}|{code}|{startByte}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];

    private static string Normalize(string value) => value.Replace('\\', '/');
}
