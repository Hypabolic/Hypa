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
    // The open rows, one after the other. The block is compressed from this
    // buffer, and the buffer is used again for the next block.
    private VtFrameCell[] _openCells = [];
    private int _openRowCount;
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

    public void AppendRows(ReadOnlySpan<VtFrameCell[]> rows, int cols, VtFrameTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (rows.IsEmpty)
                return;
            if (_openRowCount > 0 && (cols != _openCols || !ReferenceEquals(tables, _openFrozen)))
                SealOpenBlockUnlocked();

            foreach (var row in rows)
            {
                ArgumentNullException.ThrowIfNull(row);
                if (_openRowCount == 0)
                {
                    _openCols = cols;
                    _openFrozen = tables;
                }

                var target = OpenRowUnlocked(_openRowCount);
                var take = Math.Min(row.Length, cols);
                row.AsSpan(0, take).CopyTo(target);
                target[take..].Fill(VtFrameCell.Blank);
                _openRowCount++;
                _nextRowIndex++;
                if (_openRowCount >= _blockRows)
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

            if (remaining > 0 && _openRowCount > 0)
            {
                var openStart = _nextRowIndex - _openRowCount;
                if (index >= openStart && index < _nextRowIndex)
                {
                    var offset = (int)(index - openStart);
                    for (var i = offset; i < _openRowCount && remaining > 0; i++)
                    {
                        rows.Add(new PaneHistoryRow(openStart + i, _openCols, OpenRowUnlocked(i).ToArray(), _openFrozen));
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
            _openCells = [];
            _openRowCount = 0;
            _compressedBytes = 0;
        }
    }

    private void SealOpenBlockUnlocked()
    {
        if (_openRowCount == 0)
            return;

        var rowCount = _openRowCount;
        var cols = _openCols;
        var compressed = _codec.Compress(_openCells.AsSpan(0, rowCount * cols));
        var payload = compressed.IsOk ? compressed.Value : Array.Empty<byte>();
        var first = _nextRowIndex - rowCount;
        var block = new PaneHistoryBlock(first, cols, rowCount, _openFrozen, payload);
        _blocks.Add(block);
        _compressedBytes += payload.LongLength;
        _openRowCount = 0;
        _openFrozen = VtFrameTables.Empty;
        DropOverflowUnlocked();
    }

    // The cells of open row <paramref name="row"/>. The buffer grows by
    // doubling up to one block, so a quiet pane keeps a small buffer.
    private Span<VtFrameCell> OpenRowUnlocked(int row)
    {
        var end = (row + 1) * _openCols;
        if (end > _openCells.Length)
        {
            var size = Math.Max(end, Math.Min(_openCells.Length * 2, _blockRows * _openCols));
            Array.Resize(ref _openCells, size);
        }

        return _openCells.AsSpan(row * _openCols, _openCols);
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
