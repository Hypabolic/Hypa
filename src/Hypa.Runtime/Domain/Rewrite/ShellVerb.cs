namespace Hypa.Runtime.Domain.Rewrite;

public static class ShellVerb
{
    public static string? Extract(IReadOnlyList<ShellToken> tokens)
    {
        var canSkipAssignments = true;

        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.Whitespace)
                continue;

            if (token.Kind == TokenKind.Arg)
            {
                if (canSkipAssignments && IsAssignment(token.Value))
                    continue;

                return token.Value;
            }

            if (token.Kind == TokenKind.QuotedArg)
                return StripQuotes(token.Value);

            canSkipAssignments = false;
        }

        return null;
    }

    // Returns only the Arg/QuotedArg tokens that form the command proper,
    // skipping any leading VAR=value environment-variable assignments.
    public static IReadOnlyList<ShellToken> CommandArgs(IReadOnlyList<ShellToken> tokens)
    {
        var args = tokens
            .Where(t => t.Kind is TokenKind.Arg or TokenKind.QuotedArg)
            .ToList();

        var skip = 0;
        foreach (var t in args)
        {
            if (t.Kind == TokenKind.Arg && IsAssignment(t.Value))
                skip++;
            else
                break;
        }

        return args.Skip(skip).ToList();
    }

    public static bool HasAssignmentPrefix(IReadOnlyList<ShellToken> tokens)
    {
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.Whitespace)
                continue;

            return token.Kind == TokenKind.Arg && IsAssignment(token.Value);
        }

        return false;
    }

    // Returns the leading VAR=value assignment tokens joined with spaces, or an empty string
    // when no leading assignments are present. Use this to preserve env prefixes in rewritten
    // output, e.g. prepend "FOO=bar " to produce "FOO=bar hypa git status".
    public static string AssignmentPrefix(IReadOnlyList<ShellToken> tokens)
    {
        var assignments = tokens
            .Where(t => t.Kind is TokenKind.Arg or TokenKind.QuotedArg)
            .TakeWhile(t => t.Kind == TokenKind.Arg && IsAssignment(t.Value))
            .Select(t => t.Value)
            .ToList();

        return assignments.Count == 0 ? "" : string.Join(" ", assignments) + " ";
    }

    private static bool IsAssignment(string value)
    {
        if (value.Length < 2 || (value[0] != '_' && !IsAsciiLetter(value[0])))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '=')
                return true;

            if (ch != '_' && !IsAsciiLetter(ch) && !char.IsAsciiDigit(ch))
                return false;
        }

        return false;
    }

    private static bool IsAsciiLetter(char ch) =>
        ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') ||
                                  (value[0] == '"' && value[^1] == '"')))
            return value[1..^1];

        return value;
    }
}
