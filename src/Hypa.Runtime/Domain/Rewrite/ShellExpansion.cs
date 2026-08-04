namespace Hypa.Runtime.Domain.Rewrite;

/// <summary>
/// Detects expansion markers embedded inside argument tokens that require the
/// platform shell (<c>sh -c</c> / <c>cmd.exe /c</c>) so expansion happens before
/// the program runs. Running the program directly would pass the unexpanded
/// text through as a literal argument.
/// </summary>
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
    /// Quoted tildes (<c>"~/x"</c>) and non-leading tildes (<c>a~b</c>) are not
    /// tilde words. Forms such as <c>~*</c> / <c>~?</c> are also not tilde words
    /// (login names cannot contain glob metacharacters); they may still route
    /// through the shell via <see cref="ContainsGlobOrBraceExpansion"/> for
    /// pathname expansion.
    /// </para>
    /// </summary>
    public static bool ContainsTildeExpansion(IReadOnlyList<ShellToken> tokens) =>
        tokens.Any(token =>
            token.Kind is TokenKind.Arg &&
            IsTildeWord(token.Value));

    /// <summary>
    /// Detects unquoted argument tokens that require the shell for pathname
    /// expansion (glob) or brace expansion.
    /// <list type="bullet">
    /// <item>Glob: any unquoted <c>*</c>, <c>?</c>, or <c>[</c> in an Arg token.</item>
    /// <item>
    /// Brace: an unquoted <c>{…}</c> (or an open <c>{</c> prefix split across
    /// quote boundaries) whose interior contains <c>,</c> or <c>..</c>.
    /// Bare pairs such as <c>{x}</c> stay on the direct path. Detection is
    /// intentionally broad for ranges (any <c>..</c>); false-positive shell
    /// routing is preferred over missing real expansions.
    /// </item>
    /// </list>
    /// Quoted tokens (<see cref="TokenKind.QuotedArg"/>) are never treated as
    /// expanding for glob or brace, matching shell quoting rules.
    /// </summary>
    public static bool ContainsGlobOrBraceExpansion(IReadOnlyList<ShellToken> tokens) =>
        tokens.Any(token =>
            token.Kind is TokenKind.Arg &&
            (HasGlobMetacharacter(token.Value) || HasBraceExpansionForm(token.Value)));

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
        // metacharacters such as * ? [ (those are glob forms, not tilde words).
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

    private static bool HasGlobMetacharacter(string value) =>
        value.Contains('*') || value.Contains('?') || value.Contains('[');

    /// <summary>
    /// Conservative brace-expansion detection: any <c>{…}</c> span (or an
    /// open <c>{</c> that continues past the end of this token) whose interior
    /// contains a comma or <c>..</c> range marker.
    /// <para>
    /// Open spans matter because the lexer splits on quotes, so a single shell
    /// word such as <c>{a,"b"}</c> becomes adjacent Arg/QuotedArg tokens
    /// (<c>{a,</c> then <c>"b"</c> then <c>}</c>). Detecting the incomplete
    /// prefix still forces shell routing so the whole word expands correctly.
    /// </para>
    /// </summary>
    private static bool HasBraceExpansionForm(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '{')
                continue;

            var close = value.IndexOf('}', i + 1);
            // No closing brace in this token: still inspect the remainder so
            // quote-split prefixes like "{a," route through the shell.
            var end = close < 0 ? value.Length : close;
            var content = value.AsSpan(i + 1, end - i - 1);
            if (content.Contains(',') || content.Contains("..", StringComparison.Ordinal))
                return true;

            if (close < 0)
                break;

            i = close;
        }

        return false;
    }
}
