using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Test stamp destination for the flat live buffer. Holds the reused flat
/// packed array beside the stamp tables, and resolves row views on read.
/// </summary>
internal sealed class StampBuffer
{
    private VtFrameTables? _frozen;

    public StampBuffer(int cols, int rows)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        Cells = new VtFrameCell[Cols * Rows];
    }

    public VtFrameCell[] Cells { get; }

    public VtStampTables Tables { get; } = new();

    public int Cols { get; }

    public int Rows { get; }

    public StampRow this[int row] => new(this, row);

    internal VtCellView ViewAt(int row, int col)
    {
        _frozen = Tables.Freeze(_frozen);
        if ((uint)row >= (uint)Rows || (uint)col >= (uint)Cols)
            return VtCellView.Blank;
        return _frozen.Resolve(Cells[(row * Cols) + col]);
    }

    internal sealed class StampRow
    {
        private readonly StampBuffer _owner;
        private readonly int _row;

        public StampRow(StampBuffer owner, int row)
        {
            _owner = owner;
            _row = row;
        }

        public VtCellView this[int col] => _owner.ViewAt(_row, col);
    }
}
