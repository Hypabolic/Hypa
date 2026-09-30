namespace Hypa.AgentRuntime.Application;

/// <summary>
/// One sealed history block. Rows are packed cells. The codec
/// compresses the raw <c>ulong</c> cell bytes. Tables stay uncompressed.
/// A block never reflows. It keeps its own column count.
/// </summary>
public sealed record PaneHistoryBlock(
    long FirstRowIndex,
    int Cols,
    int RowCount,
    VtFrameTables Tables,
    byte[] CompressedCells);

/// <summary>One stored row plus the tables that resolve its packed cells.</summary>
public sealed record PaneHistoryRow(
    long Index,
    int Cols,
    VtFrameCell[] Cells,
    VtFrameTables Tables);

/// <summary>A contiguous read from the store. A miss is a failed result.</summary>
public sealed record PaneHistoryRead(IReadOnlyList<PaneHistoryRow> Rows);
