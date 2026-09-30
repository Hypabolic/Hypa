namespace Hypa.AgentRuntime.Application;

/// <summary>
// / One flat row-major cell store.
/// <c>src/protocol/wire.rs:731-744</c> <c>FrameData</c> holds
/// <c>cells: Vec&lt;CellData&gt;</c> in row-major order with
/// <c>width</c> and <c>height</c>. Length must equal
/// <c>width * height</c>.
/// </summary>
public sealed class VtFrameGrid
{
    public VtFrameCell[] Cells { get; }

    public VtFrameTables Tables { get; }

    public int Cols { get; }

    public int Rows { get; }

    public VtFrameGrid(VtFrameCell[] cells, VtFrameTables tables, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        // length is not width * height.
        if (cells.Length != cols * rows)
            throw new ArgumentException("Cell array length must equal cols * rows.", nameof(cells));
        Cells = cells;
        Tables = tables;
        Cols = cols;
        Rows = rows;
    }

    /// <summary>
    /// <c>let idx = (row as usize) * (self.width as usize) + (col as usize)</c>.
    /// </summary>
    public int Index(int row, int col) => (row * Cols) + col;

    public VtFrameCell PackedAt(int row, int col)
    {
        if ((uint)row >= (uint)Rows || (uint)col >= (uint)Cols)
            return VtFrameCell.Blank;
        return Cells[(row * Cols) + col];
    }

    public VtCellView ViewAt(int row, int col)
    {
        if ((uint)row >= (uint)Rows || (uint)col >= (uint)Cols)
            return VtCellView.Blank;
        return Tables.Resolve(Cells[(row * Cols) + col]);
    }
}

/// <summary>Transient row views over a flat grid. Never retained.</summary>
public sealed class VtViewRows : IReadOnlyList<IReadOnlyList<VtCellView>>
{
    private readonly VtFrameGrid _grid;

    public VtViewRows(VtFrameGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        _grid = grid;
    }

    public IReadOnlyList<VtCellView> this[int index] => new VtViewRow(_grid, index);

    public int Count => _grid.Rows;

    public IEnumerator<IReadOnlyList<VtCellView>> GetEnumerator()
    {
        for (var r = 0; r < _grid.Rows; r++)
            yield return new VtViewRow(_grid, r);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Transient row view over a flat grid. Never retained.</summary>
public sealed class VtViewRow : IReadOnlyList<VtCellView>
{
    private readonly VtFrameGrid _grid;
    private readonly int _row;

    public VtViewRow(VtFrameGrid grid, int row)
    {
        ArgumentNullException.ThrowIfNull(grid);
        _grid = grid;
        _row = row;
    }

    public VtCellView this[int index] => _grid.ViewAt(_row, index);

    public int Count => _grid.Cols;

    public IEnumerator<VtCellView> GetEnumerator()
    {
        for (var c = 0; c < _grid.Cols; c++)
            yield return _grid.ViewAt(_row, c);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
