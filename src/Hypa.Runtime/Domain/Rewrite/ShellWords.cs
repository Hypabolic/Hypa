using System.Text;

namespace Hypa.Runtime.Domain.Rewrite;

public static class ShellWords
{
    /// <summary>
    /// Builds argv from lexed tokens the way a shell does: quote removal on each part,
    /// with adjacent Arg/QuotedArg parts (no whitespace between) joined into one word.
    /// </summary>
    public static string[] ToArguments(IReadOnlyList<ShellToken> tokens)
    {
        var words = new List<string>();
        StringBuilder? current = null;
        var previousEnd = -1;

        foreach (var token in tokens)
        {
            if (token.Kind is not (TokenKind.Arg or TokenKind.QuotedArg))
                continue;

            var value = token.Kind == TokenKind.QuotedArg ? StripQuotes(token.Value) : token.Value;
            if (current is not null && token.Offset == previousEnd)
            {
                current.Append(value);
            }
            else
            {
                if (current is not null) words.Add(current.ToString());
                current = new StringBuilder(value);
            }

            previousEnd = token.Offset + token.Value.Length;
        }

        if (current is not null) words.Add(current.ToString());
        return [.. words];
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') ||
                                   (value[0] == '"' && value[^1] == '"')))
            return value[1..^1];
        return value;
    }
}
