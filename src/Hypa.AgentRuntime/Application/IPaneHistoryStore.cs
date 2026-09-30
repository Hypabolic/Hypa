namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Mux-side archive of rows that Ghostty has discarded.
/// <c>src/pane/terminal/windows_recent_fallback.rs:3-12</c> keeps a
/// bounded side cache and reads it for text only
/// (<c>src/pane/terminal.rs:2736-2744</c>). Hypa stores packed cells.
/// The store does not answer a live paint. The store does not answer a
/// wheel paint inside the Ghostty range.
/// </summary>
public interface IPaneHistoryStore : IDisposable
{
    int RowCount { get; }

    long CompressedByteCount { get; }

    long FirstRowIndex { get; }

    long NextRowIndex { get; }

    int Cols { get; }

    void AppendRows(IReadOnlyList<VtFrameCell[]> rows, int cols, VtFrameTables tables);

    void SealOpenBlock();

    PaneHistoryResult<PaneHistoryRead> TryReadRows(long startIndex, int count);
}
