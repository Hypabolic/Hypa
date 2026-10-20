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

    // Repaint requests and the request count that a committed full encode
    // served. A repaint is pending while the two differ. A request can come
    // from outside the paint gate between an encode and its commit. The
    // request is one atomic increment, and a commit serves only the count
    // that its encode read, so a later request stays pending.
    private long _repaintRequested;
    private long _repaintServed;

    // The host cursor of the last commit: position, presence, visibility and
    // shape. A diff encode with no changed cell and this cursor is a no-op.
    private HostCursor? _committedCursor;

    public bool HasBaseline => _hasLast;

    /// <summary>
    /// The kept frame: what the host terminal shows after the last commit.
    /// False when there is no kept frame or the cell is outside it.
    /// </summary>
    internal bool TryGetKeptCell(int col, int row, out AssembledCell cell)
    {
        cell = AssembledCell.Blank;
        if (!_hasLast || (uint)col >= (uint)_lastCols || (uint)row >= (uint)_lastRows)
            return false;
        cell = _lastCells[row * _lastCols + col];
        return true;
    }

    public void Invalidate()
    {
        _hasLast = false;
        _lastCells = [];
        _lastCols = 0;
        _lastRows = 0;
        _lastVisibleCursor = null;
        _lastCursorShape = 0;
        _committedCursor = null;
        RequestRepaint();
    }

    /// <summary>
    /// Ask for a full rewrite of every host cell on the next encode. Any
    /// thread can call it.
    /// </summary>
    public void RequestRepaint() => Interlocked.Increment(ref _repaintRequested);

    /// <summary>True when the next encode must rewrite every host cell.</summary>
    public bool RepaintPending =>
        Interlocked.Read(ref _repaintRequested) != Interlocked.Read(ref _repaintServed);

    /// <summary>
    /// True when <see cref="EncodeRows"/> can diff <paramref name="frame"/>
    /// against the kept frame: there is a kept frame of the same size and no
    /// repaint is pending.
    /// </summary>
    public bool CanEncodeRows(HostFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _hasLast
            && !RepaintPending
            && _lastCols == frame.Cols
            && _lastRows == frame.Rows
            && _lastCells.Length == frame.Cols * frame.Rows;
    }

    /// <summary>
    /// Encode only the cells of <paramref name="spans"/> of
    /// <paramref name="frame"/> against the kept frame. The caller guarantees
    /// that each cell outside the spans equals the kept frame. The spans of
    /// one row merge into one span from the first to the last column. Each
    /// row is scanned at the full host width with the same rules as the full
    /// diff, but only a cell in its span is compared: a cell outside it is
    /// equal. Thus the bytes are the bytes of a diff of the full rows.
    /// Returns null when there is no kept frame to diff against.
    /// <see cref="Commit"/> then copies only these spans.
    /// herdr <c>encode_patch</c> and <c>blit_patch_to</c>
    /// (<c>src/protocol/render_ansi.rs:144-187</c>, <c>:606-671</c>).
    /// </summary>
    public EncodedHostBlit? EncodeRows(HostFrame frame, IReadOnlyList<HostRowSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(spans);
        if (!CanEncodeRows(frame))
            return null;
        var repaintSerial = Interlocked.Read(ref _repaintRequested);
        if (repaintSerial != Interlocked.Read(ref _repaintServed))
            return null;
        var merged = MergeSpans(spans, frame.Cols, frame.Rows);
        _bytes.Clear();
        WriteAscii(_bytes, "\x1b[?2026h"u8);
        WriteAscii(_bytes, "\x1b[?25l"u8);
        WriteAscii(_bytes, "\x1b]8;;\x1b\\"u8);
        AssembledStyle? current = null;
        string? activeHyperlink = null;
        var written = 0;
        foreach (var span in merged)
            written += WriteChangedRow(_bytes, frame, span.Row, _lastCells, ref current, ref activeHyperlink, span.Col, span.EndCol);
        CellSgrEncoder.CloseHyperlink(_bytes, ref activeHyperlink);
        var noOp = written == 0 && IsCommittedCursor(frame.Cursor);
        var nextVisible = _lastVisibleCursor;
        var nextShape = _lastCursorShape;
        WriteHostCursor(_bytes, frame, ref nextVisible, ref nextShape);
        WriteAscii(_bytes, "\x1b[?2026l"u8);
        return new EncodedHostBlit(
            StabilizeWritten(),
            Full: false,
            nextVisible,
            nextShape)
        {
            PatchSpans = merged,
            RepaintSerial = repaintSerial,
            Cursor = frame.Cursor,
            NoOp = noOp,
        };
    }

    /// <summary>
    /// True when <paramref name="cursor"/> is the host cursor of the last
    /// commit. A diff encode with no changed cell and this cursor is a no-op:
    /// herdr <c>encode_patch</c> (<c>src/protocol/render_ansi.rs:154-166</c>).
    /// Hypa marks it with <see cref="EncodedHostBlit.NoOp"/>, not with an
    /// empty payload. The payload keeps the cursor bytes, so the writer can
    /// still write them when another writer wrote to the host since.
    /// </summary>
    private bool IsCommittedCursor(HostCursor cursor) =>
        _committedCursor is { } committed && committed == cursor;

    /// <summary>
    /// The spans in the frame, one for each row, sorted by row. The span of a
    /// row goes from the first column to the last column of its spans.
    /// </summary>
    private static HostRowSpan[] MergeSpans(IReadOnlyList<HostRowSpan> spans, int frameCols, int frameRows)
    {
        var list = new List<HostRowSpan>(spans.Count);
        foreach (var span in spans)
        {
            var start = Math.Max(0, span.Col);
            var end = Math.Min(frameCols, span.EndCol);
            if ((uint)span.Row < (uint)frameRows && start < end)
                list.Add(new HostRowSpan(span.Row, start, end));
        }

        list.Sort(static (a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));
        var write = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (write > 0 && list[write - 1].Row == list[i].Row)
            {
                var last = list[write - 1];
                list[write - 1] = last with { EndCol = Math.Max(last.EndCol, list[i].EndCol) };
                continue;
            }

            list[write++] = list[i];
        }

        return list.GetRange(0, write).ToArray();
    }

    public EncodedHostBlit Encode(HostFrame frame, bool repaint = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var repaintSerial = Interlocked.Read(ref _repaintRequested);
        var forceRepaint = repaint || repaintSerial != Interlocked.Read(ref _repaintServed);
        var full = forceRepaint
            || !_hasLast
            || _lastCols != frame.Cols
            || _lastRows != frame.Rows;
        var clearBeforeFull = !_hasLast;
        var noOp = false;
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
            noOp = WriteChangedCells(_bytes, frame, _lastCells) == 0 && IsCommittedCursor(frame.Cursor);

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
            nextShape)
        {
            RepaintSerial = repaintSerial,
            Cursor = frame.Cursor,
            NoOp = noOp,
        };
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
        _committedCursor = encoded.Cursor;
        // herdr commit_patch (render_ansi.rs:232-254) copies only the patched
        // spans. A patch encode copies only its spans. A size change since the
        // encode falls back to a full copy.
        if (encoded.PatchSpans is not { } spans
            || !_hasLast
            || !frame.CopySpansInto(_lastCells, _lastCols, _lastRows, spans))
        {
            frame.CopyCellsInto(ref _lastCells, out _lastCols, out _lastRows);
        }
        _hasLast = true;
        // A full encode rewrote every cell, so it serves the requests that it
        // saw. A request after the encode stays pending for the next paint.
        if (encoded.Full && encoded.RepaintSerial > Interlocked.Read(ref _repaintServed))
            Interlocked.Exchange(ref _repaintServed, encoded.RepaintSerial);
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

                // A cursor move does not change the SGR state of the host
                // terminal. Keep the current style for the whole frame.
                if (nextInlineCol != c)
                    WriteCursorAddress(w, c, r);

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

    /// <summary>Returns the number of cells written.</summary>
    private static int WriteChangedCells(
        IBufferWriter<byte> w,
        HostFrame frame,
        AssembledCell[] prev)
    {
        AssembledStyle? current = null;
        string? activeHyperlink = null;
        var written = 0;
        // every host cell versus last frame. There is no row-dirty skip.
        for (var r = 0; r < frame.Rows; r++)
            written += WriteChangedRow(w, frame, r, prev, ref current, ref activeHyperlink, 0, frame.Cols);

        CellSgrEncoder.CloseHyperlink(w, ref activeHyperlink);
        return written;
    }

    /// <summary>
    /// Returns the number of cells written. <paramref name="prev"/> has the
    /// size of <paramref name="frame"/>. Only a cell from
    /// <paramref name="spanStart"/> to <paramref name="spanEnd"/> (not
    /// included) is compared with <paramref name="prev"/>. The caller
    /// guarantees that each other cell of the row is equal to it. The scan
    /// still reads the width of each cell of the row, because a wide cell
    /// changes the rules for the cells to its right.
    /// </summary>
    private static int WriteChangedRow(
        IBufferWriter<byte> w,
        HostFrame frame,
        int r,
        AssembledCell[] prev,
        ref AssembledStyle? current,
        ref string? activeHyperlink,
        int spanStart,
        int spanEnd)
    {
        var cols = frame.Cols;
        var row = frame.RowCells(r);
        var prevRow = prev.AsSpan(r * cols, cols);
        var invalidated = 0;
        var toSkip = 0;
        var written = 0;
        int? nextInlineCol = null;
        for (var c = 0; c < cols; c++)
        {
            ref readonly var cell = ref row[c];
            ref readonly var prevCell = ref prevRow[c];
            var skip = cell.IsContinuation
                || (cell.Width > 0 && c + cell.Width > cols);
            var width = VisualWidth(in cell);
            if (!skip
                && toSkip == 0
                && (invalidated > 0
                    || (c >= spanStart && c < spanEnd && !HostFrame.CellsEqual(in cell, in prevCell))))
            {
                // A cursor move and a skipped cell do not change the SGR
                // state of the host terminal. Keep the current style.
                if (nextInlineCol != c || invalidated > 0)
                    WriteCursorAddress(w, c, r);

                AppendCell(w, in cell, ref current, ref activeHyperlink);
                written++;
                nextInlineCol = cell.Text is { Length: 1 } glyph && char.IsAscii(glyph[0]) && width == 1
                    ? c + 1
                    : null;
            }
            else
                nextInlineCol = null;

            toSkip = Math.Max(0, width - 1);
            var affected = Math.Max(width, VisualWidth(in prevCell));
            invalidated = Math.Max(affected, invalidated) - 1;
            if (invalidated < 0)
                invalidated = 0;
        }

        return written;
    }

    private static void AppendCell(
        IBufferWriter<byte> w,
        in AssembledCell cell,
        ref AssembledStyle? current,
        ref string? activeHyperlink)
    {
        // The SGR key leaves out the hyperlink. The link has its own state,
        // and a reset does not close it (herdr render_ansi.rs:959-988).
        if (current is not { } liveStyle || !HostFrame.SgrEqual(liveStyle, cell.Style))
        {
            CellSgrEncoder.AppendResetSgr(w, cell.Style.ToSgr());
            current = cell.Style;
        }

        CellSgrEncoder.WriteHyperlinkIfChanged(w, ref activeHyperlink, cell.Style.Hyperlink);
        var text = string.IsNullOrEmpty(cell.Text) ? " " : cell.Text;
        WriteChars(w, text);
    }

    private static int VisualWidth(in AssembledCell cell) =>
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
        if (text.Length == 1 && char.IsAscii(text[0]))
        {
            // Most cells are one ASCII character: one UTF-8 byte.
            var one = w.GetSpan(1);
            one[0] = (byte)text[0];
            w.Advance(1);
            return;
        }

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
    /// <summary>
    /// The host row spans of a patch encode, one for each row, sorted by
    /// row. Null for a full or diff encode of the whole frame.
    /// </summary>
    public HostRowSpan[]? PatchSpans { get; init; }

    /// <summary>
    /// True when the paint changes no host cell and not the cursor. The
    /// writer skips the terminal write, unless another writer wrote to the
    /// host since the last encoded write, and still runs the accepted-write
    /// work.
    /// </summary>
    public bool NoOp { get; init; }

    /// <summary>The host cursor that this encode shows. <see cref="HostBlitEncoder.Commit"/> keeps it.</summary>
    public HostCursor Cursor { get; init; }

    /// <summary>The repaint request count that the encode saw.</summary>
    public long RepaintSerial { get; init; }

    public ReadOnlySpan<byte> Span => Utf8.Span;

    public byte[] Bytes => Utf8.ToArray();
}

/// <summary>
/// The host cells of row <see cref="Row"/> from column <see cref="Col"/> to
/// column <see cref="EndCol"/> (not included) that a patch paint stamped.
/// </summary>
internal readonly record struct HostRowSpan(int Row, int Col, int EndCol);
