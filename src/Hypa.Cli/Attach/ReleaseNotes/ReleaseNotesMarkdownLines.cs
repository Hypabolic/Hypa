using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.ReleaseNotes;

/// <summary>
/// Plain-text pack notes with light markdown-ish rendering.
/// </summary>
public static class ReleaseNotesMarkdownLines
{
    public static IReadOnlyList<string> Build(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];

        var lines = new List<string>();
        var inFence = false;
        foreach (var raw in body.Split('\n'))
        {
            var trimmed = raw.TrimEnd();
            if (trimmed.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                lines.Add(trimmed);
                continue;
            }

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                lines.Add(trimmed[4..].Trim());
                continue;
            }

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                lines.Add(trimmed[3..].Trim());
                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                lines.Add(trimmed[2..].Trim());
                continue;
            }

            lines.Add(RenderInlineCode(trimmed));
        }

        return lines;
    }

    internal static string RenderInlineCode(string line)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains('`', StringComparison.Ordinal))
            return line;

        var sb = new System.Text.StringBuilder();
        var remaining = line;
        while (true)
        {
            var start = remaining.IndexOf('`');
            if (start < 0)
            {
                sb.Append(remaining);
                break;
            }

            sb.Append(remaining.AsSpan(0, start));
            remaining = remaining[(start + 1)..];
            var end = remaining.IndexOf('`');
            if (end < 0)
            {
                sb.Append('`');
                sb.Append(remaining);
                break;
            }

            var code = remaining[..end];
            if (code.Contains('=', StringComparison.Ordinal))
                code = code.Replace(" ", "\u00a0", StringComparison.Ordinal);
            sb.Append(code);
            remaining = remaining[(end + 1)..];
        }

        return sb.ToString();
    }

    public static int WrappedLineCount(IReadOnlyList<string> lines, int width)
    {
        width = Math.Max(1, width);
        var total = 0;
        for (var i = 0; i < lines.Count; i++)
            total += WrapCount(lines[i], width);
        return total;
    }

    public static IEnumerable<string> VisibleWrappedLines(
        IReadOnlyList<string> lines,
        int width,
        int scroll,
        int viewportRows)
    {
        width = Math.Max(1, width);
        viewportRows = Math.Max(0, viewportRows);
        var wrapped = new List<string>();
        for (var i = 0; i < lines.Count; i++)
            wrapped.AddRange(WrapLine(lines[i], width));

        var start = Math.Clamp(scroll, 0, Math.Max(0, wrapped.Count - viewportRows));
        for (var row = 0; row < viewportRows && start + row < wrapped.Count; row++)
            yield return wrapped[start + row];
    }

    private static int WrapCount(string line, int width)
    {
        if (string.IsNullOrEmpty(line))
            return 1;
        var encoded = SafeDisplayText.Encode(line);
        var displayWidth = SafeDisplayText.Width(encoded);
        if (displayWidth <= width)
            return 1;
        return (displayWidth + width - 1) / width;
    }

    private static IEnumerable<string> WrapLine(string line, int width)
    {
        if (string.IsNullOrEmpty(line))
        {
            yield return "";
            yield break;
        }

        var encoded = SafeDisplayText.Encode(line);
        var displayWidth = SafeDisplayText.Width(encoded);
        if (displayWidth <= width)
        {
            yield return line;
            yield break;
        }

        var offset = 0;
        while (offset < encoded.Length)
        {
            var chunk = SafeDisplayText.Clip(encoded[offset..], width);
            if (chunk.Length == 0)
                break;
            yield return chunk;
            offset += chunk.Length;
        }
    }
}
