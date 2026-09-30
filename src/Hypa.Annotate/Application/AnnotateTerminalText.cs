using System.Text;

namespace Hypa.Annotate.Application;

/// <summary>
/// Terminal-safe manager list text.
/// and <c>rust/src/manager.rs:681-688</c> <c>clipped</c>.
/// </summary>
internal static class AnnotateTerminalText
{
    /// <summary>
    /// List clip width for the manager popup. The manager pane requests 100
    // / columns.
    /// <c>list_width.saturating_sub(4)</c>.
    /// </summary>
    internal const int ManagerListClipWidth = 96;

    public static string Sanitize(string text)
    {
        var buffer = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character == '\t')
            {
                buffer.Append("    ");
                continue;
            }

            if (character <= '\u0008'
                || character is '\u000b' or '\u000c'
                || (character >= '\u000e' && character <= '\u001f')
                || character == '\u007f')
            {
                continue;
            }

            buffer.Append(character);
        }

        return buffer.ToString();
    }

    public static string ClipForList(string text, int width = ManagerListClipWidth)
    {
        var collapsed = string.Join(
            ' ',
            Sanitize(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (width < 1)
            return string.Empty;
        if (collapsed.Length <= width)
            return collapsed;

        var keep = Math.Max(0, width - 1);
        return collapsed[..keep] + "…";
    }
}
