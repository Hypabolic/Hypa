using System.Text;
using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>One terminal row indexed by display columns.</summary>
internal sealed class ChromeRowBuffer
{
    private readonly string[] _cells;

    public ChromeRowBuffer(int cols)
    {
        _cells = new string[Math.Max(0, cols)];
        Array.Fill(_cells, " ");
    }

    public int Cols => _cells.Length;

    public void Write(int col, string? text)
    {
        if (col >= _cells.Length || col < 0)
            return;

        var encoded = SafeDisplayText.Encode(text);
        var cursor = col;
        foreach (var grapheme in SafeDisplayText.EnumerateGraphemes(encoded))
        {
            var width = SafeDisplayText.Width(grapheme);
            if (width <= 0)
                continue;
            if (cursor + width > _cells.Length)
                break;

            _cells[cursor] = grapheme;
            for (var i = 1; i < width; i++)
                _cells[cursor + i] = "";
            cursor += width;
        }
    }

    public string ToText()
    {
        var sb = new StringBuilder(_cells.Length);
        for (var i = 0; i < _cells.Length;)
        {
            var cell = _cells[i];
            if (cell.Length > 0)
            {
                sb.Append(cell);
                i += Math.Max(1, SafeDisplayText.Width(cell));
                continue;
            }

            sb.Append(' ');
            i++;
        }

        return sb.ToString();
    }
}
