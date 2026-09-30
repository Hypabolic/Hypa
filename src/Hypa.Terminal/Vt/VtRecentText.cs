using System.Text;

namespace Hypa.Terminal.Vt;

/// <summary>
/// Join helper for recent / recent-unwrapped reads.
/// <c>maxLines</c> is a physical-row budget. Unwrap joins wrap-flagged rows after that slice.
/// Wrap-flagged physical rows stay untrimmed; TrimEnd applies to each completed logical line.
/// </summary>
internal static class VtRecentText
{
    public static string JoinPhysical(
        IReadOnlyList<string> lines,
        IReadOnlyList<bool> wrap,
        int maxLines,
        bool unwrap,
        bool trim = true)
    {
        if (maxLines < 1)
            return string.Empty;
        if (wrap.Count != lines.Count)
            throw new ArgumentException("Wrap flags must match physical rows.", nameof(wrap));

        var end = lines.Count;
        while (end > 0 && string.IsNullOrWhiteSpace(lines[end - 1]))
            end--;

        var start = end > maxLines ? end - maxLines : 0;
        if (start >= end)
            return string.Empty;

        if (!unwrap)
        {
            var wrapped = new StringBuilder();
            for (var i = start; i < end; i++)
            {
                if (i > start)
                    wrapped.Append('\n');
                var physical = lines[i];
                if (trim)
                    physical = physical.TrimEnd();
                wrapped.Append(physical);
            }

            return wrapped.ToString();
        }

        var unwrapped = new StringBuilder();
        var logical = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            logical.Append(lines[i]);
            if (i < end - 1 && wrap[i])
                continue;

            var line = logical.ToString();
            if (trim)
                line = line.TrimEnd();
            if (unwrapped.Length > 0)
                unwrapped.Append('\n');
            unwrapped.Append(line);
            logical.Clear();
        }

        return unwrapped.ToString();
    }

    public static string TrimTrailingBlankLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var span = text.AsSpan();
        var end = span.Length;
        while (end > 0)
        {
            var lastNl = span[..end].LastIndexOf('\n');
            var lineStart = lastNl < 0 ? 0 : lastNl + 1;
            var line = span.Slice(lineStart, end - lineStart);
            if (!line.IsWhiteSpace())
                break;
            end = lastNl < 0 ? 0 : lastNl;
        }

        return end == span.Length ? text : text[..end];
    }
}
