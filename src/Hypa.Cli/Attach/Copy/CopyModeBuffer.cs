using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach.Copy;

/// <summary>Line/cell grid for copy mode. Cursor never lands on a continuation cell.</summary>
public sealed class CopyModeBuffer
{
    /// <summary>
    /// In-memory copy-grid line cap. Live <c>AppendLive</c> grows toward
    /// <see cref="MaxHistoryBytes"/> (design §14, 8 MiB).
    /// Do not use this as the <c>pane.read</c> seed <c>lines</c> value —
    /// that payload must fit the 1 MiB NDJSON frame.
    /// </summary>
    public const int MaxHistoryLines = 100_000;

    /// <summary>Design §14 scrollback budget.</summary>
    public const int MaxHistoryBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Raw-text budget for the copy-mode <c>pane.read</c> recent seed.
    /// One eighth of <see cref="UnixSocketServerOptions.DefaultMaxLineBytes"/>
    /// leaves margin for JSON escaping and the sibling <c>compressed</c> field.
    /// </summary>
    public const int SeedRecentMaxBytes = UnixSocketServerOptions.DefaultMaxLineBytes / 8;

    /// <summary>
    /// Conservative <c>pane.read</c> <c>lines</c> so the NDJSON reply fits
    /// <see cref="UnixSocketServerOptions.DefaultMaxLineBytes"/>.
    /// </summary>
    public static int ResolveSeedRecentLines(int cols)
    {
        _ = cols;
        return Math.Max(1, Math.Min(MaxHistoryLines, SeedRecentMaxBytes / 64));
    }

    private readonly LineRing _lines = new();
    private int _retainedByteCount;

    public int Cols { get; private set; }

    public int RowCount => _lines.Count;

    public int CursorRow { get; private set; }

    public int CursorCol { get; private set; }

    public int ViewportTop { get; private set; }

    public int ViewportRows { get; set; } = 1;

    public int HistoryLineCount { get; private set; }

    public bool ViewportPinned { get; private set; }

    public bool HasNewOutput { get; private set; }

    public bool LiveLineOpen { get; private set; }

    public IReadOnlyList<IReadOnlyList<CopyCell>> Lines => _lines.Snapshot();

    /// <summary>Immutable paint copy. Production paint must not read mutable lines.</summary>
    public CopyModePaintSnapshot CapturePaintSnapshot()
    {
        var rows = _lines.Snapshot();
        return new CopyModePaintSnapshot(
            rows,
            ViewportTop,
            ViewportRows,
            Cols,
            ViewportPinned,
            RowCount,
            CursorRow,
            CursorCol);
    }

    public bool IsEmpty => _lines.Count == 0;

    public void Clear()
    {
        _lines.Clear();
        _retainedByteCount = 0;
        RetainedByteCountUpdateCount = 0;
        Cols = 0;
        CursorRow = 0;
        CursorCol = 0;
        ViewportTop = 0;
        ViewportRows = 1;
        HistoryLineCount = 0;
        ViewportPinned = false;
        HasNewOutput = false;
        LiveLineOpen = false;
    }

    public void ReplaceAll(
        IReadOnlyList<IReadOnlyList<CopyCell>> lines,
        int cols,
        int historyLineCount,
        int viewportRows)
    {
        _lines.Clear();
        _retainedByteCount = 0;
        RetainedByteCountUpdateCount = 0;
        Cols = Math.Max(0, cols);
        foreach (var line in lines)
        {
            foreach (var row in WrapLine(line, Math.Max(1, Cols)))
                AddLine(row);
        }
        if (_lines.Count == 0)
        {
            AddLine(BlankLine(Math.Max(1, Cols)));
            Cols = Math.Max(1, Cols);
        }

        HistoryLineCount = Math.Clamp(historyLineCount, 0, _lines.Count);
        ViewportRows = Math.Max(1, viewportRows);
        LiveLineOpen = false;
        ViewportPinned = false;
        HasNewOutput = false;
        SetCursor(HistoryLineCount, 0);
        FollowViewport();
    }

    public void SetHistoryLineCount(int count) =>
        HistoryLineCount = Math.Clamp(count, 0, _lines.Count);

    public void MarkLiveLineOpen(bool open) => LiveLineOpen = open && _lines.Count > 0;

    public int LiveTailRow()
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (!IsBlankLine(i))
                return i;
        }

        return Math.Max(0, _lines.Count - 1);
    }

    public int TrimTrailingBlankRows()
    {
        var dropped = 0;
        while (_lines.Count > 1 && IsBlankLine(_lines.Count - 1))
        {
            RemoveLineAt(_lines.Count - 1);
            dropped++;
        }

        HistoryLineCount = Math.Min(HistoryLineCount, _lines.Count);
        if (CursorRow >= _lines.Count)
            CursorRow = Math.Max(0, _lines.Count - 1);
        ClampViewport();
        return dropped;
    }

    public void SetCursor(int row, int col)
    {
        if (_lines.Count == 0)
        {
            CursorRow = 0;
            CursorCol = 0;
            return;
        }

        CursorRow = Math.Clamp(row, 0, _lines.Count - 1);
        var line = _lines[CursorRow];
        if (line.Count == 0)
        {
            CursorCol = 0;
            return;
        }

        CursorCol = Math.Clamp(col, 0, line.Count - 1);
        SnapToPrimary();
        RevealCursor();
        RefreshFollowState();
    }

    public void SnapToPrimary()
    {
        if (_lines.Count == 0)
            return;
        var line = _lines[CursorRow];
        while (CursorCol > 0 && CursorCol < line.Count && line[CursorCol].IsContinuation)
            CursorCol--;
    }

    public CopyCell CellAt(int row, int col)
    {
        if (row < 0 || row >= _lines.Count)
            return CopyCell.Blank;
        var line = _lines[row];
        if (col < 0 || col >= line.Count)
            return CopyCell.Blank;
        return line[col];
    }

    public bool IsContinuation(int row, int col) => CellAt(row, col).IsContinuation;

    public int LineLength(int row) =>
        row < 0 || row >= _lines.Count ? 0 : _lines[row].Count;

    public string LineText(int row) => LineText(row, out _);

    public string LineText(int row, out List<int> indexToCol)
    {
        indexToCol = [];
        if (row < 0 || row >= _lines.Count)
            return "";
        var sb = new System.Text.StringBuilder();
        var line = _lines[row];
        for (var c = 0; c < line.Count; c++)
        {
            var cell = line[c];
            if (cell.IsContinuation)
                continue;
            foreach (var _ in cell.Text)
                indexToCol.Add(c);
            sb.Append(cell.Text);
        }

        return sb.ToString();
    }

    public int FirstPrimaryCol(int row)
    {
        var len = LineLength(row);
        for (var c = 0; c < len; c++)
        {
            if (!IsContinuation(row, c))
                return c;
        }

        return 0;
    }

    public int LastPrimaryCol(int row)
    {
        var last = 0;
        var len = LineLength(row);
        for (var c = 0; c < len; c++)
        {
            if (!IsContinuation(row, c))
                last = c;
        }

        return last;
    }

    public int LastNonBlankPrimaryCol(int row)
    {
        var last = -1;
        var len = LineLength(row);
        for (var c = 0; c < len; c++)
        {
            if (IsContinuation(row, c))
                continue;
            foreach (var ch in CellAt(row, c).Text)
            {
                if (!char.IsWhiteSpace(ch))
                {
                    last = c;
                    break;
                }
            }
        }

        return last;
    }

    public int NextPrimaryCol(int row, int col)
    {
        var len = LineLength(row);
        for (var c = col + 1; c < len; c++)
        {
            if (!IsContinuation(row, c))
                return c;
        }

        return -1;
    }

    public int PrevPrimaryCol(int row, int col)
    {
        for (var c = col - 1; c >= 0; c--)
        {
            if (!IsContinuation(row, c))
                return c;
        }

        return -1;
    }

    public bool IsBlankLine(int row)
    {
        if (row < 0 || row >= _lines.Count)
            return true;
        foreach (var cell in _lines[row])
        {
            if (cell.IsContinuation)
                continue;
            foreach (var ch in cell.Text)
            {
                if (!char.IsWhiteSpace(ch))
                    return false;
            }
        }

        return true;
    }

    public string Extract(int rowA, int colA, int rowB, int colB)
    {
        Normalize(rowA, colA, rowB, colB, out var r1, out var c1, out var r2, out var c2);
        var sb = new System.Text.StringBuilder();
        for (var r = r1; r <= r2; r++)
        {
            if (r > r1)
                sb.Append('\n');
            var start = r == r1 ? c1 : 0;
            var end = r == r2 ? c2 : Math.Max(0, LineLength(r) - 1);
            var lastVisible = LastNonBlankPrimaryCol(r);
            if (lastVisible < 0)
                continue;
            end = Math.Min(end, lastVisible);
            var line = r < _lines.Count ? _lines[r] : null;
            if (line is null || line.Count == 0)
                continue;
            start = Math.Clamp(start, 0, line.Count - 1);
            end = Math.Clamp(end, 0, line.Count - 1);
            if (start > end)
                continue;
            for (var c = start; c <= end; c++)
            {
                if (line[c].IsContinuation)
                    continue;
                sb.Append(line[c].Text);
            }
        }

        return sb.ToString();
    }

    public bool IsFollowingLiveTail()
    {
        if (_lines.Count == 0)
            return true;
        if (ViewportPinned)
            return false;
        return CursorRow >= LiveTailRow();
    }

    public int AppendLiveLines(IReadOnlyList<string> lines, bool lastIncomplete, bool replaceOpen = true)
    {
        if (lines.Count == 0)
            return 0;

        var retain = !IsFollowingLiveTail();
        var savedRow = CursorRow;
        var savedCol = CursorCol;
        var savedTop = ViewportTop;
        var cols = Math.Max(1, Cols);
        Cols = cols;

        var tail = LiveTailRow();
        var canReplace = replaceOpen && LiveLineOpen && _lines.Count > 0;
        var insertAt = canReplace ? tail : tail + 1;
        if (insertAt > _lines.Count)
            insertAt = _lines.Count;

        var start = 0;
        if (canReplace && insertAt < _lines.Count)
        {
            var wrapped = WrapFromText(lines[0], cols);
            ReplaceLineAt(insertAt, wrapped[0]);
            for (var w = 1; w < wrapped.Count; w++)
            {
                insertAt++;
                if (insertAt >= _lines.Count)
                    AddLine(wrapped[w]);
                else
                    InsertLine(insertAt, wrapped[w]);
            }

            start = 1;
            insertAt++;
        }

        for (var i = start; i < lines.Count; i++)
        {
            var wrapped = WrapFromText(lines[i], cols);
            foreach (var row in wrapped)
            {
                if (insertAt >= _lines.Count)
                    AddLine(row);
                else
                    InsertLine(insertAt, row);
                insertAt++;
            }
        }

        LiveLineOpen = lastIncomplete;
        var dropped = TrimToBudget();
        if (retain)
        {
            RetainPinnedCursor(savedRow - dropped, savedCol, savedTop - dropped);
            return dropped;
        }

        SetCursor(LiveTailRow(), 0);
        FollowViewport();
        return dropped;
    }

    public int ReplaceLiveScreen(
        IReadOnlyList<IReadOnlyList<CopyCell>> screen,
        int cols,
        bool retainCursor = false,
        bool promoteScrolled = true)
    {
        var retain = retainCursor || !IsFollowingLiveTail();
        var savedRow = CursorRow;
        var savedCol = CursorCol;
        var savedTop = ViewportTop;
        var next = promoteScrolled ? TrimTrailingBlankCellRows(screen) : CopyRows(screen);
        if (!promoteScrolled)
        {
            _lines.Clear();
            _retainedByteCount = 0;
            RetainedByteCountUpdateCount = 0;
            Cols = Math.Max(1, Math.Max(Cols, cols));
            foreach (var line in next)
            {
                foreach (var row in WrapLine(line, Cols))
                    AddLine(row);
            }
            if (_lines.Count == 0)
                AddLine(BlankLine(Cols));
            HistoryLineCount = 0;
            LiveLineOpen = false;
            var droppedAlt = TrimToBudget();
            if (retain)
            {
                RetainPinnedCursor(savedRow - droppedAlt, savedCol, savedTop - droppedAlt);
                return droppedAlt;
            }

            SetCursor(_lines.Count - 1, 0);
            FollowViewport();
            return droppedAlt;
        }

        var history = Math.Min(HistoryLineCount, _lines.Count);
        var oldLive = history < _lines.Count
            ? _lines.GetRange(history, _lines.Count - history)
            : [];
        var overlap = SuffixPrefixOverlap(oldLive, next);
        var promoted = overlap == 0 ? 0 : Math.Max(0, oldLive.Count - overlap);

        var kept = history > 0 ? _lines.GetRange(0, history) : [];
        for (var i = 0; i < promoted; i++)
            kept.Add(oldLive[i]);

        _lines.Clear();
        _retainedByteCount = 0;
        RetainedByteCountUpdateCount = 0;
        foreach (var line in kept)
            AddLine(line);
        Cols = Math.Max(Cols, Math.Max(1, cols));
        foreach (var line in next)
        {
            foreach (var row in WrapLine(line, Cols))
                AddLine(row);
        }
        if (_lines.Count == 0)
            AddLine(BlankLine(Cols));
        HistoryLineCount = Math.Min(history + promoted, _lines.Count);
        LiveLineOpen = false;
        var dropped = TrimToBudget();

        if (retain)
        {
            RetainPinnedCursor(savedRow - dropped, savedCol, savedTop - dropped);
            return dropped;
        }

        SetCursor(LiveTailRow(), 0);
        FollowViewport();
        return dropped;
    }

    public void SetViewportTop(int top)
    {
        var maxTop = Math.Max(0, _lines.Count - Math.Max(1, ViewportRows));
        ViewportTop = Math.Clamp(top, 0, maxTop);
        ViewportPinned = ViewportTop < maxTop;
        if (!ViewportPinned)
            HasNewOutput = false;
    }

    public void ScrollLines(int delta)
    {
        if (delta == 0 || _lines.Count == 0)
            return;
        SetViewportTop(ViewportTop + delta);
    }

    public void ClampViewport()
    {
        var maxTop = Math.Max(0, _lines.Count - Math.Max(1, ViewportRows));
        if (ViewportTop > maxTop)
            ViewportTop = maxTop;
        if (ViewportTop < 0)
            ViewportTop = 0;
    }

    public void RevealCursor()
    {
        var view = Math.Max(1, ViewportRows);
        if (CursorRow < ViewportTop)
            ViewportTop = CursorRow;
        else if (CursorRow >= ViewportTop + view)
            ViewportTop = CursorRow - view + 1;
        ClampViewport();
    }

    public void FollowViewport()
    {
        ViewportPinned = false;
        var view = Math.Max(1, ViewportRows);
        ViewportTop = Math.Max(0, CursorRow - view + 1);
        ClampViewport();
    }

    public void RefreshFollowState()
    {
        if (CursorRow >= LiveTailRow())
        {
            ViewportPinned = false;
            HasNewOutput = false;
        }
    }

    public void NotePinnedNewOutput()
    {
        ViewportPinned = true;
        HasNewOutput = true;
    }

    private void RetainPinnedCursor(int row, int col, int top)
    {
        if (_lines.Count == 0)
        {
            CursorRow = 0;
            CursorCol = 0;
            ViewportTop = 0;
            ViewportPinned = true;
            HasNewOutput = true;
            return;
        }

        CursorRow = Math.Clamp(row, 0, _lines.Count - 1);
        CursorCol = col;
        ViewportTop = top;
        ViewportPinned = true;
        HasNewOutput = true;
        SnapToPrimary();
        ClampViewport();
    }

    internal static int SuffixPrefixOverlap(
        IReadOnlyList<IReadOnlyList<CopyCell>> oldLive,
        IReadOnlyList<IReadOnlyList<CopyCell>> screen)
    {
        var max = Math.Min(oldLive.Count, screen.Count);
        for (var k = max; k > 0; k--)
        {
            var match = true;
            for (var i = 0; i < k; i++)
            {
                if (!string.Equals(
                        PlainText(oldLive[oldLive.Count - k + i]),
                        PlainText(screen[i]),
                        StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return k;
        }

        return 0;
    }

    internal static string PlainText(IReadOnlyList<CopyCell> line)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var cell in line)
        {
            if (cell.IsContinuation)
                continue;
            sb.Append(cell.Text);
        }

        return sb.ToString().TrimEnd();
    }

    internal int TrimToBudget()
    {
        var dropped = 0;
        while (_lines.Count > MaxHistoryLines || _retainedByteCount > MaxHistoryBytes)
        {
            if (_lines.Count <= 1)
                break;
            RemoveLineAt(0);
            dropped++;
            if (HistoryLineCount > 0)
                HistoryLineCount--;
        }

        if (dropped == 0)
            return 0;

        CursorRow = Math.Max(0, CursorRow - dropped);
        ViewportTop = Math.Max(0, ViewportTop - dropped);
        ClampViewport();
        return dropped;
    }

    /// <summary>Diagnostic byte count retained by the buffer.</summary>
    internal int MeasureByteSize() => _retainedByteCount;

    /// <summary>Diagnostic count of row-level retained-byte updates since reset.</summary>
    internal int RetainedByteCountUpdateCount { get; private set; }

    private void AddLine(List<CopyCell> line)
    {
        _lines.Add(line);
        _retainedByteCount += LineByteSize(line);
        RetainedByteCountUpdateCount++;
    }

    private void InsertLine(int index, List<CopyCell> line)
    {
        _lines.Insert(index, line);
        _retainedByteCount += LineByteSize(line);
        RetainedByteCountUpdateCount++;
    }

    private void ReplaceLineAt(int index, List<CopyCell> line)
    {
        _retainedByteCount -= LineByteSize(_lines[index]);
        _lines[index] = line;
        _retainedByteCount += LineByteSize(line);
        RetainedByteCountUpdateCount++;
    }

    private void RemoveLineAt(int index)
    {
        _retainedByteCount -= LineByteSize(_lines[index]);
        _lines.RemoveAt(index);
        RetainedByteCountUpdateCount++;
    }

    private static int LineByteSize(IReadOnlyList<CopyCell> line)
    {
        var n = 0;
        foreach (var cell in line)
            n += Encoding.UTF8.GetByteCount(cell.Text);
        return n;
    }

    public static List<List<CopyCell>> WrapFromText(string text, int cols)
    {
        cols = Math.Max(1, cols);
        var rows = new List<List<CopyCell>>();
        var current = new List<CopyCell>(cols);
        foreach (var ch in text)
        {
            current.Add(new CopyCell(ch.ToString(), 1, false));
            if (current.Count >= cols)
            {
                rows.Add(current);
                current = new List<CopyCell>(cols);
            }
        }

        if (current.Count > 0 || rows.Count == 0)
            rows.Add(Pad(current, cols));
        return rows;
    }

    public static List<CopyCell> FromText(string text, int cols)
    {
        var wrapped = WrapFromText(text, cols);
        return wrapped[0];
    }

    public static List<List<CopyCell>> WrapLine(IReadOnlyList<CopyCell> line, int cols)
    {
        cols = Math.Max(1, cols);
        if (line.Count <= cols)
            return [Pad(line, cols)];

        var rows = new List<List<CopyCell>>();
        var current = new List<CopyCell>(cols);
        foreach (var cell in line)
        {
            current.Add(cell);
            if (current.Count >= cols)
            {
                rows.Add(current);
                current = new List<CopyCell>(cols);
            }
        }

        if (current.Count > 0)
            rows.Add(Pad(current, cols));
        return rows;
    }

    public static List<CopyCell> FromAssembled(IReadOnlyList<AssembledCell> row, int cols)
    {
        var line = new List<CopyCell>(row.Count);
        foreach (var cell in row)
            line.Add(CopyCell.FromAssembled(cell));
        return Pad(line, cols);
    }

    public static List<CopyCell> Pad(IReadOnlyList<CopyCell> line, int cols)
    {
        var copy = new List<CopyCell>(Math.Max(cols, line.Count));
        copy.AddRange(line);
        while (copy.Count < cols)
            copy.Add(CopyCell.Blank);
        return copy;
    }

    public static List<CopyCell> BlankLine(int cols)
    {
        var line = new List<CopyCell>(Math.Max(1, cols));
        for (var i = 0; i < Math.Max(1, cols); i++)
            line.Add(CopyCell.Blank);
        return line;
    }

    internal static List<List<CopyCell>> CopyRows(
        IReadOnlyList<IReadOnlyList<CopyCell>> rows)
    {
        var copy = new List<List<CopyCell>>(rows.Count);
        foreach (var row in rows)
            copy.Add(row as List<CopyCell> ?? [.. row]);
        return copy;
    }

    internal static List<List<CopyCell>> TrimTrailingBlankCellRows(
        IReadOnlyList<IReadOnlyList<CopyCell>> rows)
    {
        var copy = CopyRows(rows);
        while (copy.Count > 1 && IsBlankCells(copy[^1]))
            copy.RemoveAt(copy.Count - 1);
        return copy;
    }

    private static bool IsBlankCells(IReadOnlyList<CopyCell> line)
    {
        foreach (var cell in line)
        {
            if (cell.IsContinuation)
                continue;
            foreach (var ch in cell.Text)
            {
                if (!char.IsWhiteSpace(ch))
                    return false;
            }
        }

        return true;
    }

    public static void Normalize(
        int rowA,
        int colA,
        int rowB,
        int colB,
        out int r1,
        out int c1,
        out int r2,
        out int c2)
    {
        if (rowA < rowB || (rowA == rowB && colA <= colB))
        {
            r1 = rowA;
            c1 = colA;
            r2 = rowB;
            c2 = colB;
            return;
        }

        r1 = rowB;
        c1 = colB;
        r2 = rowA;
        c2 = colA;
    }
}

public readonly record struct CopyCell(string Text, int Width, bool IsContinuation, AssembledStyle Style)
{
    public CopyCell(string text, int width, bool isContinuation)
        : this(text, width, isContinuation, AssembledStyle.Default)
    {
    }

    public static CopyCell Blank { get; } = new(" ", 1, false, AssembledStyle.Default);

    public static CopyCell FromAssembled(AssembledCell cell)
    {
        var text = SafeDisplayText.Encode(cell.Text);
        if (text.Length == 0)
            text = " ";
        return new(
            text,
            cell.Width > 0 ? cell.Width : 1,
            cell.IsContinuation,
            cell.Style);
    }
}

/// <summary>Immutable copy-mode paint view. Safe to read while append continues.</summary>
public sealed record CopyModePaintSnapshot(
    IReadOnlyList<IReadOnlyList<CopyCell>> Lines,
    int ViewportTop,
    int ViewportRows,
    int Cols,
    bool ViewportPinned,
    int RowCount,
    int CursorRow = 0,
    int CursorCol = 0);

internal sealed class LineRing
{
    private readonly List<List<CopyCell>> _items = [];
    private int _head;

    public int Count => _items.Count - _head;

    public List<CopyCell> this[int index]
    {
        get => _items[_head + index];
        set => _items[_head + index] = value;
    }

    public void Add(List<CopyCell> line) => _items.Add(line);

    public void Insert(int index, List<CopyCell> line)
    {
        CompactIfNeeded();
        _items.Insert(_head + index, line);
    }

    public void RemoveAt(int index)
    {
        if (index == 0 && Count > 0)
        {
            _head++;
            CompactIfNeeded();
            return;
        }

        CompactIfNeeded();
        _items.RemoveAt(_head + index);
    }

    public void Clear()
    {
        _items.Clear();
        _head = 0;
    }

    public List<List<CopyCell>> GetRange(int index, int count)
    {
        var list = new List<List<CopyCell>>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
            list.Add(this[index + i]);
        return list;
    }

    public IReadOnlyList<IReadOnlyList<CopyCell>> Snapshot()
    {
        var rows = new IReadOnlyList<CopyCell>[Count];
        for (var i = 0; i < Count; i++)
            rows[i] = this[i].ToArray();
        return rows;
    }

    private void CompactIfNeeded()
    {
        if (_head == 0)
            return;
        if (_head < 64 && _head * 2 < _items.Count)
            return;
        _items.RemoveRange(0, _head);
        _head = 0;
    }
}
