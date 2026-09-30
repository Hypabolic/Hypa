using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach;

/// <summary>
// / Stateful host encoder.
/// <c>BlitEncoder</c> at 57-61, encode at 80-125 (<c>let mut bytes = Vec::new()</c>
/// at 95; borrow <c>&amp;FrameData</c> at 80-125, no grid clone), blit order at
/// 464-500, CUP at 607-610, commit at 128-132.
/// </summary>
internal sealed class HostBlitEncoder
{
    public const string SyncBegin = "\u001b[?2026h";
    public const string SyncEnd = "\u001b[?2026l";
    public const string Osc8Reset = "\u001b]8;;\u001b\\";

    private readonly ArrayBufferWriter<byte> _bytes = new(1024);
    private byte[] _stableA = [];
    private byte[] _stableB = [];
    private bool _stableFlip;
    private AssembledCell[] _lastCells = [];
    private int _lastCols;
    private int _lastRows;
    private bool _hasLast;
    private (int Col, int Row)? _lastVisibleCursor;
    private int _lastCursorShape;
    private bool _repaintPending;

    public bool HasBaseline => _hasLast;

    public void Invalidate()
    {
        _hasLast = false;
        _lastCells = [];
        _lastCols = 0;
        _lastRows = 0;
        _lastVisibleCursor = null;
        _lastCursorShape = 0;
        _repaintPending = true;
    }

    public void RequestRepaint() => _repaintPending = true;

    public EncodedHostBlit Encode(HostFrame frame, bool repaint = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var forceRepaint = repaint || _repaintPending;
        var full = forceRepaint
            || !_hasLast
            || _lastCols != frame.Cols
            || _lastRows != frame.Rows;
        var clearBeforeFull = !_hasLast;
        _bytes.Clear();
        WriteAscii(_bytes, "\x1b[?2026h"u8);
        WriteAscii(_bytes, "\x1b[?25l"u8);
        WriteAscii(_bytes, "\x1b]8;;\x1b\\"u8);
        if (full)
        {
            if (clearBeforeFull)
                WriteAscii(_bytes, "\x1b[2J"u8);
            WriteAllCells(_bytes, frame);
        }
        else
            WriteChangedCells(_bytes, frame, _lastCells, _lastCols);

        var nextVisible = _lastVisibleCursor;
        var nextShape = _lastCursorShape;
        WriteHostCursor(_bytes, frame, ref nextVisible, ref nextShape);
        WriteAscii(_bytes, "\x1b[?2026l"u8);
        // Hypa reuses
        // the writer, then copies into a ping-pong emit buffer so a nested
        // encode cannot invalidate the in-flight write span.
        // src/client/mod.rs:1698-1700 writes then commit.
        return new EncodedHostBlit(
            StabilizeWritten(),
            full,
            nextVisible,
            nextShape);
    }

    private ReadOnlyMemory<byte> StabilizeWritten()
    {
        var n = _bytes.WrittenCount;
        ref var dest = ref (_stableFlip ? ref _stableB : ref _stableA);
        _stableFlip = !_stableFlip;
        if (dest.Length < n)
            dest = new byte[n];
        if (n > 0)
            _bytes.WrittenSpan.CopyTo(dest);
        return dest.AsMemory(0, n);
    }

    public void Commit(HostFrame frame, EncodedHostBlit encoded)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _lastVisibleCursor = encoded.NextLastVisibleCursor;
        _lastCursorShape = encoded.NextLastCursorShape;
        frame.CopyCellsInto(ref _lastCells, out _lastCols, out _lastRows);
        _hasLast = true;
        _repaintPending = false;
    }

    private static void WriteAllCells(IBufferWriter<byte> w, HostFrame frame)
    {
        AssembledStyle? current = null;
        string? activeHyperlink = null;
        for (var r = 0; r < frame.Rows; r++)
        {
            // to_skip from cell_width; CUP when the next cell is not inline.
            var toSkip = 0;
            int? nextInlineCol = null;
            for (var c = 0; c < frame.Cols; c++)
            {
                if (toSkip > 0)
                {
                    toSkip--;
                    continue;
                }

                var cell = frame.CellAt(c, r);
                var width = VisualWidth(cell);
                var skip = cell.IsContinuation
                    || (cell.Width > 0 && c + cell.Width > frame.Cols);
                if (skip)
                {
                    nextInlineCol = null;
                    continue;
                }

                if (nextInlineCol != c)
                {
                    WriteCursorAddress(w, c, r);
                    current = null;
                }

                AppendCell(w, cell, ref current, ref activeHyperlink);
                nextInlineCol = cell.Text is { Length: 1 } glyph && char.IsAscii(glyph[0]) && width == 1
                    ? c + 1
                    : null;
                toSkip = Math.Max(0, width - 1);
            }
        }

        CellSgrEncoder.CloseHyperlink(w, ref activeHyperlink);
        WriteAscii(w, "\x1b[0m"u8);
    }

    private static void WriteChangedCells(
        IBufferWriter<byte> w,
        HostFrame frame,
        AssembledCell[] prev,
        int prevCols)
    {
        AssembledStyle? current = null;
        string? activeHyperlink = null;
        // every host cell versus last frame. There is no row-dirty skip.
        for (var r = 0; r < frame.Rows; r++)
        {
            var invalidated = 0;
            var toSkip = 0;
            int? nextInlineCol = null;
            for (var c = 0; c < frame.Cols; c++)
            {
                var cell = frame.CellAt(c, r);
                var prevCell = prev[r * prevCols + c];
                var skip = cell.IsContinuation
                    || (cell.Width > 0 && c + cell.Width > frame.Cols);
                var width = VisualWidth(cell);
                if (!skip
                    && toSkip == 0
                    && (invalidated > 0 || !HostFrame.CellsEqual(cell, prevCell)))
                {
                    if (nextInlineCol != c || invalidated > 0)
                    {
                        WriteCursorAddress(w, c, r);
                        current = null;
                    }

                    AppendCell(w, cell, ref current, ref activeHyperlink);
                    nextInlineCol = cell.Text is { Length: 1 } glyph && char.IsAscii(glyph[0]) && width == 1
                        ? c + 1
                        : null;
                }
                else if (skip || toSkip > 0)
                {
                    current = null;
                    nextInlineCol = null;
                }
                else
                    nextInlineCol = null;

                toSkip = Math.Max(0, width - 1);
                var affected = Math.Max(width, VisualWidth(prevCell));
                invalidated = Math.Max(affected, invalidated) - 1;
                if (invalidated < 0)
                    invalidated = 0;
            }
        }

        CellSgrEncoder.CloseHyperlink(w, ref activeHyperlink);
    }

    private static void AppendCell(
        IBufferWriter<byte> w,
        AssembledCell cell,
        ref AssembledStyle? current,
        ref string? activeHyperlink)
    {
        if (current is not { } liveStyle || !HostFrame.StylesEqual(liveStyle, cell.Style))
        {
            CellSgrEncoder.CloseHyperlink(w, ref activeHyperlink);
            WriteAscii(w, "\x1b[0m"u8);
            CellSgrEncoder.AppendSgr(w, cell.Style.ToSgr());
            current = cell.Style;
        }

        CellSgrEncoder.WriteHyperlinkIfChanged(w, ref activeHyperlink, cell.Style.Hyperlink);
        var text = string.IsNullOrEmpty(cell.Text) ? " " : cell.Text;
        WriteChars(w, text);
    }

    private static int VisualWidth(AssembledCell cell) =>
        cell.Width > 0 ? cell.Width : 1;

    private static void WriteHostCursor(
        IBufferWriter<byte> w,
        HostFrame frame,
        ref (int Col, int Row)? lastVisible,
        ref int lastShape)
    {
        // Some visible: CUP, show, remember last-visible.
        // Some hidden (including 0,0): CUP to that host cell, hide.
        // None parks last-visible, else default_hidden_cursor_position at
        // render_ansi.rs:593-598 (width-1, height-1). write_host_cursor_state
        // at 612-623 always CUPs that parked position, then hide.
        var cursor = frame.Cursor;
        int col;
        int row;
        bool visible;
        int shape;
        if (!cursor.HasCursor)
        {
            if (lastVisible is { } parked)
            {
                col = Math.Clamp(parked.Col, 0, Math.Max(0, frame.Cols - 1));
                row = Math.Clamp(parked.Row, 0, Math.Max(0, frame.Rows - 1));
            }
            else
            {
                col = Math.Max(0, frame.Cols - 1);
                row = Math.Max(0, frame.Rows - 1);
            }

            visible = false;
            shape = 0;
        }
        else
        {
            col = Math.Clamp(cursor.Col, 0, Math.Max(0, frame.Cols - 1));
            row = Math.Clamp(cursor.Row, 0, Math.Max(0, frame.Rows - 1));
            visible = cursor.Visible;
            shape = Math.Clamp(cursor.Shape, 0, 6);
            if (visible)
                lastVisible = (col, row);
        }

        WriteCursorAddress(w, col, row);
        if (shape != lastShape)
        {
            WriteAscii(w, "\x1b["u8);
            WriteInt(w, shape);
            WriteAscii(w, " q"u8);
            lastShape = shape;
        }

        WriteAscii(w, visible ? "\x1b[?25h"u8 : "\x1b[?25l"u8);
    }

    private static void WriteCursorAddress(IBufferWriter<byte> w, int col, int row)
    {
        WriteAscii(w, "\x1b["u8);
        WriteInt(w, row + 1);
        WriteAscii(w, ";"u8);
        WriteInt(w, col + 1);
        WriteAscii(w, "H"u8);
    }

    private static void WriteAscii(IBufferWriter<byte> w, ReadOnlySpan<byte> ascii)
    {
        var span = w.GetSpan(ascii.Length);
        ascii.CopyTo(span);
        w.Advance(ascii.Length);
    }

    private static void WriteInt(IBufferWriter<byte> w, int value)
    {
        var span = w.GetSpan(11);
        if (!Utf8Formatter.TryFormat(value, span, out var written))
            throw new InvalidOperationException("CUP digit write failed.");
        w.Advance(written);
    }

    private static void WriteChars(IBufferWriter<byte> w, ReadOnlySpan<char> text)
    {
        var max = Encoding.UTF8.GetMaxByteCount(text.Length);
        var span = w.GetSpan(max);
        var written = Encoding.UTF8.GetBytes(text, span);
        w.Advance(written);
    }
}

internal readonly record struct EncodedHostBlit(
    ReadOnlyMemory<byte> Utf8,
    bool Full,
    (int Col, int Row)? NextLastVisibleCursor,
    int NextLastCursorShape)
{
    public ReadOnlySpan<byte> Span => Utf8.Span;

    public byte[] Bytes => Utf8.ToArray();
}
