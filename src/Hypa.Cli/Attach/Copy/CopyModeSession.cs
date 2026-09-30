using System.Text;
using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Copy;

/// <summary>One copy-mode grid. Attach seeds and appends. KeyEngine stays socket-free.</summary>
public sealed class CopyModeSession
{
    private readonly object _gate = new();
    private readonly CopyLiveDecoder _decoder = new();
    private bool _seeded;
    private bool _selecting;
    private int _anchorRow;
    private int _anchorCol;
    private bool _searchPrompt;
    private bool _searchForward = true;
    private string _searchQuery = "";
    private string _lastQuery = "";
    private bool _lastForward = true;
    private bool _altScreen;
    private bool _needsRecentReseed;

    public CopyModeBuffer Buffer { get; } = new();

    /// <summary>Immutable paint snapshot. Production paint must not read Buffer.Lines.</summary>
    public CopyModePaintSnapshot CapturePaintSnapshot()
    {
        lock (_gate)
            return Buffer.CapturePaintSnapshot();
    }

    /// <summary>Session-owned viewport clamp then immutable paint snapshot.</summary>
    public CopyModePaintSnapshot CapturePaintSnapshot(int viewportRows)
    {
        lock (_gate)
        {
            Buffer.ViewportRows = Math.Max(1, viewportRows);
            Buffer.ClampViewport();
            return Buffer.CapturePaintSnapshot();
        }
    }

    public bool ShowsOverlay
    {
        get
        {
            lock (_gate)
                return _seeded && Buffer.ViewportPinned;
        }
    }

    public int ViewportRows
    {
        get
        {
            lock (_gate)
                return Buffer.ViewportRows;
        }
    }

    public int RowCount
    {
        get
        {
            lock (_gate)
                return Buffer.RowCount;
        }
    }

    public int ViewportTop
    {
        get
        {
            lock (_gate)
                return Buffer.ViewportTop;
        }
    }

    public int Cols
    {
        get
        {
            lock (_gate)
                return Buffer.Cols;
        }
    }

    public int ScrollOffset
    {
        get
        {
            lock (_gate)
            {
                var view = Math.Max(1, Buffer.ViewportRows);
                var maxTop = Math.Max(0, Buffer.RowCount - view);
                return Math.Max(0, maxTop - Buffer.ViewportTop);
            }
        }
    }

    internal CopyModePaintSnapshot PreparePaintSnapshot(int viewportRows)
    {
        Buffer.ViewportRows = Math.Max(1, viewportRows);
        Buffer.ClampViewport();
        Buffer.RevealCursor();
        return Buffer.CapturePaintSnapshot();
    }

    public void SetViewportFromY(int y, CellRect scrollbar)
    {
        lock (_gate)
        {
            if (!_seeded || scrollbar.Rows <= 0)
                return;
            var rel = y - scrollbar.Row;
            var maxTop = Math.Max(0, Buffer.RowCount - Math.Max(1, Buffer.ViewportRows));
            var top = scrollbar.Rows <= 1
                ? 0
                : (int)Math.Round(rel * (double)maxTop / (scrollbar.Rows - 1));
            Buffer.SetViewportTop(top);
        }
    }

    public bool IsSeeded
    {
        get
        {
            lock (_gate)
                return _seeded;
        }
    }

    public bool IsAltScreen
    {
        get
        {
            lock (_gate)
                return _altScreen;
        }
    }

    public bool HasNewOutput
    {
        get
        {
            lock (_gate)
                return Buffer.HasNewOutput;
        }
    }

    public bool HasSelection
    {
        get
        {
            lock (_gate)
                return _selecting;
        }
    }

    public bool HasSearch
    {
        get
        {
            lock (_gate)
                return _searchPrompt || _lastQuery.Length > 0;
        }
    }

    public bool SearchPromptActive
    {
        get
        {
            lock (_gate)
                return _searchPrompt;
        }
    }

    public bool SearchForward
    {
        get
        {
            lock (_gate)
                return _searchForward;
        }
    }

    public string SearchQuery
    {
        get
        {
            lock (_gate)
                return _searchQuery;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Buffer.Clear();
            _decoder.Reset();
            _seeded = false;
            _selecting = false;
            _anchorRow = 0;
            _anchorCol = 0;
            _searchPrompt = false;
            _searchForward = true;
            _searchQuery = "";
            _lastQuery = "";
            _lastForward = true;
            _altScreen = false;
            _needsRecentReseed = false;
        }
    }

    public bool ConsumeNeedsRecentReseed()
    {
        lock (_gate)
        {
            var needed = _needsRecentReseed;
            _needsRecentReseed = false;
            return needed;
        }
    }

    public void NoteLiveScreen(bool nowAlt)
    {
        lock (_gate)
        {
            if (!_seeded)
                return;
            if (nowAlt == _altScreen)
                return;
            _decoder.Reset();
            _selecting = false;
            _searchPrompt = false;
            _searchQuery = "";
            _lastQuery = "";
            var cols = Math.Max(1, Buffer.Cols);
            var viewport = Math.Max(1, Buffer.ViewportRows);
            if (nowAlt)
            {
                _altScreen = true;
                _needsRecentReseed = false;
                Buffer.ReplaceAll(
                    [CopyModeBuffer.BlankLine(cols)],
                    cols,
                    historyLineCount: 0,
                    viewport);
                return;
            }

            _altScreen = false;
            _needsRecentReseed = true;
            Buffer.ReplaceAll(
                [CopyModeBuffer.BlankLine(cols)],
                cols,
                historyLineCount: 0,
                viewport);
        }
    }

    public void Seed(AssembledSnapshot? snapshot, string? recentText, int viewportRows)
    {
        lock (_gate)
            SeedUnlocked(snapshot, recentText, viewportRows, markReseed: false);
    }

    public void ScrollLines(int delta)
    {
        lock (_gate)
        {
            if (!_seeded || delta == 0)
                return;
            Buffer.ScrollLines(delta);
        }
    }

    public void SetViewportTop(int top)
    {
        lock (_gate)
        {
            if (!_seeded)
                return;
            Buffer.SetViewportTop(top);
        }
    }

    public void MergeSnapshot(AssembledSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (!_seeded)
                return;
            if (_altScreen != snapshot.IsAlternateScreen)
            {
                SeedUnlocked(
                    snapshot,
                    recentText: null,
                    Math.Max(1, Buffer.ViewportRows),
                    markReseed: !snapshot.IsAlternateScreen);
                return;
            }

            var dropped = Buffer.ReplaceLiveScreen(
                ScreenLines(snapshot),
                snapshot.Cols,
                retainCursor: _altScreen,
                promoteScrolled: !_altScreen);
            NoteTrimmed(dropped);
            Buffer.ViewportRows = Math.Max(1, Buffer.ViewportRows);
        }
    }

    public void AppendLive(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        lock (_gate)
        {
            if (!_seeded || _altScreen)
                return;
            var cols = Math.Max(1, Buffer.Cols);
            var tailText = Buffer.LiveLineOpen
                ? Buffer.LineText(Buffer.LiveTailRow()).TrimEnd()
                : null;
            var replaceOpen = tailText is not null && tailText.Length < cols;
            var lines = _decoder.Decode(bytes, replaceOpen ? tailText : null, cols, out var incomplete);
            if (lines.Count == 0)
                return;
            NoteTrimmed(Buffer.AppendLiveLines(lines, incomplete, replaceOpen));
        }
    }

    public void AppendUnseenLive(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        lock (_gate)
        {
            if (!_seeded || _altScreen)
                return;
            var cols = Math.Max(1, Buffer.Cols);
            var decoder = new CopyLiveDecoder();
            var lines = decoder.Decode(bytes, openLine: null, cols, out var incomplete);
            if (lines.Count == 0)
                return;
            var overlap = TextSuffixPrefixOverlap(Buffer, lines);
            if (overlap >= lines.Count)
                return;
            var rest = lines.GetRange(overlap, lines.Count - overlap);
            NoteTrimmed(Buffer.AppendLiveLines(rest, incomplete, replaceOpen: false));
        }
    }

    public string Paint(
        int originCol,
        int originRow,
        int cols,
        int rows,
        Hypa.AgentRuntime.Domain.Theme.ThemePalette? theme = null)
    {
        lock (_gate)
            return CopyModePainter.PaintUnlocked(this, originCol, originRow, cols, rows, theme);
    }

    public bool MoveLeft() => Mutate(CopyMotions.Left);

    public bool MoveRight() => Mutate(CopyMotions.Right);

    public bool MoveUp() => Mutate(CopyMotions.Up);

    public bool MoveDown() => Mutate(CopyMotions.Down);

    public bool WordForward(bool big) => Mutate(b => CopyMotions.WordForward(b, big));

    public bool WordBack(bool big) => Mutate(b => CopyMotions.WordBack(b, big));

    public bool WordEnd(bool big) => Mutate(b => CopyMotions.WordEnd(b, big));

    public bool ParagraphForward() => Mutate(CopyMotions.ParagraphForward);

    public bool ParagraphBack() => Mutate(CopyMotions.ParagraphBack);

    public bool PageDown() => Mutate(CopyMotions.PageDown);

    public bool PageUp() => Mutate(CopyMotions.PageUp);

    public bool HalfDown() => Mutate(CopyMotions.HalfDown);

    public bool HalfUp() => Mutate(CopyMotions.HalfUp);

    public bool StartSelection()
    {
        lock (_gate)
        {
            if (!_seeded)
                return false;
            if (_selecting)
                return false;
            _selecting = true;
            _anchorRow = Buffer.CursorRow;
            _anchorCol = Buffer.CursorCol;
            return true;
        }
    }

    public bool ClearSelection()
    {
        lock (_gate)
        {
            if (!_selecting)
                return false;
            _selecting = false;
            return true;
        }
    }

    public bool TryGetSelection(out int rowA, out int colA, out int rowB, out int colB)
    {
        lock (_gate)
        {
            rowA = _anchorRow;
            colA = _anchorCol;
            rowB = Buffer.CursorRow;
            colB = Buffer.CursorCol;
            return _selecting;
        }
    }

    internal bool TryGetSelectionUnlocked(out int rowA, out int colA, out int rowB, out int colB)
    {
        rowA = _anchorRow;
        colA = _anchorCol;
        rowB = Buffer.CursorRow;
        colB = Buffer.CursorCol;
        return _selecting;
    }

    public string ExtractSelection()
    {
        lock (_gate)
        {
            if (!_selecting)
                return "";
            return Buffer.Extract(_anchorRow, _anchorCol, Buffer.CursorRow, Buffer.CursorCol);
        }
    }

    public string? Yank()
    {
        lock (_gate)
        {
            if (!_selecting)
                return null;
            var text = Buffer.Extract(_anchorRow, _anchorCol, Buffer.CursorRow, Buffer.CursorCol);
            _selecting = false;
            return text;
        }
    }

    public bool BeginSearch(bool forward)
    {
        lock (_gate)
        {
            if (!_seeded)
                return false;
            _searchPrompt = true;
            _searchForward = forward;
            _searchQuery = "";
            return true;
        }
    }

    public bool AppendSearch(string text)
    {
        lock (_gate)
        {
            if (!_searchPrompt || string.IsNullOrEmpty(text))
                return false;
            _searchQuery += text;
            return true;
        }
    }

    public bool SearchBackspace()
    {
        lock (_gate)
        {
            if (!_searchPrompt || _searchQuery.Length == 0)
                return _searchPrompt;
            _searchQuery = _searchQuery[..^1];
            return true;
        }
    }

    public bool CommitSearch()
    {
        lock (_gate)
        {
            if (!_searchPrompt)
                return false;
            _searchPrompt = false;
            if (_searchQuery.Length == 0)
                return true;
            _lastQuery = _searchQuery;
            _lastForward = _searchForward;
            return JumpTo(_lastQuery, _lastForward, fromNext: false);
        }
    }

    public bool RepeatSearch(bool reverse)
    {
        lock (_gate)
        {
            if (_lastQuery.Length == 0)
                return false;
            var forward = reverse ? !_lastForward : _lastForward;
            return JumpTo(_lastQuery, forward, fromNext: true);
        }
    }

    public bool ClearSearch()
    {
        lock (_gate)
        {
            var had = _searchPrompt || _lastQuery.Length > 0;
            _searchPrompt = false;
            _searchQuery = "";
            _lastQuery = "";
            return had;
        }
    }

    private void SeedUnlocked(
        AssembledSnapshot? snapshot,
        string? recentText,
        int viewportRows,
        bool markReseed)
    {
        _seeded = false;
        Buffer.Clear();
        _decoder.Reset();
        _selecting = false;
        _searchPrompt = false;
        _searchQuery = "";
        _lastQuery = "";
        _needsRecentReseed = markReseed;
        viewportRows = Math.Max(1, viewportRows);

        if (snapshot is not null && snapshot.IsAlternateScreen)
        {
            _altScreen = true;
            var alt = ScreenLines(snapshot);
            Buffer.ReplaceAll(alt, Math.Max(1, snapshot.Cols), historyLineCount: 0, viewportRows);
            var altCursor = snapshot.Cursor ?? AssembledCursor.Default;
            Buffer.SetCursor(altCursor.Row, altCursor.Col);
            _seeded = true;
            return;
        }

        _altScreen = false;
        var cols = snapshot is not null ? Math.Max(1, snapshot.Cols) : 80;
        if (snapshot is not null)
        {
            var screen = ScreenLines(snapshot);
            if (TryAlignRecent(recentText, screen, cols, out var history))
            {
                var lines = new List<IReadOnlyList<CopyCell>>(history.Count + screen.Count);
                lines.AddRange(history);
                lines.AddRange(screen);
                Buffer.ReplaceAll(lines, cols, history.Count, viewportRows);
                Buffer.TrimTrailingBlankRows();
                var cursor = snapshot.Cursor ?? AssembledCursor.Default;
                Buffer.SetCursor(history.Count + cursor.Row, cursor.Col);
                _seeded = true;
                return;
            }

            if (!string.IsNullOrEmpty(recentText))
            {
                SeedFromRecentUnlocked(recentText, cols, viewportRows);
                return;
            }

            Buffer.ReplaceAll(screen, cols, historyLineCount: 0, viewportRows);
            Buffer.TrimTrailingBlankRows();
            var snapCursor = snapshot.Cursor ?? AssembledCursor.Default;
            Buffer.SetCursor(snapCursor.Row, snapCursor.Col);
            _seeded = true;
            return;
        }

        if (!string.IsNullOrEmpty(recentText))
        {
            SeedFromRecentUnlocked(recentText, cols, viewportRows);
            return;
        }

        Buffer.ReplaceAll([CopyModeBuffer.BlankLine(cols)], cols, historyLineCount: 0, viewportRows);
        _seeded = true;
    }

    private void SeedFromRecentUnlocked(string recentText, int cols, int viewportRows)
    {
        var recent = SplitTextLines(recentText);
        var lines = new List<IReadOnlyList<CopyCell>>();
        foreach (var line in recent)
            lines.AddRange(CopyModeBuffer.WrapFromText(line, Math.Max(1, cols)));
        if (lines.Count == 0)
            lines.Add(CopyModeBuffer.BlankLine(Math.Max(1, cols)));
        Buffer.ReplaceAll(lines, Math.Max(1, cols), historyLineCount: 0, viewportRows);
        Buffer.SetCursor(Buffer.LiveTailRow(), 0);
        _seeded = true;
    }

    private void NoteTrimmed(int dropped)
    {
        if (dropped <= 0)
            return;
        _anchorRow = Math.Max(0, _anchorRow - dropped);
    }

    private bool JumpTo(string query, bool forward, bool fromNext)
    {
        if (!CopySearch.Find(Buffer, query, forward, fromNext, out var row, out var col))
            return false;
        if (row == Buffer.CursorRow && col == Buffer.CursorCol)
            return false;
        Buffer.SetCursor(row, col);
        return true;
    }

    private bool Mutate(Action<CopyModeBuffer> action)
    {
        lock (_gate)
        {
            if (!_seeded || Buffer.IsEmpty)
                return false;
            var row = Buffer.CursorRow;
            var col = Buffer.CursorCol;
            action(Buffer);
            return Buffer.CursorRow != row || Buffer.CursorCol != col;
        }
    }

    internal static List<List<CopyCell>> ScreenLines(AssembledSnapshot snapshot)
    {
        var lines = new List<List<CopyCell>>(snapshot.Rows);
        var cols = Math.Max(1, snapshot.Cols);
        for (var r = 0; r < snapshot.Rows; r++)
        {
            if (r < snapshot.Cells.Count)
                lines.Add(CopyModeBuffer.FromAssembled(snapshot.Cells[r], cols));
            else
                lines.Add(CopyModeBuffer.BlankLine(cols));
        }

        return lines;
    }

    internal static List<List<CopyCell>> HistoryLines(
        string? recentText,
        IReadOnlyList<IReadOnlyList<CopyCell>> screen,
        int cols)
    {
        if (TryAlignRecent(recentText, screen, cols, out var history))
            return history;
        return [];
    }

    internal static int TextSuffixPrefixOverlap(
        CopyModeBuffer buffer,
        IReadOnlyList<string> incoming)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(incoming);
        var max = Math.Min(buffer.RowCount, incoming.Count);
        for (var k = max; k > 0; k--)
        {
            var match = true;
            for (var i = 0; i < k; i++)
            {
                if (!string.Equals(
                        buffer.LineText(buffer.RowCount - k + i).TrimEnd(),
                        incoming[i].TrimEnd(),
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

    internal static bool TryAlignRecent(
        string? recentText,
        IReadOnlyList<IReadOnlyList<CopyCell>> screen,
        int cols,
        out List<List<CopyCell>> history)
    {
        history = [];
        if (string.IsNullOrEmpty(recentText))
            return false;

        var recent = SplitTextLines(recentText);
        var visible = new List<string>(screen.Count);
        for (var i = 0; i < screen.Count; i++)
            visible.Add(CellsToText(screen[i]));

        var trimmed = visible.Count;
        while (trimmed > 0 && string.IsNullOrWhiteSpace(visible[trimmed - 1]))
            trimmed--;
        if (trimmed == 0 || recent.Count < trimmed)
            return false;

        var extra = recent.Count - trimmed;
        for (var i = 0; i < trimmed; i++)
        {
            if (!string.Equals(recent[extra + i], visible[i], StringComparison.Ordinal))
                return false;
        }

        for (var i = 0; i < extra; i++)
            history.AddRange(CopyModeBuffer.WrapFromText(recent[i], cols));
        return true;
    }

    internal static List<string> SplitLiveLines(ReadOnlySpan<byte> bytes, string? openLine, out bool incomplete)
    {
        var decoder = new CopyLiveDecoder();
        return decoder.Decode(bytes, openLine, cols: 10_000, out incomplete);
    }

    internal static List<string> SplitTextLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return [.. normalized.Split('\n')];
    }

    internal static string CellsToText(IReadOnlyList<CopyCell> line)
    {
        var sb = new StringBuilder();
        foreach (var cell in line)
        {
            if (cell.IsContinuation)
                continue;
            sb.Append(cell.Text);
        }

        return sb.ToString().TrimEnd();
    }

    internal static string StripControls(string text)
    {
        var decoder = new CopyLiveDecoder();
        var lines = decoder.Decode(Encoding.UTF8.GetBytes(text), openLine: null, cols: 10_000, out _);
        return string.Join('\n', lines);
    }
}
