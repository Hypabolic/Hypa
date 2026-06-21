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
