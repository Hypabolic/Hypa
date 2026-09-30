using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.History;

/// <summary>
/// Memory-only history archive. Oldest sealed block drops first when a
/// bound is met. Open rows stay readable. The store is not a VT grid.
/// </summary>
public sealed class InMemoryPaneHistoryStore : IPaneHistoryStore
{
    public const int DefaultBlockRows = 256;

    private readonly IPaneHistoryCodec _codec;
    private readonly int _rowBound;
    private readonly long _byteBound;
    private readonly int _blockRows;
    private readonly object _gate = new();
    private readonly List<PaneHistoryBlock> _blocks = [];
    private readonly List<VtFrameCell[]> _openRows = [];
    private VtFrameTables _openFrozen = VtFrameTables.Empty;
    private int _openCols;
    private long _firstRowIndex;
    private long _nextRowIndex;
    private long _compressedBytes;
    private bool _disposed;

    public InMemoryPaneHistoryStore(
        IPaneHistoryCodec codec,
        int rowBound,
        long byteBound,
        int blockRows = DefaultBlockRows)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        ArgumentOutOfRangeException.ThrowIfLessThan(rowBound, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteBound, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockRows, 1);
        _rowBound = rowBound;
        _byteBound = byteBound;
        _blockRows = blockRows;
    }

    public int RowCount
    {
        get
        {
            lock (_gate)
                return checked((int)Math.Min(_nextRowIndex - _firstRowIndex, int.MaxValue));
        }
    }

    public long CompressedByteCount
    {
        get
        {
            lock (_gate)
                return _compressedBytes;
        }
    }

    public long FirstRowIndex
    {
        get
        {
            lock (_gate)
                return _firstRowIndex;
        }
    }

    public long NextRowIndex
    {
        get
        {
            lock (_gate)
                return _nextRowIndex;
        }
    }

    public int Cols
    {
        get
        {
            lock (_gate)
                return _openCols;
        }
    }

    public void AppendRows(IReadOnlyList<VtFrameCell[]> rows, int cols, VtFrameTables tables)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (rows.Count == 0)
                return;
            if (_openRows.Count > 0 && (cols != _openCols || !ReferenceEquals(tables, _openFrozen)))
                SealOpenBlockUnlocked();

            if (_openRows.Count == 0)
            {
                _openCols = cols;
                _openFrozen = tables;
            }

            foreach (var row in rows)
            {
                ArgumentNullException.ThrowIfNull(row);
                var copy = new VtFrameCell[cols];
                var take = Math.Min(row.Length, cols);
                if (take > 0)
                    Array.Copy(row, copy, take);
                for (var i = take; i < cols; i++)
                    copy[i] = VtFrameCell.Blank;
                _openRows.Add(copy);
                _nextRowIndex++;
                if (_openRows.Count >= _blockRows)
                    SealOpenBlockUnlocked();
            }

            DropOverflowUnlocked();
        }
    }

    public void SealOpenBlock()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SealOpenBlockUnlocked();
        }
    }

    public PaneHistoryResult<PaneHistoryRead> TryReadRows(long startIndex, int count)
    {
        if (count < 1)
            return PaneHistoryResult<PaneHistoryRead>.Ok(new PaneHistoryRead([]));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (startIndex < _firstRowIndex || startIndex >= _nextRowIndex)
                return PaneHistoryResult<PaneHistoryRead>.Fail(PaneHistoryError.NotFound);

            var rows = new List<PaneHistoryRow>(count);
            var index = startIndex;
            var remaining = count;
            foreach (var block in _blocks)
            {
                if (remaining <= 0)
                    break;
                var blockEnd = block.FirstRowIndex + block.RowCount;
                if (index >= blockEnd || index + remaining <= block.FirstRowIndex)
                    continue;
                var decoded = DecodeBlockUnlocked(block);
                if (!decoded.IsOk)
                    return PaneHistoryResult<PaneHistoryRead>.Fail(decoded.Error);
                var cells = decoded.Value;
                var offset = (int)(index - block.FirstRowIndex);
                if (offset < 0)
                    offset = 0;
                for (var i = offset; i < block.RowCount && remaining > 0; i++)
                {
                    var rowCells = new VtFrameCell[block.Cols];
                    Array.Copy(cells, i * block.Cols, rowCells, 0, block.Cols);
                    rows.Add(new PaneHistoryRow(block.FirstRowIndex + i, block.Cols, rowCells, block.Tables));
                    index++;
                    remaining--;
                }
            }

            if (remaining > 0 && _openRows.Count > 0)
            {
                var openStart = _nextRowIndex - _openRows.Count;
                if (index >= openStart && index < _nextRowIndex)
                {
                    var offset = (int)(index - openStart);
                    for (var i = offset; i < _openRows.Count && remaining > 0; i++)
                    {
                        rows.Add(new PaneHistoryRow(openStart + i, _openCols, (VtFrameCell[])_openRows[i].Clone(), _openFrozen));
                        index++;
                        remaining--;
                    }
                }
            }

            if (rows.Count == 0 || rows[0].Index != startIndex)
                return PaneHistoryResult<PaneHistoryRead>.Fail(PaneHistoryError.NotFound);
            return PaneHistoryResult<PaneHistoryRead>.Ok(new PaneHistoryRead(rows));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            SealOpenBlockUnlocked();
            _blocks.Clear();
            _openRows.Clear();
            _compressedBytes = 0;
        }
    }

    private void SealOpenBlockUnlocked()
    {
        if (_openRows.Count == 0)
            return;

        var rowCount = _openRows.Count;
        var cols = _openCols;
        var cells = new VtFrameCell[rowCount * cols];
        for (var r = 0; r < rowCount; r++)
            Array.Copy(_openRows[r], 0, cells, r * cols, cols);

        var compressed = _codec.Compress(cells);
        var payload = compressed.IsOk ? compressed.Value : Array.Empty<byte>();
        var first = _nextRowIndex - rowCount;
        var block = new PaneHistoryBlock(first, cols, rowCount, _openFrozen, payload);
        _blocks.Add(block);
        _compressedBytes += payload.LongLength;
        _openRows.Clear();
        _openFrozen = VtFrameTables.Empty;
        DropOverflowUnlocked();
    }

    private PaneHistoryResult<VtFrameCell[]> DecodeBlockUnlocked(PaneHistoryBlock block)
    {
        var expected = block.RowCount * block.Cols;
        return _codec.Decompress(block.CompressedCells, expected);
    }

    private void DropOverflowUnlocked()
    {
        while (_blocks.Count > 0)
        {
            var rows = _nextRowIndex - _firstRowIndex;
            if (rows <= _rowBound && _compressedBytes <= _byteBound)
                break;
            var oldest = _blocks[0];
            _blocks.RemoveAt(0);
            _compressedBytes = Math.Max(0, _compressedBytes - oldest.CompressedCells.LongLength);
            _firstRowIndex = oldest.FirstRowIndex + oldest.RowCount;
            if (_firstRowIndex > _nextRowIndex)
                _firstRowIndex = _nextRowIndex;
        }
    }
}
