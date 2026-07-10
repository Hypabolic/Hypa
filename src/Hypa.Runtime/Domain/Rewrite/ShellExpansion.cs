namespace Hypa.Runtime.Domain.Rewrite;

/// Detects command-substitution and variable-expansion markers (`$`, `` ` ``)
/// embedded inside argument tokens. Such tokens must be routed through
/// `sh -c` so the shell performs the expansion; running the program directly
/// would pass the unexpanded text through as a literal argument.
public static class ShellExpansion
{
    public static bool ContainsExpansion(IReadOnlyList<ShellToken> tokens) =>
        tokens.Any(token =>
            token.Kind is TokenKind.Arg or TokenKind.QuotedArg &&
            (token.Value.Contains('$') || token.Value.Contains('`')));

    /// <summary>
    /// Detects unquoted argument tokens that begin with a tilde, such as
    /// <c>~/x</c>, <c>~</c>, or <c>~user/bin</c>. Tilde expansion is performed
    /// by the shell and only at the start of an unquoted word; running the
    /// program directly would pass the literal text through as an argument, so
    /// such commands must route through <c>sh -c</c>. A tilde inside quotes
    /// (<c>"~/x"</c>) is intentionally not expanded by POSIX shells and stays
    /// on the direct-execution path, as does a tilde that is not at the start
    /// of a token (e.g. <c>a~b</c>).
    /// </summary>
    public static bool ContainsTildeExpansion(IReadOnlyList<ShellToken> tokens) =>
        tokens.Any(token =>
            token.Kind is TokenKind.Arg &&
            token.Value.StartsWith('~'));
}
