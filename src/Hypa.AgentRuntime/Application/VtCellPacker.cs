using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Packs a pane <see cref="VtFrame"/> into a compact cell payload.
/// A first frame or a size change packs the full grid. Else the payload
/// holds the cells that differ from the baseline. When the grid carries a
/// dirty-row hint whose base is the baseline grid, only the hinted rows
/// are compared.
/// <para>
/// With <c>rowShift</c>, an update can also move the rows of the baseline
/// by a signed count before its rows apply (<see cref="VtCellPackResult.Shift"/>).
/// A positive shift moves the content up: row <c>r</c> shows the baseline
/// row <c>r + shift</c>. The rows that the move empties are blank default
/// cells. The packer uses a shift only when the cells that it must still
/// send are less than half of the cells of the plain update.
/// </para>
/// <para>
/// When a full frame is smaller than the update, the packer returns the full
/// frame with <see cref="VtCellPackResult.FullIsSmaller"/> set. That frame
/// is not a reanchor.
/// </para>
/// </summary>
public static class VtCellPacker
{
    // changed cell follows the last written cell. One extra JSON row object
    // costs about 20 bytes; eight default cells cost about 8 bytes.
    private const int SegmentJoinColumns = 8;

    /// <summary>
    /// The cost of one row segment, in cells, beyond its own cells: the JSON
    /// row object costs about 20 bytes. A shift is compared with the plain
    /// update by this cost, so many small edits of a scroll (for example the
    /// last digit of each <c>seq</c> line) count as what they cost on the wire.
    /// </summary>
    private const int SegmentOverheadCells = 20;

    /// <summary>
    /// The packer compares the update with a full frame only when the update
    /// is at least this large. A smaller update cannot save more bytes than
    /// the check costs, and a typing echo keeps its dirty-row cost.
    /// </summary>
    internal const int MinFullCompareBytes = 512;

    public static VtCellPackResult? Prepare(
        VtFrame current,
        VtFrame? baseline,
        IReadOnlyList<int>? dirtyRows,
        bool rowShift = false)
    {
        // A bare row list cannot prove that the other rows equal this
        // baseline. Only a grid hint whose base is the baseline grid can.
        _ = dirtyRows;
        return Prepare(current, baseline, rowShift, fullWhenSmaller: true);
    }

    /// <summary>
    /// <see cref="Prepare(VtFrame, VtFrame?, IReadOnlyList{int}?, bool)"/>.
    /// Without <paramref name="fullWhenSmaller"/>, the update is never
    /// replaced by a full frame. A test reads the update size with it.
    /// </summary>
    internal static VtCellPackResult? Prepare(
        VtFrame current,
        VtFrame? baseline,
        bool rowShift,
        bool fullWhenSmaller)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (baseline is null)
            return Pack(current);
        int[]? onlyRows = null;
        if (TryHintedRows(current, baseline, out var hinted))
        {
            onlyRows = hinted;
        }
        else
        {
            if (baseline.CellsVisuallyEqual(current))
                return null;
            if (baseline.Cols != current.Cols
                || baseline.Rows != current.Rows
                || baseline.ViewportOrigin != current.ViewportOrigin
                || baseline.OccupantGeneration != current.OccupantGeneration
                || !string.Equals(baseline.ActiveScreen, current.ActiveScreen, StringComparison.Ordinal))
                return Pack(current);
        }

        var plan = CollectDelta(current, baseline, onlyRows, shift: 0, stopCost: int.MaxValue)!;
        if (plan.Segments.Count == 0
            && current.Cursor.Equals(baseline.Cursor)
            && current.Modes.Equals(baseline.Modes))
        {
            return null;
        }

        var shift = 0;
        // A shift can make the update smaller only when at least two rows
        // changed. A scroll changes almost every row, so the packer hashes
        // the grids only when at least half of the rows changed. An echo that
        // changes the input row and a hint row keeps its dirty-row cost.
        // The grid hint does not change the shift: a row outside the hint
        // equals the baseline row at the same index.
        if (rowShift
            && plan.ChangedRows >= 2
            && plan.ChangedRows * 2 >= current.Rows
            && FindShift(current, baseline) is var found and not 0)
        {
            // herdr surface_scroll.rs:254-256: keep the shift only when the
            // cells that are left are less than half of the plain update.
            // Each segment also counts its row object.
            var shifted = CollectDelta(current, baseline, onlyRows: null, found, stopCost: plan.Cost);
            if (shifted is not null)
            {
                plan = shifted;
                shift = found;
            }
        }

        var delta = PackDelta(current, plan, shift, out var deltaBytes);
        // herdr surface_delta.rs:93-99: a full frame that is smaller than the
        // update replaces it. The full frame omits blank default rows.
        if (fullWhenSmaller
            && deltaBytes >= MinFullCompareBytes
            && PackWithin(current, deltaBytes) is { } smaller)
        {
            return smaller with { FullIsSmaller = true };
        }

        return delta;
    }

    public static VtCellPackResult Pack(VtFrame current, bool emitHyperlinkTable = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        return PackWithin(current, int.MaxValue, emitHyperlinkTable)!.Value;
    }

    /// <summary>
    /// The full frame of <paramref name="current"/>, or null when its rows
    /// reach <paramref name="maxRowBytes"/> estimated JSON bytes.
    /// </summary>
    private static VtCellPackResult? PackWithin(VtFrame current, int maxRowBytes, bool emitHyperlinkTable = false)
    {
        var rows = new List<TerminalRenderCellRow>(current.Rows);
        var changedCells = 0;
        var rowBytes = 0L;
        List<string>? linkTable = null;
        for (var r = 0; r < current.Rows; r++)
        {
            if (IsBlankDefaultRow(current, r))
                continue;
            var packed = PackRow(current, r, omitBlankDefault: true, emitHyperlinkTable, ref linkTable);
            if (packed is null)
                continue;
            rows.Add(packed.Value.Row);
            changedCells += packed.Value.ChangedCells;
            rowBytes += EstimateRowBytes(packed.Value.Row);
            if (rowBytes >= maxRowBytes)
                return null;
        }

        return new VtCellPackResult(
            true,
            changedCells,
            rows,
            PackCursor(current.Cursor),
            current.Cols,
            current.Rows,
            current.Provider,
            current.ActiveScreen,
            current.ViewportOrigin,
            current.Modes.SynchronizedOutput,
            current.Modes.Mouse,
            current.Modes.MouseEncoding,
            linkTable,
            current.Modes.BracketedPaste,
            current.Modes.ApplicationCursor);
    }

    /// <summary>
    /// True when the current grid states that it differs from the baseline
    /// grid only in the hinted rows, and the frame header matches.
    /// </summary>
    private static bool TryHintedRows(VtFrame current, VtFrame baseline, out int[] rows)
    {
        rows = [];
        if (current.Grid.Delta is not { } delta
            || baseline.Grid.Identity is null
            || !ReferenceEquals(baseline.Grid.Identity, delta.BaseIdentity))
        {
            return false;
        }

        if (baseline.Cols != current.Cols
            || baseline.Rows != current.Rows
            || baseline.Grid.Cols != current.Grid.Cols
            || baseline.Grid.Rows != current.Grid.Rows
            || baseline.ViewportOrigin != current.ViewportOrigin
            || baseline.OccupantGeneration != current.OccupantGeneration
            || !string.Equals(baseline.ActiveScreen, current.ActiveScreen, StringComparison.Ordinal))
        {
            return false;
        }

        rows = delta.Rows;
        return true;
    }

    /// <summary>The changed segments of one update, before the pack.</summary>
    private sealed class DeltaPlan
    {
        public List<(int Row, int Start, int End)> Segments { get; } = [];

        /// <summary>
        /// The columns that the segments cover, plus
        /// <see cref="SegmentOverheadCells"/> for each segment.
        /// </summary>
        public int Cost { get; set; }

        /// <summary>The rows that have at least one segment.</summary>
        public int ChangedRows { get; set; }
    }

    /// <summary>
    /// Compares each row of <paramref name="current"/> with the baseline row
    /// <c>r + shift</c>, or with a blank row when that row is outside the
    /// grid. Returns null when the cost of the segments reaches one half of
    /// <paramref name="stopCost"/>, so a shift that does not halve the plain
    /// update stops early.
    /// </summary>
    private static DeltaPlan? CollectDelta(
        VtFrame current,
        VtFrame baseline,
        int[]? onlyRows,
        int shift,
        int stopCost)
    {
        var plan = new DeltaPlan();
        var segments = new List<(int Start, int End)>(4);
        var count = onlyRows?.Length ?? current.Rows;
        for (var n = 0; n < count; n++)
        {
            var r = onlyRows is null ? n : onlyRows[n];
            if ((uint)r >= (uint)current.Rows)
                continue;
            var source = r + shift;
            if ((uint)source >= (uint)baseline.Rows)
                source = -1;
            CollectChangedSegments(current, baseline, r, source, segments);
            if (segments.Count == 0)
                continue;
            plan.ChangedRows++;
            foreach (var (start, end) in segments)
            {
                plan.Segments.Add((r, start, end));
                plan.Cost += end - start + SegmentOverheadCells;
            }

            if (stopCost != int.MaxValue && (long)plan.Cost * 2 >= stopCost)
                return null;
        }

        return plan;
    }

    private static VtCellPackResult PackDelta(VtFrame current, DeltaPlan plan, int shift, out int rowBytes)
    {
        var rows = new List<TerminalRenderCellRow>(plan.Segments.Count);
        var changedCells = 0;
        var bytes = 0L;
        List<string>? linkTable = null;
        foreach (var (r, start, end) in plan.Segments)
        {
            var packed = PackRow(
                current,
                r,
                omitBlankDefault: false,
                emitHyperlinkTable: false,
                ref linkTable,
                start,
                end);
            if (packed is null)
                continue;
            var row = packed.Value.Row with { C = start };
            rows.Add(row);
            changedCells += packed.Value.ChangedCells;
            bytes += EstimateRowBytes(row);
        }

        rowBytes = (int)Math.Min(int.MaxValue, bytes);
        return new VtCellPackResult(
            false,
            changedCells,
            rows,
            PackCursor(current.Cursor),
            current.Cols,
            current.Rows,
            current.Provider,
            current.ActiveScreen,
            current.ViewportOrigin,
            current.Modes.SynchronizedOutput,
            current.Modes.Mouse,
            current.Modes.MouseEncoding,
            linkTable,
            current.Modes.BracketedPaste,
            current.Modes.ApplicationCursor,
            shift);
    }

    /// <summary>
    /// The shift that makes the most rows of <paramref name="current"/> equal
    /// to a baseline row, or 0 when no shift beats the rows that match in
    /// place. herdr <c>surface_scroll.rs:209-231</c>. A row hash only
    /// nominates a shift. The segment compare after it reads the real cells,
    /// so a hash collision can cost bytes but not correctness.
    /// </summary>
    private static int FindShift(VtFrame current, VtFrame baseline)
    {
        var rows = current.Rows;
        if (rows < 2 || baseline.Rows != rows || baseline.Cols != current.Cols)
            return 0;
        var now = new ulong[rows];
        var was = new ulong[rows];
        for (var r = 0; r < rows; r++)
        {
            now[r] = RowHash(current, r);
            was[r] = RowHash(baseline, r);
        }

        var best = Matches(now, was, 0);
        var bestShift = 0;
        for (var distance = 1; distance < rows; distance++)
        {
            // A larger distance leaves fewer rows that can match.
            if (rows - distance <= best)
                break;
            var up = Matches(now, was, distance);
            if (up > best)
            {
                best = up;
                bestShift = distance;
            }

            var down = Matches(now, was, -distance);
            if (down > best)
            {
                best = down;
                bestShift = -distance;
            }
        }

        return bestShift;
    }

    private static int Matches(ulong[] now, ulong[] was, int shift)
    {
        var count = 0;
        for (var r = 0; r < now.Length; r++)
        {
            var source = r + shift;
            if ((uint)source < (uint)was.Length && now[r] == was[source])
                count++;
        }

        return count;
    }

    private static ulong RowHash(VtFrame frame, int row)
    {
        var hash = 14695981039346656037UL;
        for (var c = 0; c < frame.Cols; c++)
        {
            var cell = frame.CellAt(row, c);
            hash = Mix(hash, (uint)StringComparer.Ordinal.GetHashCode(cell.Text ?? ""));
            hash = Mix(hash, ((ulong)cell.FgPacked << 32) | cell.BgPacked);
            hash = Mix(
                hash,
                cell.Modifier
                | ((ulong)(uint)cell.Width << 16)
                | ((cell.IsContinuation ? 1UL : 0UL) << 24)
                | ((ulong)cell.UnderlineStylePacked << 25)
                | ((ulong)cell.UnderlineColorPacked << 33));
            if (cell.Hyperlink is { } link)
                hash = Mix(hash, (uint)StringComparer.Ordinal.GetHashCode(link));
        }

        return hash;
    }

    private static ulong Mix(ulong hash, ulong value) =>
        (ulong.RotateLeft(hash, 5) ^ value) * 0x517cc1b727220a95UL;

    private static VtCellView BaseCell(VtFrame baseline, int sourceRow, int col) =>
        sourceRow < 0 ? VtCellView.Blank : baseline.CellAt(sourceRow, col);

    /// <summary>
    /// The segments of <paramref name="row"/> that differ from the baseline
    /// row <paramref name="sourceRow"/>. A source of -1 is a blank row: the
    /// row that a shift empties.
    /// </summary>
    private static void CollectChangedSegments(
        VtFrame current,
        VtFrame baseline,
        int row,
        int sourceRow,
        List<(int Start, int End)> dest)
    {
        dest.Clear();
        var cols = current.Cols;
        Span<byte> marked = cols <= 256 ? stackalloc byte[cols] : new byte[cols];
        marked.Clear();
        var any = false;
        for (var c = 0; c < cols; c++)
        {
            var cell = current.CellAt(row, c);
            // The client fills an emptied row with blank default cells. An
            // empty cell packs as a space, so it is equal to that blank.
            if (sourceRow < 0 ? IsBlankCell(in cell) : cell.VisuallyEquals(baseline.CellAt(sourceRow, c)))
                continue;
            marked[c] = 1;
            any = true;
        }

        if (!any)
            return;

        for (var c = 1; c < cols; c++)
        {
            if (marked[c] == 0)
                continue;
            var start = c;
            while (start > 0
                && (current.CellAt(row, start).IsContinuation
                    || BaseCell(baseline, sourceRow, start).IsContinuation))
            {
                start--;
                marked[start] = 1;
            }
        }

        // invalidates max(width_current, width_baseline) columns.
        for (var c = 0; c < cols; c++)
        {
            if (marked[c] == 0)
                continue;
            var cell = current.CellAt(row, c);
            var prev = BaseCell(baseline, sourceRow, c);
            if (cell.IsContinuation && prev.IsContinuation)
                continue;
            var span = Math.Max(Math.Max(1, cell.Width), Math.Max(1, prev.Width));
            for (var k = 1; k < span && c + k < cols; k++)
                marked[c + k] = 1;
        }

        var i = 0;
        while (i < cols)
        {
            if (marked[i] == 0)
            {
                i++;
                continue;
            }

            var start = i;
            var end = i + 1;
            while (end < cols && marked[end] != 0)
                end++;
            dest.Add((start, end));
            i = end;
        }

        for (var n = dest.Count - 1; n >= 1; n--)
        {
            var gap = dest[n].Start - dest[n - 1].End;
            if (gap < SegmentJoinColumns)
                dest[n - 1] = (dest[n - 1].Start, dest[n].End);
            else
                continue;
            dest.RemoveAt(n);
        }
    }

    private static bool IsBlankCell(in VtCellView cell) =>
        !cell.IsContinuation
        && cell.Width <= 1
        && IsDefault(in cell)
        && (string.IsNullOrEmpty(cell.Text) || cell.Text == " ");

    /// <summary>True when every primary cell of the row is a default-style space.</summary>
    private static bool IsBlankDefaultRow(VtFrame current, int r)
    {
        for (var c = 0; c < current.Cols; c++)
        {
            var cell = current.CellAt(r, c);
            if (cell.IsContinuation)
                continue;
            if (!IsDefault(in cell) || !(string.IsNullOrEmpty(cell.Text) || cell.Text == " "))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The JSON bytes of one packed row, with its array comma, as the
    /// protocol serializer writes it. The packer counts the bytes of both
    /// shapes with this one rule, so the compare is fair. It does not
    /// serialize the row.
    /// </summary>
    internal static long EstimateRowBytes(TerminalRenderCellRow row)
    {
        // {"i":N,"t":"..."} and the comma before the next row.
        long bytes = 14 + Digits(row.I) + EstimateStringBytes(row.T);
        if (row.C is { } c)
            bytes += 5 + Digits(c);
        if (row.W is { } widths)
            bytes += 6 + EstimateIntList(widths);
        if (row.G is { } glyphs)
            bytes += 6 + EstimateIntList(glyphs);
        if (row.S is { } runs)
        {
            bytes += 7;
            foreach (var run in runs)
                bytes += EstimateRunBytes(run);
        }

        return bytes;
    }

    private static long EstimateRunBytes(TerminalRenderStyleRun run)
    {
        // {"c":N,"n":N} and its comma.
        long bytes = 14 + Digits(run.C) + Digits(run.N);
        if (run.Fg != 0)
            bytes += 6 + Digits(run.Fg);
        if (run.Bg != 0)
            bytes += 6 + Digits(run.Bg);
        if (run.Bold is not null)
            bytes += 12;
        if (run.Dim is not null)
            bytes += 11;
        if (run.Italic is not null)
            bytes += 14;
        if (run.Underline is not null)
            bytes += 17;
        if (run.Inverse is not null)
            bytes += 15;
        if (run.Invisible is not null)
            bytes += 17;
        if (run.Strikethrough is not null)
            bytes += 21;
        if (run.Blink is not null)
            bytes += 13;
        if (run.Overline is not null)
            bytes += 16;
        if (run.UnderlineColor != 0)
            bytes += 19 + Digits(run.UnderlineColor);
        if (run.UnderlineStyle is { } style)
            bytes += 19 + Digits(style);
        if (run.Hyperlink is { } link)
            bytes += 15 + EstimateStringBytes(link);
        if (run.Hi is { } hi)
            bytes += 6 + Digits(hi);
        return bytes;
    }

    private static long EstimateIntList(IReadOnlyList<int> values)
    {
        long bytes = 0;
        foreach (var value in values)
            bytes += Digits(value) + 1;
        return bytes;
    }

    /// <summary>
    /// The default protocol encoder writes printable ASCII as one byte. It
    /// writes a control character, a non-ASCII character and an HTML-sensitive
    /// character as <c>\uXXXX</c>.
    /// </summary>
    private static long EstimateStringBytes(string? text)
    {
        if (text is null)
            return 0;
        long bytes = 0;
        foreach (var ch in text)
        {
            bytes += ch switch
            {
                '\\' => 2,
                '"' or '<' or '>' or '&' or '\'' or '+' or '`' => 6,
                < ' ' or > '~' => 6,
                _ => 1,
            };
        }

        return bytes;
    }

    private static int Digits(long value)
    {
        var digits = value < 0 ? 2 : 1;
        var rest = Math.Abs(value);
        while (rest >= 10)
        {
            rest /= 10;
            digits++;
        }

        return digits;
    }

    public static TerminalRenderCellsPayload ToPayload(
        VtCellPackResult packed,
        string paneId,
        long generation,
        long baseGeneration,
        int occupantGeneration)
    {
        // message one time.
        // send_to_all_clients encodes above the client loop, then sends the
        // same bytes to every client. WireBytes is filled from that one emit
        // serialize, not a probe.
        return new TerminalRenderCellsPayload
        {
            PaneId = paneId,
            Kind = TerminalRenderCellsPayload.KindCells,
            Full = packed.Full,
            // A full frame that is only smaller than the update is not a
            // reanchor, so the client does not start a control round trip.
            Reanchor = packed.Full && !packed.FullIsSmaller,
            Shift = packed.Full ? 0 : packed.Shift,
            GridCols = packed.GridCols,
            GridRows = packed.GridRows,
            Generation = generation,
            BaseGeneration = baseGeneration,
            OccupantGeneration = occupantGeneration,
            ChangedCells = packed.ChangedCells,
            Rows = packed.Rows,
            Cursor = packed.Cursor,
            Provider = packed.Provider,
            ActiveScreen = packed.ActiveScreen,
            ViewportOrigin = packed.ViewportOrigin,
            Sync = packed.Sync,
            Mouse = packed.Mouse,
            MouseEncoding = packed.MouseEncoding,
            BracketedPaste = packed.BracketedPaste,
            ApplicationCursor = packed.ApplicationCursor,
            Hl = packed.HyperlinkTable,
        };
    }

    public static string Serialize(TerminalRenderCellsPayload payload) =>
        JsonSerializer.Serialize(payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);

    public static byte[] SerializeUtf8(TerminalRenderCellsPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);

    private static TerminalRenderCursorPayload PackCursor(VtFrameCursor cursor)
    {
        if (!cursor.HasCursor)
            return new TerminalRenderCursorPayload { HasCursor = false };
        return new TerminalRenderCursorPayload
        {
            Col = cursor.Col,
            Row = cursor.Row,
            Visible = cursor.Visible,
            Shape = cursor.Shape,
        };
    }

    private static (TerminalRenderCellRow Row, int ChangedCells)? PackRow(
        VtFrame current,
        int r,
        bool omitBlankDefault,
        bool emitHyperlinkTable,
        ref List<string>? linkTable,
        int startCol = 0,
        int endColExclusive = -1)
    {
        var cols = current.Cols;
        if (endColExclusive < 0 || endColExclusive > cols)
            endColExclusive = cols;
        if (startCol < 0)
            startCol = 0;
        if (startCol >= endColExclusive)
            return null;

        var text = new StringBuilder(endColExclusive - startCol);
        List<int>? widths = null;
        List<int>? glyphs = null;
        var runs = new List<TerminalRenderStyleRun>();
        // The open run is its first cell, its column and its width. The run
        // object is made only when the run closes, not for each cell.
        var hasOpen = false;
        var openCell = default(VtCellView);
        var openCol = 0;
        var openN = 0;
        int openStyleIndex = -1;
        int openLinkIndex = -2;
        var changed = 0;
        var col = startCol;
        var blankDefault = true;
        while (col < endColExclusive)
        {
            var packedCell = current.PackedAt(r, col);
            var cell = current.CellAt(r, col);
            var width = cell.Width > 0 ? cell.Width : 1;
            if (cell.IsContinuation)
            {
                col++;
                continue;
            }

            var glyph = string.IsNullOrEmpty(cell.Text) ? " " : cell.Text;
            text.Append(glyph);
            if (width != 1)
            {
                widths ??= new List<int>(current.Cols);
                while (widths.Count < changed)
                    widths.Add(1);
                widths.Add(width);
            }
            else if (widths is not null)
                widths.Add(1);

            if (glyph.Length != 1)
            {
                glyphs ??= new List<int>(current.Cols);
                while (glyphs.Count < changed)
                    glyphs.Add(1);
                glyphs.Add(glyph.Length);
            }
            else if (glyphs is not null)
                glyphs.Add(1);

            if (!IsDefault(in cell) || glyph != " ")
                blankDefault = false;

            if (hasOpen
                && packedCell.StyleIndex == openStyleIndex
                && packedCell.HyperlinkIndex == openLinkIndex
                && SameStyle(in openCell, in cell))
            {
                openN += width;
            }
            else
            {
                if (hasOpen && !IsDefaultStyle(in openCell))
                    runs.Add(ToRun(in openCell, openCol, openN, emitHyperlinkTable, ref linkTable));
                hasOpen = true;
                openCell = cell;
                openCol = col;
                openN = width;
                openStyleIndex = packedCell.StyleIndex;
                openLinkIndex = packedCell.HyperlinkIndex;
            }

            changed++;
            col += width;
        }

        if (hasOpen && !IsDefaultStyle(in openCell))
            runs.Add(ToRun(in openCell, openCol, openN, emitHyperlinkTable, ref linkTable));

        if (omitBlankDefault && blankDefault)
            return null;

        return (new TerminalRenderCellRow
        {
            I = r,
            T = text.ToString(),
            W = widths,
            G = glyphs,
            S = runs.Count == 0 ? null : runs,
        }, changed);
    }

    private static bool IsDefault(in VtCellView cell) =>
        cell.FgPacked == 0
        && cell.BgPacked == 0
        && cell.Modifier == 0
        && cell.UnderlineColorPacked == 0
        && cell.UnderlineStylePacked == 0
        && string.IsNullOrEmpty(cell.Hyperlink);

    private static TerminalRenderStyleRun ToRun(
        in VtCellView cell,
        int col,
        int n,
        bool emitHyperlinkTable,
        ref List<string>? linkTable)
    {
        int? hi = null;
        string? hyperlink = string.IsNullOrEmpty(cell.Hyperlink) ? null : cell.Hyperlink;
        if (emitHyperlinkTable && hyperlink is not null)
        {
            linkTable ??= new List<string>();
            var index = linkTable.IndexOf(hyperlink);
            if (index < 0)
            {
                index = linkTable.Count;
                linkTable.Add(hyperlink);
            }

            hi = index + 1;
            hyperlink = null;
        }

        return new()
        {
            C = col,
            N = n,
            Fg = cell.FgPacked,
            Bg = cell.BgPacked,
            Bold = cell.Bold ? true : null,
            Dim = cell.Dim ? true : null,
            Italic = cell.Italic ? true : null,
            Underline = cell.Underline ? true : null,
            Inverse = cell.Inverse ? true : null,
            Invisible = cell.Invisible ? true : null,
            Strikethrough = cell.Strikethrough ? true : null,
            Blink = cell.Blink ? true : null,
            Overline = cell.Overline ? true : null,
            UnderlineColor = cell.UnderlineColorPacked,
            UnderlineStyle = cell.UnderlineStylePacked == 0 ? null : (int)cell.UnderlineStylePacked,
            Hyperlink = hyperlink,
            Hi = hi,
        };
    }

    // Two cells give equal runs from ToRun when these fields are equal. A
    // hyperlink maps to one table index, so the link text decides for both
    // the inline link and the table index.
    private static bool SameStyle(in VtCellView a, in VtCellView b) =>
        a.FgPacked == b.FgPacked
        && a.BgPacked == b.BgPacked
        && a.Bold == b.Bold
        && a.Dim == b.Dim
        && a.Italic == b.Italic
        && a.Underline == b.Underline
        && a.Inverse == b.Inverse
        && a.Invisible == b.Invisible
        && a.Strikethrough == b.Strikethrough
        && a.Blink == b.Blink
        && a.Overline == b.Overline
        && a.UnderlineColorPacked == b.UnderlineColorPacked
        && a.UnderlineStylePacked == b.UnderlineStylePacked
        && string.Equals(LinkOf(in a), LinkOf(in b), StringComparison.Ordinal);

    // True when ToRun gives a run with no style, which the row leaves out.
    private static bool IsDefaultStyle(in VtCellView cell) =>
        cell.FgPacked == 0
        && cell.BgPacked == 0
        && !cell.Bold
        && !cell.Dim
        && !cell.Italic
        && !cell.Underline
        && !cell.Inverse
        && !cell.Invisible
        && !cell.Strikethrough
        && !cell.Blink
        && !cell.Overline
        && cell.UnderlineColorPacked == 0
        && cell.UnderlineStylePacked == 0
        && LinkOf(in cell) is null;

    private static string? LinkOf(in VtCellView cell) =>
        string.IsNullOrEmpty(cell.Hyperlink) ? null : cell.Hyperlink;
}

/// <summary>Packed pane cells before JSON wrap.</summary>
public readonly record struct VtCellPackResult(
    bool Full,
    int ChangedCells,
    IReadOnlyList<TerminalRenderCellRow> Rows,
    TerminalRenderCursorPayload Cursor,
    int GridCols,
    int GridRows,
    string Provider,
    string ActiveScreen,
    int ViewportOrigin,
    bool Sync = false,
    string? Mouse = null,
    string? MouseEncoding = null,
    IReadOnlyList<string>? HyperlinkTable = null,
    bool BracketedPaste = false,
    bool ApplicationCursor = false,
    int Shift = 0)
{
    /// <summary>
    /// True on a full frame that the packer chose only because it is smaller
    /// than the update. Such a frame is not a reanchor.
    /// </summary>
    public bool FullIsSmaller { get; init; }
}
