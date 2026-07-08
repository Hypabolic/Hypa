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
}
