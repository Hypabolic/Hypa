using System.Text;

namespace Hypa.Annotate.Application.Tui;

internal enum CellPaintStyle
{
    None,
    Bold,
    Dim,
}

internal sealed class CellGrid
{
    private readonly char[][] _cells;
    private readonly CellPaintStyle[][] _styles;

    private CellGrid(int cols, int rows)
    {
        Cols = cols;
        RowsCount = rows;
        _cells = new char[rows][];
        _styles = new CellPaintStyle[rows][];
        for (var y = 0; y < rows; y++)
        {
            _cells[y] = new char[cols];
            _styles[y] = new CellPaintStyle[cols];
            Array.Fill(_cells[y], ' ');
        }
    }

    public int Cols { get; }

    public int RowsCount { get; }

    public int? CursorCol { get; private set; }

    public int? CursorRow { get; private set; }

    public IReadOnlyList<string> Rows
    {
        get
        {
            var list = new string[RowsCount];
            for (var y = 0; y < RowsCount; y++)
                list[y] = new string(_cells[y]);
            return list;
        }
    }

    public static CellGrid Blank(int cols, int rows) => new(cols, rows);

    public void SetCursor(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Cols || y >= RowsCount)
            return;
        CursorCol = x;
        CursorRow = y;
    }

    public CellPaintStyle StyleAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Cols || y >= RowsCount)
            return CellPaintStyle.None;
        return _styles[y][x];
    }

    public void Write(int x, int y, string text, int width, CellPaintStyle style = CellPaintStyle.None)
    {
        if (y < 0 || y >= RowsCount || x >= Cols)
            return;
        var clipped = TerminalWidth.TruncateToWidth(text, Math.Max(0, Math.Min(width, Cols - x)));
        var col = x;
        foreach (var character in clipped)
        {
            var cells = Math.Max(1, TerminalWidth.CharWidth(character));
            if (col < 0 || col + cells > Cols)
                break;
            _cells[y][col] = character;
            _styles[y][col] = style;
            for (var fill = 1; fill < cells && col + fill < Cols; fill++)
            {
                _cells[y][col + fill] = ' ';
                _styles[y][col + fill] = style;
            }

            col += cells;
        }
    }

    public string PaintAnsi()
    {
        var buffer = new StringBuilder();
        buffer.Append("\u001b[H\u001b[2J");
        for (var y = 0; y < RowsCount; y++)
        {
            buffer.Append("\u001b[").Append(y + 1).Append(";1H");
            var current = CellPaintStyle.None;
            for (var x = 0; x < Cols; x++)
            {
                var style = _styles[y][x];
                if (style != current)
                {
                    buffer.Append("\u001b[0m");
                    if (style == CellPaintStyle.Bold)
                        buffer.Append("\u001b[1m");
                    else if (style == CellPaintStyle.Dim)
                        buffer.Append("\u001b[2m");
                    current = style;
                }

                buffer.Append(_cells[y][x]);
            }

            if (current != CellPaintStyle.None)
                buffer.Append("\u001b[0m");
        }

        if (CursorCol is { } cursorCol && CursorRow is { } cursorRow)
        {
            buffer.Append("\u001b[")
                .Append(cursorRow + 1)
                .Append(';')
                .Append(cursorCol + 1)
                .Append('H');
        }

        return buffer.ToString();
    }
}
