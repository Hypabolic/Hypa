namespace Hypa.Runtime.Domain.Rewrite;

/// Shell grammar keywords that introduce or delimit compound constructs
/// (loops, conditionals, brace groups, …). A segment starting with one of
/// these cannot be split into a separate `hypa -c` process without producing
/// invalid shell grammar, so any command containing one must be passed
/// through unchanged.
public static class ShellReservedWords
{
    public static readonly IReadOnlySet<string> ReservedWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "!", "{", "}", "[[", "]]", "case", "coproc", "do", "done", "elif",
        "else", "esac", "fi", "for", "function", "if", "in", "select",
        "then", "time", "until", "while",
    };

    public static bool IsReservedWord(string verb) => ReservedWords.Contains(verb);
}
