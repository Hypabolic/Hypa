using System.Buffers;
using System.Globalization;
using System.Text;
using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>Per-pane host cursor in content-box space.</summary>
public sealed class PaneLiveCursor
{
    public int Col { get; set; }

    public int Row { get; set; }

    public bool Visible { get; set; }

    /// <summary>When true, emit CUP to the tracked cell before the next graphic.</summary>
    public bool SyncHost { get; set; }

    /// <summary>
    /// Drawn host-cursor policy. Remap keeps the native cursor hidden and
    /// skips cursor-only CUP. StampHostCursor paints the block.
    /// </summary>
    public bool DrawnHost { get; set; }

    public int DrawnCol { get; set; } = -1;

    public int DrawnRow { get; set; } = -1;

    public bool DrawnActive { get; set; }

    /// <summary>Host cells the remap must not paint. Cursor state still advances.</summary>
    public CellRect? ClipOcclude { get; set; }

    public List<byte> Carry { get; } = [];

    /// <summary>True when the last Remap issued a region scroll.</summary>
    public bool DidScroll { get; set; }

    public bool HasGlyphs => _hasGlyphs && _glyphs is not null && _glyphCols > 0 && _glyphRows > 0;

    private string?[]? _glyphs;

    private int _glyphCols;

    private int _glyphRows;

    private bool _hasGlyphs;

    public void Seed(int col, int row, bool visible = false)
    {
        Col = Math.Max(0, col);
        Row = Math.Max(0, row);
        Visible = visible;
        SyncHost = false;
        Carry.Clear();
        DrawnActive = false;
        DrawnCol = -1;
        DrawnRow = -1;
    }

    public void ClearDrawn()
    {
        DrawnActive = false;
        DrawnCol = -1;
        DrawnRow = -1;
    }

    public void ClearGlyphs()
    {
        _glyphs = null;
        _glyphCols = 0;
        _glyphRows = 0;
        _hasGlyphs = false;
    }

    public void SeedGlyphs(AssembledSnapshot frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnsureGlyphs(frame.Cols, frame.Rows, reset: true);
        if (frame.Cells is null || _glyphs is null)
            return;

        var rows = Math.Min(frame.Rows, frame.Cells.Count);
        for (var r = 0; r < rows; r++)
        {
            var cells = frame.Cells[r];
            if (cells is null)
                continue;
            var cols = Math.Min(frame.Cols, cells.Count);
            for (var c = 0; c < cols; c++)
                _glyphs[r * _glyphCols + c] = cells[c].Text;
        }

        _hasGlyphs = true;
    }

    public void EnsureGlyphs(int cols, int rows, bool reset = false)
    {
        cols = Math.Max(0, cols);
        rows = Math.Max(0, rows);
        if (!reset && _glyphs is not null && _glyphCols == cols && _glyphRows == rows)
            return;

        var next = cols == 0 || rows == 0 ? null : new string?[cols * rows];
        var copied = false;
        if (!reset && _glyphs is not null && next is not null)
        {
            var copyRows = Math.Min(rows, _glyphRows);
            var copyCols = Math.Min(cols, _glyphCols);
            for (var r = 0; r < copyRows; r++)
            {
                for (var c = 0; c < copyCols; c++)
                    next[r * cols + c] = _glyphs[r * _glyphCols + c];
            }

            copied = _hasGlyphs;
        }

        _glyphs = next;
        _glyphCols = cols;
        _glyphRows = rows;
        _hasGlyphs = copied;
    }

    public string? GlyphAt(int col, int row)
    {
        if (_glyphs is null || col < 0 || row < 0 || col >= _glyphCols || row >= _glyphRows)
            return null;
        return _glyphs[row * _glyphCols + col];
    }

    public void WriteGlyph(int col, int row, string glyph, int width)
    {
        if (_glyphs is null || width <= 0 || row < 0 || row >= _glyphRows || col < 0 || col >= _glyphCols)
            return;

        _glyphs[row * _glyphCols + col] = glyph;
        for (var i = 1; i < width && col + i < _glyphCols; i++)
            _glyphs[row * _glyphCols + col + i] = "";
        _hasGlyphs = true;
    }

    public void ClearRect(int col, int row, int cols, int rows)
    {
        if (_glyphs is null)
            return;

        var x0 = Math.Clamp(col, 0, _glyphCols);
        var y0 = Math.Clamp(row, 0, _glyphRows);
        var x1 = Math.Clamp(col + Math.Max(0, cols), 0, _glyphCols);
        var y1 = Math.Clamp(row + Math.Max(0, rows), 0, _glyphRows);
        for (var r = y0; r < y1; r++)
        {
            for (var c = x0; c < x1; c++)
                _glyphs[r * _glyphCols + c] = " ";
        }

        if (x1 > x0 && y1 > y0)
            _hasGlyphs = true;
    }

    public void ScrollRows(int top, int bottomExclusive, int count, bool down)
    {
        if (_glyphs is null || count < 1)
            return;

        top = Math.Clamp(top, 0, _glyphRows);
        bottomExclusive = Math.Clamp(bottomExclusive, top, _glyphRows);
        var height = bottomExclusive - top;
        if (height < 1)
            return;

        count = Math.Min(count, height);
        if (down)
        {
            for (var r = bottomExclusive - 1; r >= top + count; r--)
                CopyRow(r - count, r);
            for (var r = top; r < top + count; r++)
                FillRow(r, " ");
            return;
        }

        for (var r = top; r < bottomExclusive - count; r++)
            CopyRow(r + count, r);
        for (var r = bottomExclusive - count; r < bottomExclusive; r++)
            FillRow(r, " ");
    }

    private void CopyRow(int from, int to)
    {
        if (_glyphs is null)
            return;
        Array.Copy(_glyphs, from * _glyphCols, _glyphs, to * _glyphCols, _glyphCols);
    }

    private void FillRow(int row, string glyph)
    {
        if (_glyphs is null)
            return;
        var start = row * _glyphCols;
        for (var c = 0; c < _glyphCols; c++)
            _glyphs[start + c] = glyph;
        _hasGlyphs = true;
    }

    public byte[] PaintVisible(CellRect box, CellRect? occlude = null)
    {
        var output = new List<byte>();
        AppendVisible(output, box, occlude, 0, box.Rows);
        return output.ToArray();
    }

    public void AppendVisible(
        List<byte> output,
        CellRect box,
        CellRect? occlude,
        int top,
        int bottomExclusive)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_glyphs is null || box.Cols < 1 || box.Rows < 1)
            return;

        top = Math.Clamp(top, 0, Math.Min(box.Rows, _glyphRows));
        bottomExclusive = Math.Clamp(bottomExclusive, top, Math.Min(box.Rows, _glyphRows));
        var cols = Math.Min(box.Cols, _glyphCols);
        for (var r = top; r < bottomExclusive; r++)
        {
            var screenRow = box.Row + r;
            var c = 0;
            while (c < cols)
            {
                var screenCol = box.Col + c;
                if (occlude is { } occ && occ.Contains(screenCol, screenRow))
                {
                    c++;
                    continue;
                }

                var start = c;
                while (c < cols && (occlude is not { } skip || !skip.Contains(box.Col + c, screenRow)))
                    c++;
                AppendCup(output, box.Col + start, screenRow);
                for (var i = start; i < c; i++)
                {
                    var glyph = _glyphs[r * _glyphCols + i];
                    if (glyph is { Length: 0 })
                        continue;
                    if (glyph is null)
                        output.Add((byte)' ');
                    else
                    {
                        var bytes = Encoding.UTF8.GetBytes(glyph);
                        for (var b = 0; b < bytes.Length; b++)
                            output.Add(bytes[b]);
                    }
                }
            }
        }
    }

    private static void AppendCup(List<byte> output, int col, int row)
    {
        var seq = SnapshotPainter.CursorAddress(col, row);
        foreach (var ch in seq)
            output.Add((byte)ch);
    }
}

/// <summary>
/// Remaps raw PTY sequences into a chrome content box. Bytes stay UTF-8.
/// Relative cursor moves are clamped. Last-row LF/RI scroll the box via DECSTBM.
/// Chunks do not home to the origin.
/// </summary>
public static class PaneLiveClip
{
    internal const int MaxCarryBytes = 1024;

    public static byte[] Remap(ReadOnlySpan<byte> bytes, CellRect box) =>
        Remap(bytes, box, new PaneLiveCursor());

    public static byte[] Remap(ReadOnlySpan<byte> bytes, CellRect box, PaneLiveCursor cursor) =>
        Remap(bytes, box, cursor, occlude: null);

    public static byte[] Remap(
        ReadOnlySpan<byte> bytes,
        CellRect box,
        PaneLiveCursor cursor,
        CellRect? occlude)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (box.Cols < 1 || box.Rows < 1)
            return bytes.ToArray();
        cursor.ClipOcclude = occlude;

        ReadOnlySpan<byte> input = bytes;
        byte[]? joined = null;
        if (cursor.Carry.Count > 0)
        {
            joined = new byte[cursor.Carry.Count + bytes.Length];
            cursor.Carry.CopyTo(joined);
            bytes.CopyTo(joined.AsSpan(cursor.Carry.Count));
            cursor.Carry.Clear();
            input = joined;
        }

        if (input.IsEmpty)
            return [];

        cursor.Col = Math.Clamp(cursor.Col, 0, box.Cols);
        cursor.Row = Math.Clamp(cursor.Row, 0, box.Rows - 1);
        cursor.DidScroll = false;
        cursor.EnsureGlyphs(box.Cols, box.Rows);

        var output = new List<byte>(input.Length + 16);
        if (cursor.DrawnHost)
            AppendAscii(output, SnapshotPainter.HideCursor);
        var i = 0;
        while (i < input.Length)
        {
            var b = input[i];
            if (b == 0x1b)
            {
                if (i + 1 >= input.Length)
                {
                    Hold(cursor, input[i..]);
                    break;
                }

                var next = input[i + 1];
                if (next == (byte)'[')
                {
                    if (!TryConsumeCsi(input, i, box, cursor, output, out var end))
                    {
                        Hold(cursor, input[i..]);
                        break;
                    }

                    i = end;
                    continue;
                }

                if (next is (byte)']' or (byte)'P' or (byte)'^' or (byte)'_')
                {
                    if (!TrySkipString(input, i, out var end))
                    {
                        Hold(cursor, input[i..]);
                        break;
                    }

                    i = end;
                    continue;
                }

                if (next == (byte)'M')
                {
                    ReverseIndex(output, box, cursor);
                    i += 2;
                    continue;
                }

                if (next == (byte)'D')
                {
                    Index(output, box, cursor, resetCol: false);
                    i += 2;
                    continue;
                }

                if (next == (byte)'E')
                {
                    Index(output, box, cursor, resetCol: true);
                    i += 2;
                    continue;
                }

                i += 2;
                continue;
            }

            if (b == (byte)'\n')
            {
                Index(output, box, cursor, resetCol: true, passLf: true);
                i++;
                continue;
            }

            if (b == (byte)'\r')
            {
                cursor.Col = 0;
                EmitCup(output, box, cursor);
                i++;
                continue;
            }

            if (b == 0x08)
            {
                cursor.Col = Math.Max(0, cursor.Col - 1);
                EmitCup(output, box, cursor);
                i++;
                continue;
            }

            if (b == (byte)'\t')
            {
                cursor.Col = Math.Min(box.Cols - 1, (cursor.Col + 8) & ~7);
                EmitCup(output, box, cursor);
                i++;
                continue;
            }

            if (b < 0x20)
            {
                i++;
                continue;
            }

            var status = Rune.DecodeFromUtf8(input[i..], out var rune, out var consumed);
            if (status == OperationStatus.NeedMoreData)
            {
                Hold(cursor, input[i..]);
                break;
            }

            if (status == OperationStatus.Done && consumed > 0)
            {
                if (!TryPlaceRune(output, box, cursor, rune))
                {
                    i += consumed;
                    continue;
                }

                var width = DisplayWidth(rune);
                NoteGlyph(cursor, rune.ToString(), width);
                if (OccludesHost(box, cursor, width))
                    cursor.SyncHost = true;
                else
                {
                    EnsureHost(output, box, cursor);
                    Append(output, input.Slice(i, consumed));
                }

                Advance(box, cursor, width);
                i += consumed;
                continue;
            }

            if (!TryPlaceRune(output, box, cursor, width: 1))
            {
                i++;
                continue;
            }

            NoteGlyph(cursor, ((char)b).ToString(), 1);
            if (OccludesHost(box, cursor))
                cursor.SyncHost = true;
            else
            {
                EnsureHost(output, box, cursor);
                output.Add(b);
            }

            Advance(box, cursor, 1);
            i++;
        }

        return output.ToArray();
    }

    private static bool TryConsumeCsi(
        ReadOnlySpan<byte> bytes,
        int start,
        CellRect box,
        PaneLiveCursor cursor,
        List<byte> output,
        out int end)
    {
        end = start;
        var i = start + 2;
        while (i < bytes.Length && (bytes[i] < 0x40 || bytes[i] > 0x7e))
            i++;
        if (i >= bytes.Length)
            return false;

        var final = (char)bytes[i];
        var body = Encoding.ASCII.GetString(bytes[(start + 2)..i]);
        end = i + 1;
        var privateMode = body.StartsWith('?');
        var payload = privateMode ? body[1..] : body;

        if (privateMode)
        {
            if (payload == "25" && final is 'h' or 'l')
            {
                cursor.Visible = final == 'h';
                if (cursor.DrawnHost)
                    AppendAscii(output, SnapshotPainter.HideCursor);
                else
                    Append(output, bytes[start..end]);
            }

            return true;
        }

        switch (final)
        {
            case 'H':
            case 'f':
                ParseCup(payload, out var row, out var col);
                cursor.Row = Math.Clamp(row - 1, 0, box.Rows - 1);
                cursor.Col = Math.Clamp(col - 1, 0, box.Cols - 1);
                EmitCup(output, box, cursor);
                return true;
            case 'J':
                EraseDisplay(output, box, cursor, FirstParam(payload, 0));
                return true;
            case 'K':
                EraseLine(output, box, cursor, FirstParam(payload, 0));
                return true;
            case 'A':
                cursor.Row = Math.Max(0, cursor.Row - FirstParam(payload, 1));
                EmitCup(output, box, cursor);
                return true;
            case 'B':
                cursor.Row = Math.Min(box.Rows - 1, cursor.Row + FirstParam(payload, 1));
                EmitCup(output, box, cursor);
                return true;
            case 'C':
                cursor.Col = Math.Min(box.Cols - 1, cursor.Col + FirstParam(payload, 1));
                EmitCup(output, box, cursor);
                return true;
            case 'D':
                cursor.Col = Math.Max(0, cursor.Col - FirstParam(payload, 1));
                EmitCup(output, box, cursor);
                return true;
            case 'G':
                cursor.Col = Math.Clamp(FirstParam(payload, 1) - 1, 0, box.Cols - 1);
                EmitCup(output, box, cursor);
                return true;
            case 'd':
                cursor.Row = Math.Clamp(FirstParam(payload, 1) - 1, 0, box.Rows - 1);
                EmitCup(output, box, cursor);
                return true;
            case 'm':
                Append(output, bytes[start..end]);
                return true;
            case 'L':
                if (!IsCountParam(payload))
                    return true;
                InsertLines(output, box, cursor, FirstParam(payload, 1));
                return true;
            case 'M':
                if (!IsCountParam(payload))
                    return true;
                DeleteLines(output, box, cursor, FirstParam(payload, 1));
                return true;
            case 'S':
                if (!IsCountParam(payload))
                    return true;
                ScrollUp(output, box, cursor, FirstParam(payload, 1));
                EmitCup(output, box, cursor);
                return true;
            case 'T':
                if (!IsCountParam(payload))
                    return true;
                ScrollDown(output, box, cursor, FirstParam(payload, 1));
                EmitCup(output, box, cursor);
                return true;
            default:
                return true;
        }
    }

    private static void EraseDisplay(List<byte> output, CellRect box, PaneLiveCursor cursor, int mode)
    {
        if (mode >= 2)
        {
            AppendAscii(output, SnapshotPainter.EraseRect(box.Col, box.Row, box.Cols, box.Rows, cursor.ClipOcclude));
            cursor.ClearRect(0, 0, box.Cols, box.Rows);
            cursor.Col = 0;
            cursor.Row = 0;
            EmitCup(output, box, cursor);
            return;
        }

        if (mode == 1)
        {
            for (var r = 0; r < cursor.Row; r++)
                AppendAscii(output, SnapshotPainter.EraseRect(box.Col, box.Row + r, box.Cols, 1, cursor.ClipOcclude));
            if (cursor.Row > 0)
                cursor.ClearRect(0, 0, box.Cols, cursor.Row);
            var lead = cursor.Col + 1;
            AppendAscii(output, SnapshotPainter.EraseRect(box.Col, box.Row + cursor.Row, lead, 1, cursor.ClipOcclude));
            cursor.ClearRect(0, cursor.Row, lead, 1);
            EmitCup(output, box, cursor);
            return;
        }

        var remain = Math.Max(0, box.Cols - cursor.Col);
        if (remain > 0)
            AppendAscii(output, SnapshotPainter.EraseRect(box.Col + cursor.Col, box.Row + cursor.Row, remain, 1, cursor.ClipOcclude));
        cursor.ClearRect(cursor.Col, cursor.Row, remain, 1);
        var after = box.Rows - cursor.Row - 1;
        if (after > 0)
            AppendAscii(output, SnapshotPainter.EraseRect(box.Col, box.Row + cursor.Row + 1, box.Cols, after, cursor.ClipOcclude));
        cursor.ClearRect(0, cursor.Row + 1, box.Cols, after);
        EmitCup(output, box, cursor);
    }

    private static void EraseLine(List<byte> output, CellRect box, PaneLiveCursor cursor, int mode)
    {
        if (mode >= 2)
        {
            AppendAscii(output, SnapshotPainter.EraseRect(box.Col, box.Row + cursor.Row, box.Cols, 1, cursor.ClipOcclude));
            cursor.ClearRect(0, cursor.Row, box.Cols, 1);
        }
        else if (mode == 1)
        {
            var lead = cursor.Col + 1;
            AppendAscii(output, SnapshotPainter.EraseRect(box.Col, box.Row + cursor.Row, lead, 1, cursor.ClipOcclude));
            cursor.ClearRect(0, cursor.Row, lead, 1);
        }
        else
        {
            var remain = Math.Max(0, box.Cols - cursor.Col);
            if (remain > 0)
                AppendAscii(output, SnapshotPainter.EraseRect(box.Col + cursor.Col, box.Row + cursor.Row, remain, 1, cursor.ClipOcclude));
            cursor.ClearRect(cursor.Col, cursor.Row, remain, 1);
        }

        EmitCup(output, box, cursor);
    }

    private static bool TrySkipString(ReadOnlySpan<byte> bytes, int start, out int end)
    {
        end = start;
        for (var i = start + 2; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x07)
            {
                end = i + 1;
                return true;
            }

            if (bytes[i] == 0x1b && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\\')
            {
                end = i + 2;
                return true;
            }
        }

        return false;
    }

    internal static int DisplayWidth(Rune rune) => SafeDisplayText.Width(rune);

    private static bool TryPlaceRune(
        List<byte> output,
        CellRect box,
        PaneLiveCursor cursor,
        Rune rune) =>
        TryPlaceRune(output, box, cursor, DisplayWidth(rune));

    private static bool TryPlaceRune(
        List<byte> output,
        CellRect box,
        PaneLiveCursor cursor,
        int width)
    {
        if (width <= 0)
            return true;
        if (cursor.Col < box.Cols && cursor.Col + width <= box.Cols)
            return true;
        if (cursor.Row + 1 < box.Rows)
        {
            cursor.Row++;
            cursor.Col = 0;
            EmitCup(output, box, cursor);
            return width <= box.Cols;
        }

        ScrollUp(output, box, cursor);
        cursor.Row = box.Rows - 1;
        cursor.Col = 0;
        EmitCup(output, box, cursor);
        return width <= box.Cols;
    }

    private static void Advance(CellRect box, PaneLiveCursor cursor, int width)
    {
        if (width <= 0)
            return;

        cursor.Col += width;
        if (cursor.Col < box.Cols)
            return;

        if (cursor.Row + 1 < box.Rows)
        {
            cursor.Row++;
            cursor.Col = 0;
            cursor.SyncHost = true;
            return;
        }

        cursor.Col = box.Cols;
        cursor.SyncHost = false;
    }

    private static void Index(
        List<byte> output,
        CellRect box,
        PaneLiveCursor cursor,
        bool resetCol,
        bool passLf = false)
    {
        if (resetCol)
            cursor.Col = 0;
        if (cursor.Row + 1 < box.Rows)
        {
            cursor.Row++;
            EmitCup(output, box, cursor);
            return;
        }

        if (TryScrollAroundOcclude(output, box, cursor, 0, box.Rows, 1, down: false))
        {
            cursor.Row = box.Rows - 1;
            EmitCup(output, box, cursor);
            return;
        }

        if (passLf)
            ScrollAt(output, box, box.EndRow - 1, "\n");
        else
            ScrollUp(output, box);
        cursor.ScrollRows(0, box.Rows, 1, down: false);
        cursor.DidScroll = true;
        cursor.Row = box.Rows - 1;
        EmitCup(output, box, cursor);
    }

    private static void ReverseIndex(List<byte> output, CellRect box, PaneLiveCursor cursor)
    {
        if (cursor.Row > 0)
        {
            cursor.Row--;
            EmitCup(output, box, cursor);
            return;
        }

        if (TryScrollAroundOcclude(output, box, cursor, 0, box.Rows, 1, down: true))
        {
            cursor.Row = 0;
            EmitCup(output, box, cursor);
            return;
        }

        ScrollAt(output, box, box.Row, "\u001bM");
        cursor.ScrollRows(0, box.Rows, 1, down: true);
        cursor.DidScroll = true;
        cursor.Row = 0;
        EmitCup(output, box, cursor);
    }

    private static void InsertLines(List<byte> output, CellRect box, PaneLiveCursor cursor, int count)
    {
        if (count < 1)
            return;
        var region = new CellRect(box.Col, box.Row + cursor.Row, box.Cols, box.Rows - cursor.Row);
        if (region.Rows < 1)
            return;
        if (TryScrollAroundOcclude(output, box, cursor, cursor.Row, box.Rows, count, down: true))
        {
            EmitCup(output, box, cursor);
            return;
        }

        ScrollDown(output, region, count);
        cursor.ScrollRows(cursor.Row, box.Rows, count, down: true);
        cursor.DidScroll = true;
        EmitCup(output, box, cursor);
    }

    private static void DeleteLines(List<byte> output, CellRect box, PaneLiveCursor cursor, int count)
    {
        if (count < 1)
            return;
        var region = new CellRect(box.Col, box.Row + cursor.Row, box.Cols, box.Rows - cursor.Row);
        if (region.Rows < 1)
            return;
        if (TryScrollAroundOcclude(output, box, cursor, cursor.Row, box.Rows, count, down: false))
        {
            EmitCup(output, box, cursor);
            return;
        }

        ScrollUp(output, region, count);
        cursor.ScrollRows(cursor.Row, box.Rows, count, down: false);
        cursor.DidScroll = true;
        EmitCup(output, box, cursor);
    }

    private static void ScrollUp(List<byte> output, CellRect box, PaneLiveCursor cursor, int count = 1)
    {
        if (TryScrollAroundOcclude(output, box, cursor, 0, box.Rows, count, down: false))
            return;
        ScrollUp(output, box, count);
        cursor.ScrollRows(0, box.Rows, count, down: false);
        cursor.DidScroll = true;
    }

    private static void ScrollDown(List<byte> output, CellRect box, PaneLiveCursor cursor, int count = 1)
    {
        if (TryScrollAroundOcclude(output, box, cursor, 0, box.Rows, count, down: true))
            return;
        ScrollDown(output, box, count);
        cursor.ScrollRows(0, box.Rows, count, down: true);
        cursor.DidScroll = true;
    }

    private static bool TryScrollAroundOcclude(
        List<byte> output,
        CellRect box,
        PaneLiveCursor cursor,
        int top,
        int bottomExclusive,
        int count,
        bool down)
    {
        if (count < 1)
            return false;
        if (cursor.ClipOcclude is not { Cols: > 0, Rows: > 0 } occ)
            return false;

        top = Math.Clamp(top, 0, box.Rows);
        bottomExclusive = Math.Clamp(bottomExclusive, top, box.Rows);
        var height = bottomExclusive - top;
        if (height < 1)
            return false;

        var region = new CellRect(box.Col, box.Row + top, box.Cols, height);
        if (!region.Intersects(occ))
            return false;

        // Copy-blank (or no-op) around the overlay. Do not DECSTBM through ClipOcclude.
        cursor.ScrollRows(top, bottomExclusive, count, down);
        cursor.AppendVisible(output, box, occ, top, bottomExclusive);
        cursor.DidScroll = true;
        cursor.SyncHost = true;
        return true;
    }

    private static void ScrollUp(List<byte> output, CellRect box, int count = 1)
    {
        if (count < 1)
            return;
        var n = count == 1 ? "\u001b[S" : string.Create(CultureInfo.InvariantCulture, $"\u001b[{count}S");
        ScrollWith(output, box, n);
    }

    private static void ScrollDown(List<byte> output, CellRect box, int count = 1)
    {
        if (count < 1)
            return;
        var n = count == 1 ? "\u001b[T" : string.Create(CultureInfo.InvariantCulture, $"\u001b[{count}T");
        ScrollWith(output, box, n);
    }

    private static void ScrollWith(List<byte> output, CellRect box, string action)
    {
        AppendAscii(output, SnapshotPainter.SetScrollRegion(box));
        AppendAscii(output, action);
        AppendAscii(output, SnapshotPainter.ResetScrollRegion);
    }

    private static void ScrollAt(List<byte> output, CellRect box, int screenRow, string action)
    {
        AppendAscii(output, SnapshotPainter.SetScrollRegion(box));
        AppendAscii(output, SnapshotPainter.CursorAddress(box.Col, screenRow));
        AppendAscii(output, action);
        AppendAscii(output, SnapshotPainter.ResetScrollRegion);
    }

    private static bool IsCountParam(string payload)
    {
        if (string.IsNullOrEmpty(payload))
            return true;
        if (payload.IndexOf(';') >= 0)
            return false;
        return int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private static void NoteGlyph(PaneLiveCursor cursor, string glyph, int width)
    {
        if (width <= 0)
            return;
        cursor.WriteGlyph(cursor.Col, cursor.Row, glyph, width);
    }

    private static void EnsureHost(List<byte> output, CellRect box, PaneLiveCursor cursor)
    {
        if (cursor.SyncHost)
            EmitCup(output, box, cursor, force: true);
    }

    private static void EmitCup(List<byte> output, CellRect box, PaneLiveCursor cursor, bool force = false)
    {
        cursor.Col = Math.Clamp(cursor.Col, 0, box.Cols - 1);
        cursor.Row = Math.Clamp(cursor.Row, 0, box.Rows - 1);
        if (cursor.DrawnHost && !force)
        {
            cursor.SyncHost = true;
            return;
        }

        if (OccludesHost(box, cursor))
        {
            cursor.SyncHost = true;
            return;
        }

        AppendAscii(output, SnapshotPainter.CursorAddress(box.Col + cursor.Col, box.Row + cursor.Row));
        cursor.SyncHost = false;
    }

    private static bool OccludesHost(CellRect box, PaneLiveCursor cursor, int width = 1)
    {
        if (cursor.ClipOcclude is not { Cols: > 0, Rows: > 0 } occ)
            return false;
        var col = box.Col + cursor.Col;
        var row = box.Row + cursor.Row;
        if (occ.Contains(col, row))
            return true;
        for (var i = 1; i < width; i++)
        {
            if (occ.Contains(col + i, row))
                return true;
        }

        return false;
    }

    private static void Hold(PaneLiveCursor cursor, ReadOnlySpan<byte> leftover)
    {
        cursor.Carry.Clear();
        if (leftover.Length > MaxCarryBytes)
            return;

        cursor.Carry.AddRange(leftover.ToArray());
    }

    private static void Append(List<byte> output, ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            output.Add(bytes[i]);
    }

    private static void AppendAscii(List<byte> output, string text)
    {
        foreach (var ch in text)
            output.Add((byte)ch);
    }

    private static int FirstParam(string body, int fallback)
    {
        if (string.IsNullOrEmpty(body))
            return fallback;
        var cut = body.IndexOf(';');
        var token = cut < 0 ? body : body[..cut];
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0)
            return n == 0 && fallback > 0 ? fallback : n;
        return fallback;
    }

    private static void ParseCup(string body, out int row, out int col)
    {
        row = 1;
        col = 1;
        if (string.IsNullOrEmpty(body))
            return;
        var parts = body.Split(';');
        if (parts.Length > 0
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
            && r > 0)
        {
            row = r;
        }

        if (parts.Length > 1
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
            && c > 0)
        {
            col = c;
        }
    }
}
