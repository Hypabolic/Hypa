using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Copy;

namespace Hypa.Cli.Attach;

/// <summary>
/// One host-size cell buffer. Chrome and pane cells share this frame before
// / encode.
/// cells land at <c>src/pane/terminal.rs:2170</c>
/// <c>buf[(area.x + x, area.y + y)]</c>.
/// </summary>
internal sealed class HostFrame
{
    private AssembledCell[] _cells = [];
    private bool[] _rowDirty = [];

    public int Cols { get; private set; }

    public int Rows { get; private set; }

    public HostCursor Cursor { get; set; }

    public int CellCount => _cells.Length;

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows && _cells.Length == cols * rows)
            return;
        Cols = cols;
        Rows = rows;
        _cells = new AssembledCell[cols * rows];
        _rowDirty = new bool[rows];
        for (var i = 0; i < _cells.Length; i++)
            _cells[i] = AssembledCell.Blank;
        for (var r = 0; r < rows; r++)
            _rowDirty[r] = true;
        Cursor = default;
    }

    public void Clear()
    {
        for (var i = 0; i < _cells.Length; i++)
            _cells[i] = AssembledCell.Blank;
        for (var r = 0; r < _rowDirty.Length; r++)
            _rowDirty[r] = true;
        Cursor = default;
    }

    /// <summary>
    /// to every host cell before the modal paints.
    /// </summary>
    public void DimAll()
    {
        for (var i = 0; i < _cells.Length; i++)
        {
            var cell = _cells[i];
            if (cell.Style.Dim)
                continue;
            _cells[i] = cell with { Style = cell.Style with { Dim = true } };
            _rowDirty[i / Cols] = true;
        }
    }

    public void Invalidate()
    {
        for (var r = 0; r < _rowDirty.Length; r++)
            _rowDirty[r] = true;
    }

    public AssembledCell CellAt(int col, int row)
    {
        if ((uint)col >= (uint)Cols || (uint)row >= (uint)Rows)
            return AssembledCell.Blank;
        return _cells[row * Cols + col];
    }

    public void Stamp(int col, int row, AssembledCell cell)
    {
        if ((uint)col >= (uint)Cols || (uint)row >= (uint)Rows)
            return;
        var i = row * Cols + col;
        if (CellsEqual(_cells[i], cell))
            return;
        _cells[i] = cell;
        _rowDirty[row] = true;
    }

    /// <summary>
    /// Stamp a chrome run by grapheme and display width, then mark
    // / continuation cells.
    /// <c>src/protocol/render_ansi.rs:524-528</c> <c>cell_width</c> plus skip
    /// columns at <c>639-650</c>. <paramref name="maxCols"/> is the destination
    /// band, not the host width.
    /// </summary>
    public void StampGlyphRun(int col, int row, string text, AssembledStyle style, int maxCols = -1)
    {
        if ((uint)row >= (uint)Rows || string.IsNullOrEmpty(text))
            return;
        if (col >= Cols || maxCols == 0)
            return;
        var encoded = SafeDisplayText.Encode(text);
        if (encoded.Length == 0)
            return;

        var x = col;
        var end = Cols;
        if (maxCols > 0 && col >= 0)
        {
            var bandEnd = col + maxCols;
            if (bandEnd >= col && bandEnd < end)
                end = bandEnd;
        }

        foreach (var grapheme in SafeDisplayText.EnumerateGraphemes(encoded))
        {
            var width = SafeDisplayText.Width(grapheme);
            if (width <= 0)
                continue;
            if (x < 0)
            {
                x += width;
                continue;
            }

            if (x + width > end)
                break;
            Stamp(x, row, new AssembledCell(grapheme, width, false, style));
            for (var i = 1; i < width; i++)
                Stamp(x + i, row, new AssembledCell("", 1, true, style));
            x += width;
        }
    }

    public void StampSnapshot(AssembledSnapshot snapshot, CellRect box, CellRect? occlude = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (box.Cols < 1 || box.Rows < 1)
            return;
        var maxRows = Math.Min(snapshot.Rows, box.Rows);
        var maxCols = Math.Min(snapshot.Cols, box.Cols);
        for (var r = 0; r < box.Rows; r++)
        {
            var hostRow = box.Row + r;
            if ((uint)hostRow >= (uint)Rows)
                break;
            for (var c = 0; c < box.Cols; c++)
            {
                var hostCol = box.Col + c;
                if ((uint)hostCol >= (uint)Cols)
                    break;
                if (occlude is { } occ && occ.Contains(hostCol, hostRow))
                    continue;
                var cell = r < maxRows && c < maxCols
                    ? snapshot.CellAt(c, r)
                    : AssembledCell.Blank;
                if (cell.Width > 1 && c + cell.Width > maxCols)
                    cell = AssembledCell.Blank;
                Stamp(hostCol, hostRow, cell);
            }
        }
    }

    /// <summary>
    /// <c>src/pane/terminal.rs:2170</c> <c>inner_rect</c>). Clip a spilling
    /// wide primary like <see cref="StampSnapshot"/>. Mark skip columns like
    /// <c>src/protocol/render_ansi.rs:639-667</c>.
    /// </summary>
    public void StampCopyView(CopyModePaintSnapshot view, CellRect box)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (box.Cols < 1 || box.Rows < 1)
            return;
        var top = Math.Max(0, view.ViewportTop);
        for (var i = 0; i < box.Rows; i++)
        {
            var srcRow = top + i;
            var hostRow = box.Row + i;
            if ((uint)hostRow >= (uint)Rows)
                break;
            var line = srcRow < view.RowCount && srcRow < view.Lines.Count
                ? view.Lines[srcRow]
                : null;
            var written = 0;
            if (line is not null)
            {
                for (var c = 0; c < line.Count && written < box.Cols; c++)
                {
                    var cell = line[c];
                    if (cell.IsContinuation)
                        continue;
                    var width = cell.Width > 0 ? cell.Width : 1;
                    if (width > 1 && written + width > box.Cols)
                    {
                        Stamp(box.Col + written, hostRow, AssembledCell.Blank);
                        written++;
                        continue;
                    }

                    Stamp(
                        box.Col + written,
                        hostRow,
                        new AssembledCell(cell.Text, width, false, cell.Style));
                    for (var k = 1; k < width; k++)
                    {
                        Stamp(
                            box.Col + written + k,
                            hostRow,
                            new AssembledCell("", 1, true, cell.Style));
                    }

                    written += width;
                }
            }

            while (written < box.Cols)
            {
                Stamp(box.Col + written, hostRow, AssembledCell.Blank);
                written++;
            }
        }
    }

    public void MarkRowDirty(int row)
    {
        if ((uint)row < (uint)_rowDirty.Length)
            _rowDirty[row] = true;
    }

    public bool IsRowDirty(int row) =>
        (uint)row < (uint)_rowDirty.Length && _rowDirty[row];

    public void ClearDirty()
    {
        for (var r = 0; r < _rowDirty.Length; r++)
            _rowDirty[r] = false;
    }

    /// <summary>
    // / Copy cells into encoder-owned storage.
    /// <c>src/protocol/render_ansi.rs:128-132</c> moves <c>FrameData</c> on
    /// commit. Hypa attach reuses this frame, so commit copies.
    /// </summary>
    internal void CopyCellsInto(ref AssembledCell[] dest, out int cols, out int rows)
    {
        cols = Cols;
        rows = Rows;
        if (dest.Length != _cells.Length)
            dest = _cells.Length == 0 ? [] : new AssembledCell[_cells.Length];
        if (_cells.Length > 0)
            Array.Copy(_cells, dest, _cells.Length);
    }

    public void CopyFrom(HostFrame source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.CopyCellsInto(ref _cells, out var cols, out var rows);
        Cols = cols;
        Rows = rows;
        if (_rowDirty.Length != rows)
            _rowDirty = rows == 0 ? [] : new bool[rows];
        Cursor = source.Cursor;
    }

    /// <summary>
    /// Stream extract in host cells. Trailing blanks drop per row, same
    /// as <c>CopyModeBuffer.Extract</c>.
    /// </summary>
    public string ExtractRange(int aCol, int aRow, int bCol, int bRow)
    {
        NormalizeHostRange(aCol, aRow, bCol, bRow, out var c1, out var r1, out var c2, out var r2);
        if (Cols < 1 || Rows < 1)
            return "";
        c1 = Math.Clamp(c1, 0, Cols - 1);
        c2 = Math.Clamp(c2, 0, Cols - 1);
        r1 = Math.Clamp(r1, 0, Rows - 1);
        r2 = Math.Clamp(r2, 0, Rows - 1);
        var sb = new System.Text.StringBuilder();
        for (var row = r1; row <= r2; row++)
        {
            if (row > r1)
                sb.Append('\n');
            var start = row == r1 ? c1 : 0;
            var end = row == r2 ? c2 : Cols - 1;
            var last = start - 1;
            for (var col = start; col <= end; col++)
            {
                var cell = CellAt(col, row);
                if (cell.IsContinuation)
                    continue;
                if (!IsBlankGlyph(cell.Text))
                    last = col;
            }

            if (last < start)
                continue;
            for (var col = start; col <= last; col++)
            {
                var cell = CellAt(col, row);
                if (cell.IsContinuation)
                    continue;
                sb.Append(cell.Text);
            }
        }

        return sb.ToString();
    }

    public void StampInverseRange(int aCol, int aRow, int bCol, int bRow)
    {
        NormalizeHostRange(aCol, aRow, bCol, bRow, out var c1, out var r1, out var c2, out var r2);
        if (Cols < 1 || Rows < 1)
            return;
        c1 = Math.Clamp(c1, 0, Cols - 1);
        c2 = Math.Clamp(c2, 0, Cols - 1);
        r1 = Math.Clamp(r1, 0, Rows - 1);
        r2 = Math.Clamp(r2, 0, Rows - 1);
        for (var row = r1; row <= r2; row++)
        {
            var start = row == r1 ? c1 : 0;
            var end = row == r2 ? c2 : Cols - 1;
            for (var col = start; col <= end; col++)
            {
                var existing = CellAt(col, row);
                Stamp(col, row, existing with { Style = existing.Style with { Inverse = true } });
            }
        }
    }

    internal static void NormalizeHostRange(
        int aCol,
        int aRow,
        int bCol,
        int bRow,
        out int c1,
        out int r1,
        out int c2,
        out int r2)
    {
        if (aRow < bRow || (aRow == bRow && aCol <= bCol))
        {
            c1 = aCol;
            r1 = aRow;
            c2 = bCol;
            r2 = bRow;
            return;
        }

        c1 = bCol;
        r1 = bRow;
        c2 = aCol;
        r2 = aRow;
    }

    private static bool IsBlankGlyph(string text)
    {
        if (string.IsNullOrEmpty(text))
            return true;
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
                return false;
        }

        return true;
    }

    internal static bool CellsEqual(AssembledCell a, AssembledCell b) =>
        a.Text == b.Text
        && a.Width == b.Width
        && a.IsContinuation == b.IsContinuation
        && StylesEqual(a.Style, b.Style);

    internal static bool StylesEqual(AssembledStyle a, AssembledStyle b) =>
        a.Fg == b.Fg
        && a.Bg == b.Bg
        && a.Bold == b.Bold
        && a.Dim == b.Dim
        && a.Italic == b.Italic
        && a.Underline == b.Underline
        && a.Inverse == b.Inverse
        && a.Invisible == b.Invisible
        && a.Strikethrough == b.Strikethrough
        && a.Blink == b.Blink
        && a.Overline == b.Overline
        && a.UnderlineColor == b.UnderlineColor
        && a.UnderlineStyle == b.UnderlineStyle
        && a.Hyperlink == b.Hyperlink;
}

internal readonly record struct HostCursor(int Col, int Row, bool Visible, int Shape = 0, bool HasCursor = true)
{
    /// <summary>
    // Encoder parks last-visible,
    /// else <c>default_hidden_cursor_position</c> at
    /// <c>src/protocol/render_ansi.rs:593-598</c>. Distinct from Some
    /// hidden at host origin. <c>write_host_cursor_state</c> at
    /// <c>render_ansi.rs:612-623</c> always CUPs the parked cell.
    /// </summary>
    public static HostCursor None { get; } = new(0, 0, false, 0, HasCursor: false);
}
