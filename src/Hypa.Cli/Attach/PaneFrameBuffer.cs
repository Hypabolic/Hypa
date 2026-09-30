using System.Collections;

namespace Hypa.Cli.Attach;

/// <summary>
// / Reused row-major pane grid.
/// <c>FrameData.cells</c> is one <c>Vec&lt;CellData&gt;</c>. Size-stable
/// paints overwrite in place. Do not allocate a new jagged grid per apply.
/// </summary>
internal static class PaneFrameBuffer
{
    public static AssembledCell[] Rent(
        int cols,
        int rows,
        AssembledCell[]? previous,
        int previousCols,
        int previousRows,
        bool fillBlank)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var n = cols * rows;
        AssembledCell[] storage;
        if (previous is not null
            && previous.Length == n
            && previousCols == cols
            && previousRows == rows)
        {
            storage = previous;
        }
        else
        {
            storage = new AssembledCell[n];
            fillBlank = true;
        }

        if (fillBlank)
            Array.Fill(storage, AssembledCell.Blank);
        return storage;
    }

    public static IReadOnlyList<IReadOnlyList<AssembledCell>> Rows(
        AssembledCell[] storage,
        int cols,
        int rows)
    {
        var list = new AssembledRow[Math.Max(0, rows)];
        for (var r = 0; r < list.Length; r++)
            list[r] = new AssembledRow(storage, r * cols, cols);
        return list;
    }

    public static void WriteCell(AssembledCell[] storage, int cols, int col, int row, AssembledCell cell)
    {
        if ((uint)col >= (uint)cols)
            return;
        var i = row * cols + col;
        if ((uint)i >= (uint)storage.Length)
            return;
        storage[i] = cell;
    }
}

internal sealed class AssembledRow : IReadOnlyList<AssembledCell>
{
    private readonly AssembledCell[] _storage;
    private readonly int _offset;
    private readonly int _count;

    public AssembledRow(AssembledCell[] storage, int offset, int count)
    {
        _storage = storage;
        _offset = offset;
        _count = count;
    }

    public AssembledCell this[int index] => _storage[_offset + index];

    public int Count => _count;

    public IEnumerator<AssembledCell> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
            yield return _storage[_offset + i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
