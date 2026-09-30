using System.Text;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Typed cell redaction shared by the snapshot route and the packed-cells
/// first-observe route. Same rules and same order as the former
/// <c>JsonNode</c> redact: walk the grid in row-major order, skip a
/// continuation cell, join the text with no newline, run the stream-keyed
/// redactor, flush the stream, and write back by original text length.
/// </summary>
public static class AttachFrameRedactor
{
    /// <summary>
    /// Redact cell text on the typed frame. When the redacted text equals
    /// the raw text, returns the same frame instance and writes nothing
    /// back, so Ghostty graphemes stay intact. Otherwise builds one new
    /// <see cref="VtFrame"/> with a new grid and new tables. Never mutates
    /// the captured frame: the frame shares frozen tables with the pane
    /// and with the pane frame store.
    /// </summary>
    public static VtFrame RedactCells(string paneId, VtFrame frame, IEventPayloadRedactor redactor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(redactor);

        var cols = frame.Cols;
        var rows = frame.Rows;
        if (cols < 1 || rows < 1)
            return frame;

        var joined = new StringBuilder();
        var activeCount = 0;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var view = frame.CellAt(r, c);
                if (view.IsContinuation)
                    continue;
                joined.Append(view.Text);
                activeCount++;
            }
        }

        if (activeCount == 0)
            return frame;

        var raw = joined.ToString();
        var streamKey = AttachSnapshotPacker.SnapshotStreamKey(paneId);
        var emitted = redactor.RedactTerminalBytes(streamKey, Encoding.UTF8.GetBytes(raw));
        var flushed = redactor.FlushTerminalStream(streamKey);
        var redacted = Encoding.UTF8.GetString(ConcatUtf8(emitted, flushed).Span);

        if (string.Equals(redacted, raw, StringComparison.Ordinal))
            return frame;

        var tables = new VtStampTables();
        var cells = new VtFrameCell[cols * rows];
        var offset = 0;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var view = frame.CellAt(r, c);
                var text = view.Text;
                if (!view.IsContinuation)
                {
                    var n = text.Length;
                    if (n < 1)
                        n = 1;
                    if (offset >= redacted.Length)
                    {
                        text = " ";
                    }
                    else
                    {
                        var take = Math.Min(n, redacted.Length - offset);
                        text = redacted.Substring(offset, take);
                        offset += take;
                    }
                }

                var style = new VtPackedStyle(
                    view.FgPacked,
                    view.BgPacked,
                    view.UnderlineColorPacked,
                    view.Modifier,
                    view.UnderlineStylePacked);
                cells[(r * cols) + c] = tables.PackCell(
                    text, view.Width, view.IsContinuation, in style, view.Hyperlink);
            }
        }

        return new VtFrame(
            frame.PaneId,
            frame.Cols,
            frame.Rows,
            new VtFrameGrid(cells, tables.Freeze(null), cols, rows),
            frame.Cursor,
            frame.Modes,
            frame.ViewportOrigin,
            frame.OccupantGeneration,
            frame.Generation,
            frame.Provider,
            frame.ActiveScreen);
    }

    private static ReadOnlyMemory<byte> ConcatUtf8(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second)
    {
        if (first.IsEmpty)
            return second;
        if (second.IsEmpty)
            return first;

        var buf = new byte[first.Length + second.Length];
        first.Span.CopyTo(buf);
        second.Span.CopyTo(buf.AsSpan(first.Length));
        return buf;
    }
}
