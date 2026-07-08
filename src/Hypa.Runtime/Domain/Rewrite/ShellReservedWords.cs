namespace Hypa.Runtime.Domain.Rewrite;

public static class ShellReservedWords
{
    public static readonly IReadOnlySet<string> Values = new HashSet<string>(StringComparer.Ordinal)
    {
        "!", "{", "}", "[[", "]]", "case", "coproc", "do", "done", "elif",
        "else", "esac", "fi", "for", "function", "if", "in", "select",
        "then", "time", "until", "while",
    };

    public static bool IsReservedWord(string verb) => Values.Contains(verb);
}
