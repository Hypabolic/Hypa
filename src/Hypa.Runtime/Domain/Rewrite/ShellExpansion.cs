namespace Hypa.Runtime.Domain.Rewrite;

/// Detects command-substitution, variable-expansion, and tilde-expansion markers
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
    /// Detects unquoted argument tokens that are POSIX tilde words:
    /// <c>~</c>, <c>~/…</c>, <c>~user</c>, or <c>~user/…</c>. Tilde expansion
    /// is performed by the shell only at the start of an unquoted word; running
    /// the program directly would pass the literal text through as an argument,
    /// so such commands must route through <c>sh -c</c>.
    /// <para>
    /// Quoted tildes (<c>"~/x"</c>), non-leading tildes (<c>a~b</c>), and
    /// non-POSIX forms such as <c>~*</c> / <c>~?</c> are intentionally left on
    /// the direct-execution path: the shell would not perform tilde expansion
    /// for them, and routing would only change globbing semantics.
    /// </para>
    /// </summary>
    public static bool ContainsTildeExpansion(IReadOnlyList<ShellToken> tokens) =>
        tokens.Any(token =>
            token.Kind is TokenKind.Arg &&
            IsTildeWord(token.Value));

    /// <summary>
    /// Returns true for words a POSIX shell would treat as candidates for tilde
    /// expansion: bare <c>~</c>, <c>~/path</c>, or <c>~login</c>/<c>~login/path</c>
    /// where <c>login</c> is a non-empty sequence of portable username characters
    /// (<c>A–Z a–z 0–9 _ . -</c>).
    /// </summary>
    private static bool IsTildeWord(string value)
    {
        if (value.Length == 0 || value[0] != '~')
            return false;

        if (value.Length == 1)
            return true;

        if (value[1] == '/')
            return true;

        // ~user or ~user/... — login name must be non-empty and free of
        // metacharacters such as * ? [ that would otherwise force shell routing
        // solely for globbing side effects.
        var slash = value.IndexOf('/', 1);
        var loginLength = slash < 0 ? value.Length - 1 : slash - 1;
        if (loginLength <= 0)
            return false;

        for (var i = 1; i <= loginLength; i++)
        {
            var c = value[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
                return false;
        }

        return true;
    }
}
