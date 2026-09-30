using System.Globalization;
using System.Text;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Chrome and label text. Keeps printable graphemes. Drops C0, C1, ESC, and DEL.
/// Width is terminal columns, not UTF-16 length.
/// </summary>
public static class SafeDisplayText
{
    public const int MaxCommandCols = 32;

    public static bool ContainsUnsafeControl(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsUnsafeControl(rune))
                return true;
        }

        return false;
    }

    public static bool IsUnsafeControl(Rune rune)
    {
        var value = rune.Value;
        return value <= 0x1F || value == 0x7F || (value >= 0x80 && value <= 0x9F);
    }

    public static string Encode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var sb = new StringBuilder(text.Length);
        foreach (var grapheme in EnumerateGraphemes(text))
        {
            if (!IsPrintableGrapheme(grapheme))
                continue;
            sb.Append(grapheme);
        }

        return sb.ToString();
    }

    public static int Width(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var width = 0;
        foreach (var grapheme in EnumerateGraphemes(text))
            width += WidthOfGrapheme(grapheme);
        return width;
    }

    public static int Width(Rune rune)
    {
        if (IsUnsafeControl(rune) || Rune.IsControl(rune))
            return 0;

        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.Format
            or UnicodeCategory.Control)
        {
            return 0;
        }

        var value = rune.Value;
        if (value is >= 0x1100 and <= 0x115F
            or >= 0x2329 and <= 0x232A
            or >= 0x2E80 and <= 0x303E
            or >= 0x3040 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F1E6 and <= 0x1F1FF
            or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD)
        {
            return 2;
        }

        return 1;
    }

    public static string Clip(string? text, int maxCols)
    {
        var encoded = Encode(text);
        if (maxCols <= 0 || encoded.Length == 0)
            return "";

        var sb = new StringBuilder(encoded.Length);
        var used = 0;
        foreach (var grapheme in EnumerateGraphemes(encoded))
        {
            var width = WidthOfGrapheme(grapheme);
            if (width <= 0)
                continue;
            if (used + width > maxCols)
                break;
            sb.Append(grapheme);
            used += width;
        }

        return sb.ToString();
    }

    /// <summary>
    // Width 0 is empty.
    /// Width 1 that cannot keep a grapheme is an ellipsis.
    /// </summary>
    public static string TruncateEnd(string? text, int maxCols)
    {
        var encoded = Encode(text);
        if (maxCols <= 0)
            return "";
        if (Width(encoded) <= maxCols)
            return encoded;
        if (maxCols == 1)
            return "…";

        return Clip(encoded, maxCols - 1) + "…";
    }

    public static string PadRight(string? text, int cols)
    {
        var encoded = Encode(text);
        if (cols <= 0)
            return "";

        var width = Width(encoded);
        var clipped = width >= cols ? Clip(encoded, cols) : encoded;
        var used = Width(clipped);
        if (used >= cols)
            return clipped;
        return clipped + new string(' ', cols - used);
    }

    public static IEnumerable<string> EnumerateGraphemes(string text)
    {
        if (text.Length == 0)
            yield break;

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            yield return enumerator.GetTextElement();
    }

    private static bool IsPrintableGrapheme(string grapheme)
    {
        if (grapheme.Length == 0)
            return false;
        foreach (var rune in grapheme.EnumerateRunes())
        {
            if (IsUnsafeControl(rune))
                return false;
        }

        return WidthOfGrapheme(grapheme) > 0;
    }

    private static int WidthOfGrapheme(string grapheme)
    {
        var width = 0;
        foreach (var rune in grapheme.EnumerateRunes())
            width = Math.Max(width, Width(rune));
        return width;
    }
}
