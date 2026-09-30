using System.Text;

namespace Hypa.Terminal.Vt;

/// <summary>
/// Length-preserving PII scrub for offline PTY feeds.
/// Every substitution keeps <see cref="Encoding.UTF8.GetByteCount(string)"/> equal
/// so CUP/OSC8 layout and 120-column wraps stay on the original cells.
/// </summary>
public static class RecordedFeedSanitizer
{
    public static readonly IReadOnlyList<(string From, string To)> ExactReplacements =
    [
        ("https://claude.ai/code/", "https://example.test/x/"),
        ("session_016MG5GvumCWWLCuuybcm7TQ", "fixture_00FIXTUREID00RECORD00000"),
        ("Matthews-Pro", "fixture-host"),
        ("matthew-fixture-host", "fixture-fixture-host"),
        ("matthew-", "fixture-"),
        ("Matthew", "Fixture"),
        ("matthew@", "fixture@"),
        ("matthew", "fixture"),
        ("/Users/", "/x/usr/"),
        ("/home/", "/x/hm/"),
    ];

    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sanitized = text;
        foreach (var (from, to) in ExactReplacements)
        {
            EnsureSameUtf8Length(from, to);
            sanitized = sanitized.Replace(from, to, StringComparison.Ordinal);
        }

        sanitized = ReplaceSessionIds(sanitized);
        sanitized = ReplaceEmails(sanitized);
        sanitized = ReplaceHomeUserPrefixes(sanitized);
        return sanitized;
    }

    public static void EnsureSameUtf8Length(string from, string to)
    {
        var fromBytes = Encoding.UTF8.GetByteCount(from);
        var toBytes = Encoding.UTF8.GetByteCount(to);
        if (fromBytes != toBytes)
        {
            throw new InvalidOperationException(
                "Sanitize replacement changes UTF-8 length: '" + from + "' (" + fromBytes
                + ") -> '" + to + "' (" + toBytes + ").");
        }
    }

    private static string ReplaceSessionIds(string text)
    {
        var sb = new StringBuilder(text.Length);
        const string prefix = "session_";
        const string replacementPrefix = "fixture_";
        EnsureSameUtf8Length(prefix, replacementPrefix);
        var i = 0;
        while (i < text.Length)
        {
            var at = text.IndexOf(prefix, i, StringComparison.Ordinal);
            if (at < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            var idStart = at + prefix.Length;
            var idEnd = idStart;
            while (idEnd < text.Length && IsSessionIdChar(text[idEnd]))
                idEnd++;

            sb.Append(text, i, at - i);
            sb.Append(replacementPrefix);
            if (idEnd > idStart)
                sb.Append(SameLengthXs(text[idStart..idEnd]));
            i = idEnd;
        }

        return sb.ToString();
    }

    private static bool IsSessionIdChar(char ch) =>
        char.IsAsciiLetterOrDigit(ch);

    private static string ReplaceHomeUserPrefixes(string text)
    {
        var sanitized = ReplacePrefixedUser(text, "/x/usr/");
        return ReplacePrefixedUser(sanitized, "/x/hm/");
    }

    /// <summary>
    /// After <c>/Users/</c> → <c>/x/usr/</c>, a home path is <c>/x/usr/matthew</c>.
    /// Replace the username with same-length <c>x</c> so the path width holds.
    /// </summary>
    private static string ReplacePrefixedUser(string text, string prefix)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var at = text.IndexOf(prefix, i, StringComparison.Ordinal);
            if (at < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            sb.Append(text, i, at - i);
            sb.Append(prefix);
            var userStart = at + prefix.Length;
            var userEnd = userStart;
            while (userEnd < text.Length && IsPathUserChar(text[userEnd]))
                userEnd++;

            if (userEnd > userStart)
            {
                var user = text[userStart..userEnd];
                sb.Append(SameLengthXs(user));
            }

            i = userEnd;
        }

        return sb.ToString();
    }

    private static string SameLengthXs(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        return new string('x', bytes);
    }

    private static bool IsPathUserChar(char ch) =>
        char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-';

    private static string ReplaceEmails(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var at = text.IndexOf('@', i);
            if (at < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            var start = at - 1;
            while (start >= i && IsEmailAtom(text[start]))
                start--;
            start++;

            var end = at + 1;
            while (end < text.Length && IsEmailAtom(text[end]))
                end++;

            if (start < at && end > at + 1)
            {
                sb.Append(text, i, start - i);
                sb.Append(text, start, at - start);
                sb.Append('-');
                sb.Append(text, at + 1, end - at - 1);
                i = end;
                continue;
            }

            sb.Append(text, i, at - i + 1);
            i = at + 1;
        }

        return sb.ToString();
    }

    private static bool IsEmailAtom(char ch) =>
        char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '+' or '-';
}
