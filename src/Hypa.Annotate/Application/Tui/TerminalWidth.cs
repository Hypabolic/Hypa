namespace Hypa.Annotate.Application.Tui;

/// <summary>
// / Terminal cell width.
/// </summary>
public static class TerminalWidth
{
    private static readonly (uint Start, uint End)[] WideRanges =
    [
        (0x1100, 0x115f),
        (0x2e80, 0x303e),
        (0x3041, 0x33ff),
        (0x3400, 0x4dbf),
        (0x4e00, 0x9fff),
        (0xa000, 0xa4cf),
        (0xa960, 0xa97f),
        (0xac00, 0xd7a3),
        (0xf900, 0xfaff),
        (0xfe10, 0xfe19),
        (0xfe30, 0xfe6f),
        (0xff00, 0xff60),
        (0xffe0, 0xffe6),
        (0x1f300, 0x1f64f),
        (0x1f900, 0x1f9ff),
        (0x20000, 0x3fffd),
    ];

    public static int CharWidth(char character)
    {
        var codePoint = (uint)character;
        if (codePoint < 0x20 || (codePoint >= 0x7f && codePoint < 0xa0))
            return 0;
        if ((codePoint >= 0x0300 && codePoint <= 0x036f) || (codePoint >= 0x200b && codePoint <= 0x200f))
            return 0;
        return IsWide(codePoint) ? 2 : 1;
    }

    public static int StringWidth(string text)
    {
        var used = 0;
        foreach (var character in text)
            used += CharWidth(character);
        return used;
    }

    public static string TruncateToWidth(string text, int width)
    {
        if (width <= 0)
            return string.Empty;
        var used = 0;
        var buffer = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            var next = CharWidth(character);
            if (used + next > width)
                break;
            used += next;
            buffer.Append(character);
        }

        return buffer.ToString();
    }

    public static IReadOnlyList<string> WrapText(string text, int width)
    {
        var safeWidth = Math.Max(1, width);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var output = new List<string>();
        foreach (var sourceLine in normalized.Split('\n'))
        {
            if (sourceLine.Length == 0)
            {
                output.Add(string.Empty);
                continue;
            }

            var line = new System.Text.StringBuilder();
            var used = 0;
            foreach (var character in sourceLine)
            {
                var cells = CharWidth(character);
                if (used + cells > safeWidth && line.Length > 0)
                {
                    output.Add(line.ToString());
                    line.Clear();
                    used = 0;
                }

                line.Append(character);
                used += cells;
            }

            output.Add(line.ToString());
        }

        return output;
    }

    public static CommentLayout LayoutComment(IReadOnlyList<char> comment, int cursor, int width)
    {
        var safeWidth = Math.Max(1, width);
        var lines = new List<string> { string.Empty };
        var row = 0;
        var col = 0;
        var cursorRow = 0;
        var cursorCol = 0;
        for (var index = 0; index <= comment.Count; index++)
        {
            var cells = index < comment.Count ? CharWidth(comment[index]) : 0;
            if (col > 0 && col + cells > safeWidth)
            {
                lines.Add(string.Empty);
                row++;
                col = 0;
            }

            if (index == cursor)
            {
                cursorRow = row;
                cursorCol = col;
            }

            if (index >= comment.Count)
                break;

            var character = comment[index];
            if (character == '\n')
            {
                lines.Add(string.Empty);
                row++;
                col = 0;
            }
            else
            {
                lines[row] += character;
                col += cells;
            }
        }

        return new CommentLayout(lines, cursorRow, cursorCol);
    }

    private static bool IsWide(uint codePoint)
    {
        foreach (var (start, end) in WideRanges)
        {
            if (codePoint < start)
                return false;
            if (codePoint <= end)
                return true;
        }

        return false;
    }
}

public sealed record CommentLayout(IReadOnlyList<string> Lines, int CursorRow, int CursorCol);
