using System.Text;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Test double for <see cref="IVtEngine"/>. Records Feed. Returns a programmed
/// snapshot. Does not parse CSI. Product engines are Ghostty only.
/// </summary>
internal sealed class StubVtEngine : IVtEngine
{
    private readonly List<byte[]> _feeds = [];
    private readonly List<string> _scrollback = [];
    private readonly List<bool> _scrollWrap = [];
    private int _cols;
    private int _rows;
    private int _cursorCol;
    private int _cursorRow;
    private string? _visibleOverride;
    private char[] _grid;
    private bool[] _wrap;
    private VtStructuredSnapshot? _snapshot;
    private bool _disposed;
    private int _bells;
    private bool _sync;
    private enum SkipKind { None, Esc, Csi, String }
    private SkipKind _skip;
    private const int MaxScrollbackLines = 200;

    public StubVtEngine(int cols = 80, int rows = 24, string? visibleText = null, VtStructuredSnapshot? snapshot = null)
    {
        BasicVtFloor.EnsureValidDimensions(cols, rows);
        _cols = cols;
        _rows = rows;
        _visibleOverride = visibleText;
        _snapshot = snapshot;
        _grid = AllocGrid(cols, rows);
        _wrap = new bool[rows];
    }

    public IReadOnlyList<byte[]> Feeds => _feeds;

    public int Cols => _cols;
    public int Rows => _rows;
    public int CursorCol => _cursorCol;
    public int CursorRow => _cursorRow;
    public bool IsSynchronizedOutputActive => _sync;
    public bool IsAlternateScreen { get; set; }

    public void Resize(int cols, int rows)
    {
        BasicVtFloor.EnsureValidDimensions(cols, rows);
        _cols = cols;
        _rows = rows;
        _snapshot = null;
        _grid = AllocGrid(cols, rows);
        _wrap = new bool[rows];
        _scrollback.Clear();
        _scrollWrap.Clear();
        _cursorCol = 0;
        _cursorRow = 0;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        _feeds.Add(data.ToArray());
        var text = Encoding.UTF8.GetString(data);
        _sync = TestPaneFactories.NoteSynchronizedOutput(text, _sync);
        WritePrintables(text);
    }

    public void Feed(ReadOnlySpan<char> text)
    {
        Feed(Encoding.UTF8.GetBytes(text.ToString()));
    }

    public int TakePendingBellCount()
    {
        var n = _bells;
        _bells = 0;
        return n;
    }

    public void AddBell(int count = 1) => _bells += count;

    public string GetVisibleText(bool trimTrailingWhitespace = true)
    {
        if (_visibleOverride is not null)
            return trimTrailingWhitespace ? _visibleOverride.TrimEnd() : _visibleOverride;

        var lines = new string[_rows];
        var wrap = new bool[_rows];
        for (var r = 0; r < _rows; r++)
        {
            lines[r] = RowText(r);
            wrap[r] = _wrap[r];
        }

        return VtRecentText.JoinPhysical(lines, wrap, _rows, unwrap: false, trim: trimTrailingWhitespace);
    }

    public string GetRecentText(int maxLines, bool trimTrailingWhitespace = true)
    {
        if (_visibleOverride is not null)
            return trimTrailingWhitespace ? _visibleOverride.TrimEnd() : _visibleOverride;
        return JoinHistory(maxLines, unwrap: false, trimTrailingWhitespace);
    }

    public string GetRecentUnwrappedText(int maxLines, bool trimTrailingWhitespace = true)
    {
        if (_visibleOverride is not null)
            return trimTrailingWhitespace ? _visibleOverride.TrimEnd() : _visibleOverride;
        return JoinHistory(maxLines, unwrap: true, trimTrailingWhitespace);
    }

    public void SetVisibleText(string text) => _visibleOverride = text ?? "";

    public void SetCursor(int col, int row)
    {
        _cursorCol = col;
        _cursorRow = row;
    }

    public VtStructuredSnapshot CaptureSnapshot()
    {
        if (_snapshot is not null)
            return _snapshot;

        var cells = new VtCellSnapshot[_rows][];
        for (var r = 0; r < _rows; r++)
        {
            cells[r] = new VtCellSnapshot[_cols];
            for (var c = 0; c < _cols; c++)
            {
                var ch = _grid[r * _cols + c];
                cells[r][c] = ch == ' '
                    ? VtCellSnapshot.Empty
                    : new VtCellSnapshot
                    {
                        Text = ch.ToString(),
                        Width = 1,
                        IsContinuation = false,
                        Style = VtCellStyleSnapshot.Default,
                    };
            }
        }

        return new VtStructuredSnapshot
        {
            SchemaVersion = 1,
            Provider = GhosttyVtEngine.Provider,
            Cols = _cols,
            Rows = _rows,
            Cursor = new VtCursorSnapshot { Col = _cursorCol, Row = _cursorRow, Visible = true },
            ActiveScreen = "main",
            ScrollRegion = new VtScrollRegionSnapshot { Top = 0, Bottom = Math.Max(0, _rows - 1) },
            Modes = VtModesSnapshot.BasicDefaults,
            Cells = cells,
        };
    }

    public void SetSnapshot(VtStructuredSnapshot snapshot) => _snapshot = snapshot;

    public void Reset()
    {
        _feeds.Clear();
        _visibleOverride = null;
        _snapshot = null;
        _cursorCol = 0;
        _cursorRow = 0;
        _bells = 0;
        _sync = false;
        _skip = SkipKind.None;
        _grid = AllocGrid(_cols, _rows);
        _wrap = new bool[_rows];
        _scrollback.Clear();
        _scrollWrap.Clear();
    }

    private void WritePrintables(string text)
    {
        foreach (var ch in text)
        {
            if (_skip == SkipKind.Esc)
            {
                if (ch == '[')
                    _skip = SkipKind.Csi;
                else if (ch is 'P' or 'X' or '^' or '_')
                    _skip = SkipKind.String;
                else if (ch == ']')
                    _skip = SkipKind.String;
                else
                    _skip = SkipKind.None;
                continue;
            }

            if (_skip == SkipKind.Csi)
            {
                if (ch >= 0x40 && ch <= 0x7E)
                    _skip = SkipKind.None;
                continue;
            }

            if (_skip == SkipKind.String)
            {
                if (ch == '\a' || ch == '\\')
                    _skip = SkipKind.None;
                continue;
            }

            if (ch == '\u001b')
            {
                _skip = SkipKind.Esc;
                continue;
            }

            if (ch == '\n')
            {
                _cursorCol = 0;
                AdvanceRow();
                continue;
            }

            if (ch == '\r')
            {
                _cursorCol = 0;
                continue;
            }

            if (ch < 32)
                continue;

            _grid[_cursorRow * _cols + _cursorCol] = ch;
            _cursorCol++;
            if (_cursorCol >= _cols)
            {
                _wrap[_cursorRow] = true;
                _cursorCol = 0;
                AdvanceRow();
            }
        }
    }

    private void AdvanceRow()
    {
        if (_cursorRow >= _rows - 1)
        {
            ScrollUp();
            return;
        }

        _cursorRow++;
    }

    private void ScrollUp()
    {
        _scrollback.Add(RowText(0));
        _scrollWrap.Add(_wrap[0]);
        while (_scrollback.Count > MaxScrollbackLines)
        {
            _scrollback.RemoveAt(0);
            _scrollWrap.RemoveAt(0);
        }

        Array.Copy(_grid, _cols, _grid, 0, _cols * (_rows - 1));
        Array.Copy(_wrap, 1, _wrap, 0, _rows - 1);
        Array.Fill(_grid, ' ', _cols * (_rows - 1), _cols);
        _wrap[_rows - 1] = false;
        _cursorRow = _rows - 1;
    }

    private string RowText(int row)
    {
        return new string(_grid, row * _cols, _cols);
    }

    private string JoinHistory(int maxLines, bool unwrap, bool trim)
    {
        var lines = new List<string>(_scrollback.Count + _rows);
        var wrap = new List<bool>(_scrollWrap.Count + _rows);
        lines.AddRange(_scrollback);
        wrap.AddRange(_scrollWrap);
        for (var r = 0; r < _rows; r++)
        {
            lines.Add(RowText(r));
            wrap.Add(_wrap[r]);
        }

        return VtRecentText.JoinPhysical(lines, wrap, maxLines, unwrap, trim);
    }

    private static char[] AllocGrid(int cols, int rows)
    {
        var grid = new char[cols * rows];
        Array.Fill(grid, ' ');
        return grid;
    }

    public void Dispose() => _disposed = true;

    public bool IsDisposed => _disposed;

    internal static VtStructuredSnapshot EmptySnapshot(int cols, int rows, int cursorCol = 0, int cursorRow = 0)
    {
        var cells = new VtCellSnapshot[rows][];
        for (var r = 0; r < rows; r++)
        {
            cells[r] = new VtCellSnapshot[cols];
            for (var c = 0; c < cols; c++)
                cells[r][c] = VtCellSnapshot.Empty;
        }

        return new VtStructuredSnapshot
        {
            SchemaVersion = 1,
            Provider = GhosttyVtEngine.Provider,
            Cols = cols,
            Rows = rows,
            Cursor = new VtCursorSnapshot { Col = cursorCol, Row = cursorRow, Visible = true },
            ActiveScreen = "main",
            ScrollRegion = new VtScrollRegionSnapshot { Top = 0, Bottom = Math.Max(0, rows - 1) },
            Modes = VtModesSnapshot.BasicDefaults,
            Cells = cells,
        };
    }
}

/// <summary>Factory that always returns <see cref="StubVtEngine"/>.</summary>
internal sealed class StubVtEngineFactory : IVtEngineFactory
{
    public VtProviderSelection Selection { get; } = new()
    {
        Provider = VtProviderKind.Ghostty,
        RequireGhostty = true,
    };

    public string ProviderWireName => "ghostty";

    public IVtEngine Create(int cols, int rows) => new StubVtEngine(cols, rows);

    public IVtEngine Create(int cols, int rows, int maxScrollback) => new StubVtEngine(cols, rows);
}

internal static class TestVtEngines
{
    public static StubVtEngine Stub(int cols, int rows) => new(cols, rows);

    public static IVtEngine GhosttyOrSkip(int cols, int rows, int maxScrollback = 200)
    {
        var lib = GhosttyTestRequire.TryResolveNativeLibraryPath();
        GhosttyTestRequire.RequireNativeLibrary(lib);
        return new GhosttyVtEngine(cols, rows, maxScrollback, lib);
    }
}
