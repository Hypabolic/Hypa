namespace Hypa.Runtime.Application.Services;

/// <summary>
/// Builds canonical scope-path monikers for code symbols (Deep Code Graph Slice 2).
/// Monikers are content/scope addressed — they deliberately exclude byte offsets so
/// whitespace and sibling-body edits do not change symbol identity.
/// </summary>
/// <remarks>
/// Canonical shape: <c>{normalizedPath}#{kind}:{scopePath}[({paramTypes})]</c>
/// <list type="bullet">
/// <item><c>src/Foo.cs#class:Namespace.Outer</c></item>
/// <item><c>src/Foo.cs#method:Namespace.Outer.Run(string,int)</c></item>
/// <item><c>src/Foo.cs#constructor:Namespace.Outer.Outer(int)</c></item>
/// <item><c>src/Foo.cs#property:Namespace.Outer.Name</c></item>
/// </list>
/// Parameter type names (when present) disambiguate overloads; full semantic binding
/// is out of scope. Kind is always included so type/member forms with the same name
/// cannot collide.
/// </remarks>
public static class CodeSymbolMoniker
{
    /// <summary>
    /// Build a canonical moniker string. Pass <paramref name="parameterTypes"/> (possibly
    /// empty) for methods and constructors so the signature suffix <c>(...)</c> is emitted;
    /// pass <c>null</c> for types, fields, properties, and other non-callable kinds.
    /// </summary>
    /// <param name="filePath">Workspace-relative path (normalized to forward slashes).</param>
    /// <param name="kind">Symbol kind (<c>class</c>, <c>method</c>, …).</param>
    /// <param name="scopePath">Dot-separated namespace/type/member path (no file prefix).</param>
    /// <param name="parameterTypes">
    /// Comma-separated parameter type names for methods/constructors (e.g. <c>string,int</c>),
    /// or empty for parameterless callables. <c>null</c> omits the signature suffix entirely.
    /// </param>
    public static string Build(string filePath, string kind, string scopePath, string? parameterTypes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopePath);

        var path = NormalizePath(filePath);
        var body = parameterTypes is null
            ? $"{kind}:{scopePath}"
            : $"{kind}:{scopePath}({parameterTypes})";
        return $"{path}#{body}";
    }

    /// <summary>Normalize path separators to <c>/</c> for cross-platform moniker stability.</summary>
    public static string NormalizePath(string filePath) => filePath.Replace('\\', '/');
}
