using System.Runtime.InteropServices;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// F2 Ghostty VT engine: maps pinned libghostty-vt into the structured
/// snapshot contract. No native handles on the public snapshot surface.
/// </summary>
/// <remarks>
/// Thread safety: all public methods take a single lock around the native
/// terminal (PaneRuntime read loop + CaptureVtSnapshot).
/// Scroll region is dual-tracked in managed code because the C API has no
/// first-class scroll-region getter at this pin. Dual-track follows:
/// DECSTBM (CSI r), RIS (ESC c), soft-reset (CSI ! p / DECSTR), managed
/// <see cref="Reset"/>, and resize (full-screen reset — cannot re-query ABI).
/// Residual: other Ghostty-internal region resets without those sequences are
/// not dual-tracked until the pin exposes a getter.
/// Coalesce one paint per pane per tick on the attach path.
/// </remarks>
public sealed class GhosttyVtEngine : IVtEngine
{
    public const string Provider = "ghostty";

    /// <summary>Default max scrollback in bytes for new Ghostty terminals.</summary>
    public const int DefaultMaxScrollback = 10_000;

    private readonly object _gate = new();
    private readonly GhosttyTerminalSafeHandle _terminal;
    private readonly GhosttyRenderStateSafeHandle _renderState;
    private GhosttyCellStyle.ColorSet? _cachedColors;
    private GhosttyCellStyle.Rgb? _initialDefaultForeground;
    private GhosttyCellStyle.Rgb? _initialDefaultBackground;
    private GhosttyCellStyle.Rgb? _hostDefaultForeground;
    private GhosttyCellStyle.Rgb? _hostDefaultBackground;
    /// <summary>
    /// Last 256-entry default palette passed to <c>ghostty_terminal_set</c>
    // / option 14.
    /// vs this default so child OSC 4 becomes RGB.
    /// </summary>
    private GhosttyCellStyle.Rgb[] _appliedDefaultPalette = [];
    private bool _resolveColors;
    /// <summary>Test seam: force <c>ghostty_grid_ref_style</c> failure.</summary>
    internal bool ForceStyleGetFailure;

    /// <summary>Test seam: force <c>ghostty_render_state_update</c> failure.</summary>
    internal bool ForceRenderStateUpdateFailure;

    /// <summary>Test seam: count per-cell row-cells RGB reads on the paint path.</summary>
    internal int ReadRowCellsRgbCalls { get; private set; }

    /// <summary>Test seam: count content-bg-tag reads on the paint path.</summary>
    internal int ReadContentBgTokenCalls { get; private set; }
    private int _cols;
    private int _rows;
    private int _scrollTop;
    private int _scrollBottom;
    private bool _disposed;
    private IntPtr _trackedRow;
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private char[] _decodeBuf = new char[8192];
    private uint[] _graphemeCodepoints = new uint[32];
    private char[] _graphemeChars = new char[64];
    // Dual-track DECSTBM CSI scanner (byte path).
    private enum CsiTrackState { Ground, Esc, Csi }
    private CsiTrackState _csiState = CsiTrackState.Ground;
    private readonly StringBuilder _csiParams = new(64);
    private readonly VtBellCounter _bellCounter = new();

    /// <summary>
    /// Create a Ghostty terminal. Loads the native library if needed.
    /// Throws when library load or terminal create fails (fail-closed).
    /// </summary>
    public GhosttyVtEngine(
        int cols = BasicVtFloor.DefaultCols,
        int rows = BasicVtFloor.DefaultRows,
        int maxScrollback = DefaultMaxScrollback,
        string? libraryPathOverride = null)
    {
        BasicVtFloor.EnsureValidDimensions(cols, rows);
        if (cols > ushort.MaxValue || rows > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(cols), "cols/rows must fit in ushort for Ghostty");
        if (maxScrollback < 0)
            throw new ArgumentOutOfRangeException(nameof(maxScrollback));

        GhosttyLibraryLoader.LoadRequired(libraryPathOverride);

        var options = new GhosttyNative.GhosttyTerminalOptions
        {
            Cols = (ushort)cols,
            Rows = (ushort)rows,
            MaxScrollback = (nuint)maxScrollback,
        };

        IntPtr raw = IntPtr.Zero;
        var rc = GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out raw, options);
        if (rc != GhosttyNative.Success || raw == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"ghostty_terminal_new failed with code {rc}");
        }

        _terminal = new GhosttyTerminalSafeHandle(raw);
        IntPtr renderRaw = IntPtr.Zero;
        var renderRc = GhosttyNative.ghostty_render_state_new(IntPtr.Zero, out renderRaw);
        if (renderRc != GhosttyNative.Success || renderRaw == IntPtr.Zero)
        {
            _terminal.Dispose();
            throw new InvalidOperationException(
                $"ghostty_render_state_new failed with code {renderRc}");
        }

        _renderState = new GhosttyRenderStateSafeHandle(renderRaw);
        _cols = cols;
        _rows = rows;
        _scrollTop = 0;
        _scrollBottom = rows - 1;

        // Explicit non-zero cell pixel sizes (image protocols / size reports).
        rc = WithNative(ptr => GhosttyNative.ghostty_terminal_resize(
            ptr,
            (ushort)cols,
            (ushort)rows,
            GhosttyNative.DefaultCellWidthPx,
            GhosttyNative.DefaultCellHeightPx));
        if (rc != GhosttyNative.Success)
        {
            _renderState.Dispose();
            _terminal.Dispose();
            throw new InvalidOperationException(
                $"ghostty_terminal_resize (initial) failed with code {rc}");
        }

        // cache initial_default_foreground / initial_default_background.
        CacheInitialDefaultColorsUnlocked();
        _appliedDefaultPalette = LoadDefaultColors().Palette;
    }

    /// <summary>
    /// <c>mode_get(MODE_SYNCHRONIZED_OUTPUT)</c> after the Ghostty write.
    /// </summary>
    public bool IsSynchronizedOutputActive
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return ModeGet(GhosttyNative.ModeSynchronizedOutput, false);
            }
        }
    }

    /// <summary>
    /// Alternate. Host-theme restore skips this screen.
    /// </summary>
    public bool IsAlternateScreen
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return string.Equals(GetActiveScreenWire(), "alt", StringComparison.Ordinal);
            }
        }
    }

    public int Cols { get { lock (_gate) return _cols; } }
    public int Rows { get { lock (_gate) return _rows; } }

    public int CursorCol
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return GetU16(GhosttyNative.TerminalDataCursorX);
            }
        }
    }

    public int CursorRow
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return GetU16(GhosttyNative.TerminalDataCursorY);
            }
        }
    }

    public void Resize(int cols, int rows)
    {
        BasicVtFloor.EnsureValidDimensions(cols, rows);
        if (cols > ushort.MaxValue || rows > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(cols), "cols/rows must fit in ushort for Ghostty");

        lock (_gate)
        {
            ThrowIfDisposed();
            if (cols == _cols && rows == _rows)
                return;

            var rc = WithNative(ptr => GhosttyNative.ghostty_terminal_resize(
                ptr,
                (ushort)cols,
                (ushort)rows,
                GhosttyNative.DefaultCellWidthPx,
                GhosttyNative.DefaultCellHeightPx));
            if (rc != GhosttyNative.Success)
            {
                throw new InvalidOperationException(
                    $"ghostty_terminal_resize failed with code {rc}");
            }

            _cols = cols;
            _rows = rows;
            // Policy: pin has no scroll-region getter, so dual-track cannot
            // re-sync to Ghostty after resize. Reset to full screen (common VT
            // behavior when the host resizes without re-applying DECSTBM).
            ResetScrollRegionFullScreen();
        }
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        lock (_gate)
        {
            ThrowIfDisposed();
            FeedBytesUnlocked(data);
        }
    }

    public void Feed(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return;

        // Convert to UTF-8 then write under a single lock (no re-entrant Feed).
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var rented = byteCount <= 4096 ? null : System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
        Span<byte> buffer = rented is null
            ? stackalloc byte[byteCount]
            : rented.AsSpan(0, byteCount);
        try
        {
            Encoding.UTF8.GetBytes(text, buffer);
            lock (_gate)
            {
                ThrowIfDisposed();
                FeedBytesUnlocked(buffer);
            }
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void FeedBytesUnlocked(ReadOnlySpan<byte> data)
    {
        _bellCounter.Feed(data);
        TrackScrollRegionBytes(data);
        var added = false;
        try
        {
            _terminal.DangerousAddRef(ref added);
            var ptr = _terminal.DangerousGetHandle();
            unsafe
            {
                fixed (byte* p = data)
                {
                    GhosttyNative.ghostty_terminal_vt_write(
                        ptr, (IntPtr)p, (nuint)data.Length);
                }
            }
        }
        finally
        {
            if (added)
                _terminal.DangerousRelease();
        }
    }

    public int TakePendingBellCount()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _bellCounter.TakePendingCount();
        }
    }

    public string GetVisibleText(bool trimTrailingWhitespace = true)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            // Lossy projection: cell walk, skip continuation, drop style.
            var sb = new StringBuilder(_rows * (_cols + 1));
            for (var r = 0; r < _rows; r++)
            {
                var line = new StringBuilder(_cols);
                for (var c = 0; c < _cols; c++)
                {
                    var cell = ReadViewportCell((ushort)c, (uint)r);
                    if (cell.IsContinuation)
                        continue;
                    line.Append(cell.Text);
                }

                var text = line.ToString();
                if (trimTrailingWhitespace)
                    text = text.TrimEnd();
                sb.Append(text);
                if (r < _rows - 1)
                    sb.Append('\n');
            }

            return sb.ToString();
        }
    }

    public string GetRecentText(int maxLines, bool trimTrailingWhitespace = true)
    {
        if (maxLines < 1)
            return string.Empty;

        lock (_gate)
        {
            ThrowIfDisposed();
            // Prefer full screen history when available; fall back to viewport.
            var totalRows = TryGetNuint(GhosttyNative.TerminalDataTotalRows, out var total)
                ? (int)Math.Min(total, int.MaxValue)
                : _rows;
            totalRows = Math.Max(totalRows, _rows);

            var start = Math.Max(0, totalRows - maxLines);
            var lines = new List<string>(totalRows - start);
            for (var r = start; r < totalRows; r++)
            {
                var line = new StringBuilder(_cols);
                for (var c = 0; c < _cols; c++)
                {
                    var cell = ReadScreenCell((ushort)c, (uint)r);
                    if (cell.IsContinuation)
                        continue;
                    line.Append(cell.Text);
                }

                var text = line.ToString();
                if (trimTrailingWhitespace)
                    text = text.TrimEnd();
                lines.Add(text);
            }

            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
                lines.RemoveAt(lines.Count - 1);

            if (lines.Count > maxLines)
                lines = lines.GetRange(lines.Count - maxLines, maxLines);

            return string.Join('\n', lines);
        }
    }

    public string GetRecentUnwrappedText(int maxLines, bool trimTrailingWhitespace = true)
    {
        if (maxLines < 1)
            return string.Empty;

        lock (_gate)
        {
            ThrowIfDisposed();
            var totalRows = TryGetNuint(GhosttyNative.TerminalDataTotalRows, out var total)
                ? (int)Math.Min(total, int.MaxValue)
                : _rows;
            totalRows = Math.Max(totalRows, _rows);
            var start = Math.Max(0, totalRows - maxLines);
            var end = totalRows - 1;
            return FormatScreenRangeUnlocked(start, end, unwrap: true, trim: trimTrailingWhitespace);
        }
    }

    public string GetRecentUnwrappedAnsi(int maxLines, bool trimTrailingWhitespace = true)
    {
        if (maxLines < 1)
            return string.Empty;

        lock (_gate)
        {
            ThrowIfDisposed();
            var totalRows = TryGetNuint(GhosttyNative.TerminalDataTotalRows, out var total)
                ? (int)Math.Min(total, int.MaxValue)
                : _rows;
            totalRows = Math.Max(totalRows, _rows);
            var start = Math.Max(0, totalRows - maxLines);
            var end = totalRows - 1;
            return FormatScreenRangeUnlocked(
                start,
                end,
                unwrap: true,
                trim: trimTrailingWhitespace,
                GhosttyNative.FormatterFormatVt);
        }
    }

    public VtStructuredSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            // Do not call ghostty_render_state_update. Goldens keep raw style tags.
            _resolveColors = false;
            return CaptureGridUnlocked(updateRenderState: false, out _);
        }
    }

    /// <summary>
    /// Attach snapshot with resolved cell colours. Updates render-state so
    /// OSC/theme colours are current. Does not clear dirty; call
    /// <see cref="CommitPostedPaint"/> after a successful post.
    /// </summary>
    public VtStructuredSnapshot CapturePaintSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _resolveColors = true;
            return CaptureGridUnlocked(updateRenderState: true, out _);
        }
    }

    /// <summary>
    /// Screen row count (scrollback plus viewport) and viewport rows.
    /// </summary>
    public bool TryGetScrollbackExtent(out int totalRows, out int viewRows)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            viewRows = _rows;
            totalRows = ScreenRowCountUnlocked();
            return true;
        }
    }

    /// <summary>
    /// Data id 15. Scrollback rows without the viewport rows.
    /// </summary>
    public bool TryGetScrollbackRowCount(out int rows)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (TryGetNuint(GhosttyNative.TerminalDataScrollbackRows, out var value))
            {
                rows = (int)Math.Min(value, (nuint)int.MaxValue);
                return true;
            }

            rows = 0;
            return false;
        }
    }

    /// <summary>
    /// <c>primary_screen_active</c>. Drain only on the primary screen.
    /// </summary>
    public bool IsPrimaryScreenActive
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return !string.Equals(GetActiveScreenWire(), "alt", StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    // / Move the Ghostty viewport.
    /// <c>ghostty_set_scroll_offset_from_bottom</c>. Offset 0 is BOTTOM
    /// (tag 1). Else ROW (tag 3) at <c>max - offset</c>.
    /// </summary>
    public int SetScrollOffsetFromBottom(int offsetFromBottom)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return SetScrollOffsetFromBottomUnlocked(offsetFromBottom);
        }
    }

    /// <summary>
    /// via scrollbar data id 9.
    /// </summary>
    public bool TryGetScrollMetrics(out int offsetFromBottom, out int maxOffsetFromBottom)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!TryGetScrollbarUnlocked(out var total, out var offset, out var len))
            {
                offsetFromBottom = 0;
                maxOffsetFromBottom = 0;
                return false;
            }

            var max = total > len ? total - len : 0UL;
            var fromBottom = total > offset + len ? total - offset - len : 0UL;
            maxOffsetFromBottom = ToIntOffset(max);
            offsetFromBottom = ToIntOffset(fromBottom);
            return true;
        }
    }

    /// <summary>
    /// Live attach paint. Reads dirty kind before any grid alloc. Clean
    /// returns with no cell walk. Partial populates only dirty rows. Full
    /// captures the complete grid. Does not clear dirty; call
    /// <see cref="CommitPostedPaint"/> after a successful post.
    /// A failed row iterator is a full-grid paint, not a patch.
    /// </summary>
    public bool TryCaptureLivePaint(
        out VtStructuredSnapshot snapshot,
        out uint dirtyKind,
        out IReadOnlyList<int>? dirtyRows)
    {
        snapshot = null!;
        dirtyKind = GhosttyNative.RenderStateDirtyFull;
        dirtyRows = null;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _resolveColors = true;
                var renderStateUpdateOk = UpdateRenderStateUnlocked(out dirtyKind);
                if (!renderStateUpdateOk)
                    dirtyKind = GhosttyNative.RenderStateDirtyFull;
                if (dirtyKind == GhosttyNative.RenderStateDirtyClean)
                {
                    snapshot = SnapshotFromCellsUnlocked(
                        EmptyCellGrid(),
                        includeCursorShape: true,
                        renderStateUpdateOk);
                    return true;
                }

                if (dirtyKind == GhosttyNative.RenderStateDirtyPartial)
                {
                    var partial = CaptureDirtyRowsUnlocked(renderStateUpdateOk, out dirtyRows);
                    if (partial is null || dirtyRows is null || dirtyRows.Count == 0)
                    {
                        dirtyKind = GhosttyNative.RenderStateDirtyFull;
                        dirtyRows = null;
                        snapshot = CaptureGridUnlocked(updateRenderState: false, out _, renderStateUpdateOk);
                    }
                    else
                        snapshot = partial;

                    return true;
                }

                snapshot = CaptureGridUnlocked(updateRenderState: false, out _, renderStateUpdateOk);
                return true;
            }
        }
        catch
        {
            snapshot = null!;
            dirtyKind = GhosttyNative.RenderStateDirtyFull;
            dirtyRows = null;
            return false;
        }
    }

    /// <summary>
    // / Stamp the current viewport into a reused flat value buffer.
    /// <c>src/pane/terminal.rs:2196-2257</c> writes each viewport cell into
    /// one reused buffer at <c>buf[(area.x + x, area.y + y)]</c>. Hypa
    /// <c>src/protocol/wire.rs:849</c>. Clean returns without a cell
    /// walk. Partial and Full both stamp the whole viewport.
    /// </summary>
    public bool TryStampLivePaint(
        VtFrameCell[] dest,
        VtStampTables tables,
        out uint dirtyKind,
        out VtFrameCursor cursor,
        out VtFrameModes modes,
        out string activeScreen)
    {
        ArgumentNullException.ThrowIfNull(dest);
        ArgumentNullException.ThrowIfNull(tables);
        dirtyKind = GhosttyNative.RenderStateDirtyFull;
        cursor = default;
        modes = default;
        activeScreen = "main";
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _resolveColors = true;
                // render_state.update then cursor_viewport. Failed update,
                // failed get, or HAS_VALUE false is None.
                var renderStateUpdateOk = UpdateRenderStateUnlocked(out dirtyKind);
                cursor = ReadPaintCursorUnlocked(renderStateUpdateOk);
                modes = CaptureFrameModes();
                activeScreen = GetActiveScreenWire();
                if (dirtyKind == GhosttyNative.RenderStateDirtyClean)
                    return true;

                EnsureStampDest(dest, _cols, _rows);
                tables.BeginFrame();
                StampViewportCellsUnlocked(dest, _cols, _rows, tables);
                return true;
            }
        }
        catch
        {
            dirtyKind = GhosttyNative.RenderStateDirtyFull;
            return false;
        }
    }

    /// <summary>
    // / Stamp the current paint viewport after a scroll move.
    /// <c>src/pane/terminal.rs:2094-2173</c>.
    /// </summary>
    public bool TryStampPaintSnapshot(
        VtFrameCell[] dest,
        VtStampTables tables,
        out VtFrameCursor cursor,
        out VtFrameModes modes,
        out string activeScreen)
    {
        ArgumentNullException.ThrowIfNull(dest);
        ArgumentNullException.ThrowIfNull(tables);
        cursor = default;
        modes = default;
        activeScreen = "main";
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _resolveColors = true;
                var renderStateUpdateOk = UpdateRenderStateUnlocked(out _);
                cursor = ReadPaintCursorUnlocked(renderStateUpdateOk);
                modes = CaptureFrameModes();
                activeScreen = GetActiveScreenWire();
                EnsureStampDest(dest, _cols, _rows);
                tables.BeginFrame();
                StampViewportCellsUnlocked(dest, _cols, _rows, tables);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Clear per-row dirty then global dirty after a successful post.
    /// </summary>
    public void CommitPostedPaint()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            ClearDirtyUnlocked();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            WithNative(GhosttyNative.ghostty_terminal_reset);
            ResetScrollRegionFullScreen();
            _csiState = CsiTrackState.Ground;
            _csiParams.Clear();
            _bellCounter.Reset();
            _utf8.Reset();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            ClearPinnedRowUnlocked();
        }

        _renderState.Dispose();
        _terminal.Dispose();
    }

    /// <summary>
    // Pin a
    /// screen row so a later feed can tell whether Ghostty still holds it.
    /// </summary>
    internal bool TryPinScreenRow(uint y)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return TryPinScreenRowUnlocked(y);
        }
    }

    /// <summary>
    /// Read the pinned row in screen coordinates. False means Ghostty
    /// discarded the row, or no pin exists.
    /// </summary>
    internal bool TryGetPinnedScreenRow(out int y)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return TryGetPinnedScreenRowUnlocked(out y);
        }
    }

    internal void ClearPinnedRow()
    {
        lock (_gate)
            ClearPinnedRowUnlocked();
    }

    /// <summary>
    /// Cursor, modes, and active screen without a cell walk. Deep scroll
    /// stamps store cells into the pane grid and still reports Ghostty
    /// chrome.
    /// </summary>
    internal bool TryReadPaintChrome(
        out VtFrameCursor cursor,
        out VtFrameModes modes,
        out string activeScreen)
    {
        cursor = default;
        modes = default;
        activeScreen = "main";
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _resolveColors = true;
                var renderStateUpdateOk = UpdateRenderStateUnlocked(out _);
                cursor = ReadPaintCursorUnlocked(renderStateUpdateOk);
                modes = CaptureFrameModes();
                activeScreen = GetActiveScreenWire();
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pack screen rows with the same cell reader as live stamp. History
    /// does not resolve paint colours.
    /// </summary>
    internal bool TryStampScreenRows(
        int startRow,
        int rowCount,
        VtStampTables tables,
        IList<VtFrameCell[]> dest)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(dest);
        if (rowCount <= 0)
            return true;
        lock (_gate)
        {
            ThrowIfDisposed();
            _resolveColors = false;
            for (var r = 0; r < rowCount; r++)
            {
                var screenY = startRow + r;
                if (screenY < 0)
                    continue;
                var row = new VtFrameCell[_cols];
                for (var c = 0; c < _cols; c++)
                    row[c] = StampCellAt(GhosttyNative.ScreenPoint((ushort)c, (uint)screenY), default, tables);
                dest.Add(row);
            }

            return true;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed || _terminal.IsInvalid || _terminal.IsClosed, this);
    }

    private string FormatScreenRangeUnlocked(
        int startRow,
        int endRow,
        bool unwrap,
        bool trim,
        uint emit = GhosttyNative.FormatterFormatPlain)
    {
        if (_cols < 1 || endRow < startRow)
            return string.Empty;

        var startPoint = GhosttyNative.ScreenPoint(0, (uint)startRow);
        var endCol = (ushort)Math.Max(0, _cols - 1);
        var endPoint = GhosttyNative.ScreenPoint(endCol, (uint)endRow);

        var added = false;
        try
        {
            _terminal.DangerousAddRef(ref added);
            return FormatScreenRangeNative(
                _terminal.DangerousGetHandle(), startPoint, endPoint, unwrap, trim, emit);
        }
        finally
        {
            if (added)
                _terminal.DangerousRelease();
        }
    }

    private static unsafe string FormatScreenRangeNative(
        IntPtr terminal,
        GhosttyNative.GhosttyPoint startPoint,
        GhosttyNative.GhosttyPoint endPoint,
        bool unwrap,
        bool trim,
        uint emit = GhosttyNative.FormatterFormatPlain)
    {
        var startRef = GhosttyNative.EmptyGridRef();
        var endRef = GhosttyNative.EmptyGridRef();
        var rc = GhosttyNative.ghostty_terminal_grid_ref(terminal, startPoint, ref startRef);
        if (rc != GhosttyNative.Success)
        {
            throw new InvalidOperationException(
                $"ghostty_terminal_grid_ref(start) failed with code {rc}");
        }

        rc = GhosttyNative.ghostty_terminal_grid_ref(terminal, endPoint, ref endRef);
        if (rc != GhosttyNative.Success)
        {
            throw new InvalidOperationException(
                $"ghostty_terminal_grid_ref(end) failed with code {rc}");
        }

        var selection = new GhosttyNative.GhosttySelection
        {
            Size = (nuint)Marshal.SizeOf<GhosttyNative.GhosttySelection>(),
            Start = startRef,
            End = endRef,
            Rectangle = 0,
        };

        var extra = new GhosttyNative.GhosttyFormatterTerminalExtra
        {
            Size = (nuint)Marshal.SizeOf<GhosttyNative.GhosttyFormatterTerminalExtra>(),
        };

        var options = new GhosttyNative.GhosttyFormatterTerminalOptions
        {
            Size = (nuint)Marshal.SizeOf<GhosttyNative.GhosttyFormatterTerminalOptions>(),
            Emit = emit,
            Unwrap = unwrap ? (byte)1 : (byte)0,
            Trim = trim ? (byte)1 : (byte)0,
            Extra = extra,
            Selection = (IntPtr)(&selection),
        };

        rc = GhosttyNative.ghostty_formatter_terminal_new(
            IntPtr.Zero, out var formatter, terminal, options);
        if (rc != GhosttyNative.Success || formatter == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"ghostty_formatter_terminal_new failed with code {rc}");
        }

        try
        {
            rc = GhosttyNative.ghostty_formatter_format_alloc(
                formatter, IntPtr.Zero, out var outPtr, out var outLen);
            if (rc != GhosttyNative.Success)
            {
                throw new InvalidOperationException(
                    $"ghostty_formatter_format_alloc failed with code {rc}");
            }

            string text;
            if (outLen == 0 || outPtr == IntPtr.Zero)
            {
                text = string.Empty;
            }
            else
            {
                try
                {
                    text = Marshal.PtrToStringUTF8(outPtr, checked((int)outLen)) ?? string.Empty;
                }
                finally
                {
                    GhosttyNative.ghostty_free(IntPtr.Zero, outPtr, outLen);
                }
            }

            return VtRecentText.TrimTrailingBlankLines(text);
        }
        finally
        {
            GhosttyNative.ghostty_formatter_free(formatter);
        }
    }

    private void WithNative(Action<IntPtr> action)
    {
        var added = false;
        try
        {
            _terminal.DangerousAddRef(ref added);
            action(_terminal.DangerousGetHandle());
        }
        finally
        {
            if (added)
                _terminal.DangerousRelease();
        }
    }

    private T WithNative<T>(Func<IntPtr, T> action)
    {
        var added = false;
        try
        {
            _terminal.DangerousAddRef(ref added);
            return action(_terminal.DangerousGetHandle());
        }
        finally
        {
            if (added)
                _terminal.DangerousRelease();
        }
    }

    private int WithNativeGet(uint data, IntPtr outPtr) =>
        WithNative(ptr => GhosttyNative.ghostty_terminal_get(ptr, data, outPtr));

    private unsafe ushort GetU16(uint data)
    {
        ushort value = 0;
        var rc = WithNativeGet(data, (IntPtr)(&value));
        if (rc != GhosttyNative.Success)
            return 0;
        return value;
    }

    private unsafe bool GetBool(uint data, bool defaultValue)
    {
        byte value = 0;
        var rc = WithNativeGet(data, (IntPtr)(&value));
        if (rc != GhosttyNative.Success)
            return defaultValue;
        return value != 0;
    }

    private unsafe bool TryGetNuint(uint data, out nuint value)
    {
        nuint v = 0;
        var rc = WithNativeGet(data, (IntPtr)(&v));
        if (rc != GhosttyNative.Success)
        {
            value = 0;
            return false;
        }

        value = v;
        return true;
    }

    private unsafe string GetActiveScreenWire()
    {
        uint screen = GhosttyNative.TerminalScreenPrimary;
        var rc = WithNativeGet(GhosttyNative.TerminalDataActiveScreen, (IntPtr)(&screen));
        if (rc != GhosttyNative.Success)
            return "main";
        return screen == GhosttyNative.TerminalScreenAlternate ? "alt" : "main";
    }

    private bool ModeGet(ushort mode, bool defaultValue)
    {
        byte value = 0;
        var rc = WithNative(ptr => GhosttyNative.ghostty_terminal_mode_get(ptr, mode, out value));
        if (rc != GhosttyNative.Success)
            return defaultValue;
        return value != 0;
    }

    private VtModesSnapshot CaptureModes()
    {
        var origin = ModeGet(GhosttyNative.ModeOrigin, false);
        var autoWrap = ModeGet(GhosttyNative.ModeWraparound, true);
        var insert = ModeGet(GhosttyNative.ModeInsert, false);
        var applicationCursor = ModeGet(GhosttyNative.ModeCursorKeys, false);
        var bracketed = ModeGet(GhosttyNative.ModeBracketedPaste, false);
        var focus = ModeGet(GhosttyNative.ModeFocusEvent, false);

        var mouse = "none";
        if (ModeGet(GhosttyNative.ModeAnyMouse, false))
            mouse = "any";
        else if (ModeGet(GhosttyNative.ModeButtonMouse, false))
            mouse = "button";
        else if (ModeGet(GhosttyNative.ModeNormalMouse, false))
            mouse = "normal";
        else if (GetBool(GhosttyNative.TerminalDataMouseTracking, false))
            mouse = "tracking";

        // Tracking off and no encoding bits: omit (attach default SGR).
        string? encoding = null;
        if (ModeGet(GhosttyNative.ModeMouseSgrPixels, false))
            encoding = "sgr_pixels";
        else if (ModeGet(GhosttyNative.ModeMouseSgr, false))
            encoding = "sgr";
        else if (ModeGet(GhosttyNative.ModeMouseUrxvt, false))
            encoding = "urxvt";
        else if (ModeGet(GhosttyNative.ModeMouseUtf8, false))
            encoding = "utf8";
        else if (!string.Equals(mouse, "none", StringComparison.Ordinal))
            encoding = "default";

        var sync = ModeGet(GhosttyNative.ModeSynchronizedOutput, false);

        return new VtModesSnapshot
        {
            Origin = origin,
            AutoWrap = autoWrap,
            Insert = insert,
            BracketedPaste = bracketed,
            Mouse = mouse,
            MouseEncoding = encoding,
            FocusReporting = focus,
            Sync = sync,
            ApplicationCursor = applicationCursor,
        };
    }

    private VtCellSnapshot ReadViewportCell(ushort x, uint y, IntPtr rowCells = default) =>
        ReadCellAt(GhosttyNative.ViewportPoint(x, y), rowCells);

    private VtCellSnapshot ReadScreenCell(ushort x, uint y) =>
        ReadCellAt(GhosttyNative.ScreenPoint(x, y));

    private VtCellSnapshot ReadCellAt(GhosttyNative.GhosttyPoint point, IntPtr rowCells = default)
    {
        var gridRef = GhosttyNative.EmptyGridRef();
        var rc = WithNative(ptr =>
        {
            var local = gridRef;
            var code = GhosttyNative.ghostty_terminal_grid_ref(ptr, point, ref local);
            gridRef = local;
            return code;
        });
        if (rc != GhosttyNative.Success)
            return VtCellSnapshot.Empty;

        // Wide property
        var wide = GhosttyNative.CellWideNarrow;
        if (GhosttyNative.ghostty_grid_ref_cell(in gridRef, out var cell) == GhosttyNative.Success)
        {
            unsafe
            {
                uint wideVal = 0;
                if (GhosttyNative.ghostty_cell_get(
                        cell,
                        GhosttyNative.CellDataWide,
                        (IntPtr)(&wideVal)) == GhosttyNative.Success)
                {
                    wide = wideVal;
                }
            }
        }

        var isContinuation = wide is GhosttyNative.CellWideSpacerTail
            or GhosttyNative.CellWideSpacerHead;
        var width = wide == GhosttyNative.CellWideWide ? 2 : 1;

        // Graphemes
        var text = " ";
        if (!isContinuation)
        {
            text = ReadGraphemeText(in gridRef);
            if (string.IsNullOrEmpty(text))
                text = " ";
        }

        // Style
        var style = ReadStyle(in gridRef, rowCells);

        return new VtCellSnapshot
        {
            Text = text,
            Width = width,
            IsContinuation = isContinuation,
            Style = style,
        };
    }

    private int ScreenRowCountUnlocked()
    {
        if (TryGetNuint(GhosttyNative.TerminalDataTotalRows, out var total) && total > 0)
            return Math.Max(_rows, (int)Math.Min(total, int.MaxValue));
        return _rows;
    }

    private bool TryPinScreenRowUnlocked(uint y)
    {
        return WithNative(ptr =>
        {
            ClearPinnedRowUnlocked();
            var point = GhosttyNative.ScreenPoint(0, y);
            var rc = GhosttyNative.ghostty_terminal_grid_ref_track(ptr, point, out _trackedRow);
            if (rc != GhosttyNative.Success || _trackedRow == IntPtr.Zero)
            {
                _trackedRow = IntPtr.Zero;
                return false;
            }

            return true;
        });
    }

    private bool TryGetPinnedScreenRowUnlocked(out int y)
    {
        y = 0;
        if (_trackedRow == IntPtr.Zero)
            return false;
        if (GhosttyNative.ghostty_tracked_grid_ref_has_value(_trackedRow) == 0)
            return false;
        var coord = new GhosttyNative.GhosttyPointCoordinate();
        var rc = GhosttyNative.ghostty_tracked_grid_ref_point(
            _trackedRow,
            GhosttyNative.PointTagScreen,
            ref coord);
        if (rc != GhosttyNative.Success)
            return false;
        y = (int)coord.Y;
        return true;
    }

    private void ClearPinnedRowUnlocked()
    {
        if (_trackedRow == IntPtr.Zero)
            return;
        GhosttyNative.ghostty_tracked_grid_ref_free(_trackedRow);
        _trackedRow = IntPtr.Zero;
    }

    private int SetScrollOffsetFromBottomUnlocked(int offsetFromBottom)
    {
        if (!TryGetScrollbarUnlocked(out var total, out _, out var len))
        {
            ScrollViewportUnlocked(GhosttyNative.ScrollViewportBottom, default);
            return 0;
        }

        var maxOffset = total > len ? total - len : 0UL;
        var clamped = offsetFromBottom <= 0 ? 0UL : (ulong)offsetFromBottom;
        if (clamped > maxOffset)
            clamped = maxOffset;
        if (clamped == 0)
            ScrollViewportUnlocked(GhosttyNative.ScrollViewportBottom, default);
        else
        {
            var row = maxOffset - clamped;
            ScrollViewportUnlocked(
                GhosttyNative.ScrollViewportRow,
                new GhosttyNative.GhosttyTerminalScrollViewportValue { Row = (nuint)row });
        }

        // scrollbar after the viewport move instead of the clamped request.
        if (TryGetScrollbarUnlocked(out total, out var barOffset, out len))
        {
            var fromBottom = total > barOffset + len ? total - barOffset - len : 0UL;
            return ToIntOffset(fromBottom);
        }

        return ToIntOffset(clamped);
    }

    private unsafe bool TryGetScrollbarUnlocked(out ulong total, out ulong offset, out ulong len)
    {
        var bar = new GhosttyNative.GhosttyTerminalScrollbar();
        var rc = WithNativeGet(GhosttyNative.TerminalDataScrollbar, (IntPtr)(&bar));
        if (rc != GhosttyNative.Success)
        {
            total = 0;
            offset = 0;
            len = 0;
            return false;
        }

        total = bar.Total;
        offset = bar.Offset;
        len = bar.Len;
        return true;
    }

    private void ScrollViewportUnlocked(
        uint tag,
        GhosttyNative.GhosttyTerminalScrollViewportValue value)
    {
        var viewport = new GhosttyNative.GhosttyTerminalScrollViewport
        {
            Tag = tag,
            Value = value,
        };
        _ = WithNative(ptr =>
        {
            GhosttyNative.ghostty_terminal_scroll_viewport(ptr, viewport);
            return GhosttyNative.Success;
        });
    }

    private static int ToIntOffset(ulong value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;

    private VtStructuredSnapshot CaptureGridUnlocked(
        bool updateRenderState,
        out uint dirty,
        bool renderStateUpdateOk = true)
    {
        dirty = GhosttyNative.RenderStateDirtyFull;
        if (updateRenderState)
            renderStateUpdateOk = UpdateRenderStateUnlocked(out dirty);
        else
            EnsureCachedColorsUnlocked();

        var cols = _cols;
        var rows = _rows;
        var cells = new VtCellSnapshot[rows][];
        if (_resolveColors)
            CaptureViewportPaintCellsUnlocked(cells, cols, rows);
        else
        {
            for (var r = 0; r < rows; r++)
            {
                cells[r] = new VtCellSnapshot[cols];
                for (var c = 0; c < cols; c++)
                    cells[r][c] = ReadViewportCell((ushort)c, (uint)r);
            }
        }

        return SnapshotFromCellsUnlocked(
            cells,
            includeCursorShape: updateRenderState || _resolveColors,
            renderStateUpdateOk);
    }

    private VtCellSnapshot[][] EmptyCellGrid()
    {
        var cells = new VtCellSnapshot[_rows][];
        for (var r = 0; r < _rows; r++)
            cells[r] = [];
        return cells;
    }

    private VtStructuredSnapshot SnapshotFromCellsUnlocked(
        VtCellSnapshot[][] cells,
        bool includeCursorShape,
        bool renderStateUpdateOk = true)
    {
        var cols = _cols;
        var rows = _rows;
        VtCursorSnapshot? cursor;
        if (includeCursorShape)
        {
            // returns None when render_state.update fails. Do not read
            // the previous paint cursor.
            // returns None when HAS_VALUE is false or a get fails. Some
            // {visible:false} at origin is DECTCEM off, not None.
            if (!ShouldReadPaintCursor(includeCursorShape, renderStateUpdateOk)
                || !TryReadPaintCursorUnlocked(out var cursorCol, out var cursorRow, out var cursorVisible))
                cursor = null;
            else
            {
                cursor = new VtCursorSnapshot
                {
                    Col = cursorCol,
                    Row = cursorRow,
                    Visible = cursorVisible,
                    Shape = ReadCursorShapeUnlocked(),
                };
            }
        }
        else
        {
            cursor = new VtCursorSnapshot
            {
                Col = GetU16(GhosttyNative.TerminalDataCursorX),
                Row = GetU16(GhosttyNative.TerminalDataCursorY),
                Visible = GetBool(GhosttyNative.TerminalDataCursorVisible, defaultValue: false),
                Shape = 0,
            };
        }

        return new VtStructuredSnapshot
        {
            SchemaVersion = VtSnapshotNormalizer.SchemaVersion,
            Provider = Provider,
            Cols = cols,
            Rows = rows,
            Cursor = cursor,
            ActiveScreen = GetActiveScreenWire(),
            ScrollRegion = new VtScrollRegionSnapshot
            {
                Top = _scrollTop,
                Bottom = _scrollBottom,
            },
            Modes = CaptureModes(),
            Cells = cells,
        };
    }

    /// <summary>
    /// One row-iterator pass: read dirty per row, populate cells only when
    /// dirty. Iterator failure returns a null snapshot so the caller falls
    /// back to a full grid.
    /// </summary>
    private unsafe VtStructuredSnapshot? CaptureDirtyRowsUnlocked(
        bool renderStateUpdateOk,
        out IReadOnlyList<int>? dirtyRows)
    {
        dirtyRows = null;
        IntPtr iterator = IntPtr.Zero;
        IntPtr rowCells = IntPtr.Zero;
        var added = false;
        try
        {
            var rc = GhosttyNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out rowCells);
            if (rc != GhosttyNative.Success || rowCells == IntPtr.Zero)
                return null;

            rc = GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out iterator);
            if (rc != GhosttyNative.Success || iterator == IntPtr.Zero)
                return null;

            _renderState.DangerousAddRef(ref added);
            var iter = iterator;
            rc = GhosttyNative.ghostty_render_state_get(
                _renderState.DangerousGetHandle(),
                GhosttyNative.RenderStateDataRowIterator,
                (IntPtr)(&iter));
            if (rc != GhosttyNative.Success)
                return null;
            iterator = iter;

            var cols = _cols;
            var rows = _rows;
            var cells = new VtCellSnapshot[rows][];
            var dirty = new List<int>();
            for (var r = 0; r < rows; r++)
            {
                if (!GhosttyNative.ghostty_render_state_row_iterator_next(iterator))
                    return null;

                byte isDirty = 0;
                rc = GhosttyNative.ghostty_render_state_row_get(
                    iterator,
                    GhosttyNative.RenderStateRowDataDirty,
                    (IntPtr)(&isDirty));
                if (rc != GhosttyNative.Success || isDirty == 0)
                {
                    cells[r] = [];
                    continue;
                }

                var cellsHandle = rowCells;
                rc = GhosttyNative.ghostty_render_state_row_get(
                    iterator,
                    GhosttyNative.RenderStateRowDataCells,
                    (IntPtr)(&cellsHandle));
                if (rc == GhosttyNative.Success && cellsHandle != IntPtr.Zero)
                    rowCells = cellsHandle;
                var rowHandle = rc == GhosttyNative.Success ? rowCells : IntPtr.Zero;
                cells[r] = new VtCellSnapshot[cols];
                for (var c = 0; c < cols; c++)
                {
                    if (rowHandle != IntPtr.Zero)
                        _ = GhosttyNative.ghostty_render_state_row_cells_select(rowHandle, (ushort)c);
                    cells[r][c] = ReadViewportCell(
                        (ushort)c,
                        (uint)r,
                        rowHandle != IntPtr.Zero ? rowHandle : IntPtr.Zero);
                }

                dirty.Add(r);
            }

            dirtyRows = dirty;
            return SnapshotFromCellsUnlocked(cells, includeCursorShape: true, renderStateUpdateOk);
        }
        catch
        {
            dirtyRows = null;
            return null;
        }
        finally
        {
            if (added)
                _renderState.DangerousRelease();
            if (iterator != IntPtr.Zero)
                GhosttyNative.ghostty_render_state_row_iterator_free(iterator);
            if (rowCells != IntPtr.Zero)
                GhosttyNative.ghostty_render_state_row_cells_free(rowCells);
        }
    }

    private static unsafe string ReadGraphemeText(in GhosttyNative.GhosttyGridRef gridRef)
    {
        var rc = GhosttyNative.ghostty_grid_ref_graphemes(
            in gridRef, IntPtr.Zero, 0, out var required);
        if (rc != GhosttyNative.OutOfSpace && rc != GhosttyNative.Success)
            return " ";
        if (required == 0)
            return " ";

        var count = checked((int)required);
        Span<uint> codepoints = count <= 32
            ? stackalloc uint[count]
            : new uint[count];
        fixed (uint* p = codepoints)
        {
            rc = GhosttyNative.ghostty_grid_ref_graphemes(
                in gridRef, (IntPtr)p, (nuint)count, out var written);
            if (rc != GhosttyNative.Success || written == 0)
                return " ";
            count = checked((int)written);
        }

        // Encode Unicode scalar values as UTF-16 (handles non-BMP).
        var sb = new StringBuilder(count * 2);
        for (var i = 0; i < count; i++)
        {
            var cp = codepoints[i];
            if (cp == 0)
                continue;
            if (cp <= 0xFFFF)
                sb.Append((char)cp);
            else if (cp <= 0x10FFFF)
                sb.Append(char.ConvertFromUtf32(checked((int)cp)));
        }

        return sb.Length == 0 ? " " : sb.ToString();
    }

    private void CaptureViewportPaintCellsUnlocked(VtCellSnapshot[][] cells, int cols, int rows)
    {
        IntPtr iterator = IntPtr.Zero;
        IntPtr rowCells = IntPtr.Zero;
        var added = false;
        try
        {
            var rc = GhosttyNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out rowCells);
            if (rc != GhosttyNative.Success || rowCells == IntPtr.Zero)
            {
                CaptureViewportGridRefUnlocked(cells, cols, rows, IntPtr.Zero);
                return;
            }

            rc = GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out iterator);
            if (rc != GhosttyNative.Success || iterator == IntPtr.Zero)
            {
                CaptureViewportGridRefUnlocked(cells, cols, rows, IntPtr.Zero);
                return;
            }

            _renderState.DangerousAddRef(ref added);
            var iter = iterator;
            unsafe
            {
                rc = GhosttyNative.ghostty_render_state_get(
                    _renderState.DangerousGetHandle(),
                    GhosttyNative.RenderStateDataRowIterator,
                    (IntPtr)(&iter));
            }

            if (rc != GhosttyNative.Success)
            {
                CaptureViewportGridRefUnlocked(cells, cols, rows, IntPtr.Zero);
                return;
            }

            iterator = iter;
            for (var r = 0; r < rows; r++)
            {
                cells[r] = new VtCellSnapshot[cols];
                if (!GhosttyNative.ghostty_render_state_row_iterator_next(iterator))
                {
                    for (var c = 0; c < cols; c++)
                        cells[r][c] = ReadViewportCell((ushort)c, (uint)r);
                    for (var rest = r + 1; rest < rows; rest++)
                    {
                        cells[rest] = new VtCellSnapshot[cols];
                        for (var c = 0; c < cols; c++)
                            cells[rest][c] = ReadViewportCell((ushort)c, (uint)rest);
                    }

                    return;
                }

                var cellsHandle = rowCells;
                unsafe
                {
                    rc = GhosttyNative.ghostty_render_state_row_get(
                        iterator,
                        GhosttyNative.RenderStateRowDataCells,
                        (IntPtr)(&cellsHandle));
                }

                if (rc == GhosttyNative.Success && cellsHandle != IntPtr.Zero)
                    rowCells = cellsHandle;

                var rowHandle = rc == GhosttyNative.Success ? rowCells : IntPtr.Zero;
                for (var c = 0; c < cols; c++)
                {
                    if (rowHandle != IntPtr.Zero)
                        _ = GhosttyNative.ghostty_render_state_row_cells_select(rowHandle, (ushort)c);
                    cells[r][c] = ReadViewportCell(
                        (ushort)c,
                        (uint)r,
                        rowHandle != IntPtr.Zero ? rowHandle : IntPtr.Zero);
                }
            }
        }
        finally
        {
            if (added)
                _renderState.DangerousRelease();
            if (iterator != IntPtr.Zero)
                GhosttyNative.ghostty_render_state_row_iterator_free(iterator);
            if (rowCells != IntPtr.Zero)
                GhosttyNative.ghostty_render_state_row_cells_free(rowCells);
        }
    }

    private void CaptureViewportGridRefUnlocked(
        VtCellSnapshot[][] cells,
        int cols,
        int rows,
        IntPtr rowCells)
    {
        for (var r = 0; r < rows; r++)
        {
            cells[r] = new VtCellSnapshot[cols];
            for (var c = 0; c < cols; c++)
                cells[r][c] = ReadViewportCell((ushort)c, (uint)r, rowCells);
        }
    }

    private static void EnsureStampDest(VtFrameCell[] dest, int cols, int rows)
    {
        // equal width * height.
        if (dest.Length != cols * rows)
            throw new ArgumentException("stamp dest length must equal cols * rows.", nameof(dest));
    }

    /// <summary>
    // / Live stamp cursor from render_state.
    /// <c>src/pane/terminal.rs:2268-2298</c>. Failed update, failed get,
    /// or HAS_VALUE false is <see cref="VtFrameCursor.None"/>. Hidden
    /// DECTCEM with HAS_VALUE true stays Some visible false.
    /// </summary>
    private VtFrameCursor ReadPaintCursorUnlocked(bool renderStateUpdateOk)
    {
        if (!ShouldReadPaintCursor(includeCursorShape: true, renderStateUpdateOk)
            || !TryReadPaintCursorUnlocked(out var col, out var row, out var visible))
            return VtFrameCursor.None;
        return new VtFrameCursor(col, row, visible, ReadCursorShapeUnlocked());
    }

    private VtFrameModes CaptureFrameModes()
    {
        var modes = CaptureModes();
        return new VtFrameModes(
            modes.Origin,
            modes.AutoWrap,
            modes.Insert,
            modes.BracketedPaste,
            modes.Mouse,
            modes.FocusReporting,
            modes.Sync,
            modes.MouseEncoding,
            modes.ApplicationCursor);
    }

    private void StampViewportCellsUnlocked(VtFrameCell[] dest, int cols, int rows, VtStampTables tables)
    {
        IntPtr iterator = IntPtr.Zero;
        IntPtr rowCells = IntPtr.Zero;
        var added = false;
        try
        {
            var rc = GhosttyNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out rowCells);
            if (rc != GhosttyNative.Success || rowCells == IntPtr.Zero)
            {
                StampViewportGridRefUnlocked(dest, cols, rows, tables, IntPtr.Zero);
                return;
            }

            rc = GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out iterator);
            if (rc != GhosttyNative.Success || iterator == IntPtr.Zero)
            {
                StampViewportGridRefUnlocked(dest, cols, rows, tables, IntPtr.Zero);
                return;
            }

            _renderState.DangerousAddRef(ref added);
            var iter = iterator;
            unsafe
            {
                rc = GhosttyNative.ghostty_render_state_get(
                    _renderState.DangerousGetHandle(),
                    GhosttyNative.RenderStateDataRowIterator,
                    (IntPtr)(&iter));
            }

            if (rc != GhosttyNative.Success)
            {
                StampViewportGridRefUnlocked(dest, cols, rows, tables, IntPtr.Zero);
                return;
            }

            iterator = iter;
            for (var r = 0; r < rows; r++)
            {
                if (!GhosttyNative.ghostty_render_state_row_iterator_next(iterator))
                {
                    for (var c = 0; c < cols; c++)
                        dest[(r * cols) + c] = StampViewportCell((ushort)c, (uint)r, IntPtr.Zero, tables);
                    for (var rest = r + 1; rest < rows; rest++)
                    {
                        for (var c = 0; c < cols; c++)
                            dest[(rest * cols) + c] = StampViewportCell((ushort)c, (uint)rest, IntPtr.Zero, tables);
                    }

                    return;
                }

                var cellsHandle = rowCells;
                unsafe
                {
                    rc = GhosttyNative.ghostty_render_state_row_get(
                        iterator,
                        GhosttyNative.RenderStateRowDataCells,
                        (IntPtr)(&cellsHandle));
                }

                if (rc == GhosttyNative.Success && cellsHandle != IntPtr.Zero)
                    rowCells = cellsHandle;

                var rowHandle = rc == GhosttyNative.Success ? rowCells : IntPtr.Zero;
                for (var c = 0; c < cols; c++)
                {
                    if (rowHandle != IntPtr.Zero)
                        _ = GhosttyNative.ghostty_render_state_row_cells_select(rowHandle, (ushort)c);
                    dest[(r * cols) + c] = StampViewportCell(
                        (ushort)c,
                        (uint)r,
                        rowHandle != IntPtr.Zero ? rowHandle : IntPtr.Zero,
                        tables);
                }
            }
        }
        finally
        {
            if (added)
                _renderState.DangerousRelease();
            if (iterator != IntPtr.Zero)
                GhosttyNative.ghostty_render_state_row_iterator_free(iterator);
            if (rowCells != IntPtr.Zero)
                GhosttyNative.ghostty_render_state_row_cells_free(rowCells);
        }
    }

    private void StampViewportGridRefUnlocked(
        VtFrameCell[] dest,
        int cols,
        int rows,
        VtStampTables tables,
        IntPtr rowCells)
    {
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
                dest[(r * cols) + c] = StampViewportCell((ushort)c, (uint)r, rowCells, tables);
        }
    }

    private VtFrameCell StampViewportCell(ushort x, uint y, IntPtr rowCells, VtStampTables tables) =>
        StampCellAt(GhosttyNative.ViewportPoint(x, y), rowCells, tables);

    private VtFrameCell StampCellAt(
        GhosttyNative.GhosttyPoint point,
        IntPtr rowCells,
        VtStampTables tables)
    {
        var gridRef = GhosttyNative.EmptyGridRef();
        var rc = WithNative(ptr =>
        {
            var local = gridRef;
            var code = GhosttyNative.ghostty_terminal_grid_ref(ptr, point, ref local);
            gridRef = local;
            return code;
        });
        if (rc != GhosttyNative.Success)
            return VtFrameCell.Blank;

        var wide = GhosttyNative.CellWideNarrow;
        if (GhosttyNative.ghostty_grid_ref_cell(in gridRef, out var cell) == GhosttyNative.Success)
        {
            unsafe
            {
                uint wideVal = 0;
                if (GhosttyNative.ghostty_cell_get(
                        cell,
                        GhosttyNative.CellDataWide,
                        (IntPtr)(&wideVal)) == GhosttyNative.Success)
                {
                    wide = wideVal;
                }
            }
        }

        var isContinuation = wide is GhosttyNative.CellWideSpacerTail
            or GhosttyNative.CellWideSpacerHead;
        var width = wide == GhosttyNative.CellWideWide ? 2 : 1;
        var text = isContinuation ? VtGlyphIntern.Space : ReadGraphemeInterned(in gridRef);
        var packed = ReadPackedStyle(in gridRef, rowCells);
        // The mux does not read OSC 8 from Ghostty at this pin.
        return tables.PackCell(text, width, isContinuation, in packed, hyperlink: null);
    }

    private string ReadGraphemeInterned(in GhosttyNative.GhosttyGridRef gridRef)
    {
        var rc = GhosttyNative.ghostty_grid_ref_graphemes(
            in gridRef, IntPtr.Zero, 0, out var required);
        if (rc != GhosttyNative.OutOfSpace && rc != GhosttyNative.Success)
            return VtGlyphIntern.Space;
        if (required == 0)
            return VtGlyphIntern.Space;

        var count = checked((int)required);
        if (count > _graphemeCodepoints.Length)
            _graphemeCodepoints = new uint[count];
        unsafe
        {
            fixed (uint* p = _graphemeCodepoints)
            {
                rc = GhosttyNative.ghostty_grid_ref_graphemes(
                    in gridRef, (IntPtr)p, (nuint)count, out var written);
                if (rc != GhosttyNative.Success || written == 0)
                    return VtGlyphIntern.Space;
                count = checked((int)written);
            }
        }

        var chars = 0;
        for (var i = 0; i < count; i++)
        {
            var cp = _graphemeCodepoints[i];
            if (cp == 0)
                continue;
            var need = cp <= 0xFFFF ? 1 : 2;
            if (chars + need > _graphemeChars.Length)
                Array.Resize(ref _graphemeChars, Math.Max(_graphemeChars.Length * 2, chars + need));
            if (cp <= 0xFFFF)
                _graphemeChars[chars++] = (char)cp;
            else if (cp <= 0x10FFFF)
            {
                var pair = char.ConvertFromUtf32(checked((int)cp));
                _graphemeChars[chars++] = pair[0];
                _graphemeChars[chars++] = pair[1];
            }
        }

        if (chars == 0)
            return VtGlyphIntern.Space;
        if (chars == 1)
            return VtGlyphIntern.Intern(_graphemeChars[0]);
        return new string(_graphemeChars, 0, chars);
    }

    private VtPackedStyle ReadPackedStyle(in GhosttyNative.GhosttyGridRef gridRef, IntPtr rowCells)
    {
        var native = GhosttyNative.EmptyStyle();
        GhosttyNative.ghostty_style_default(ref native);
        native.Size = (nuint)Marshal.SizeOf<GhosttyNative.GhosttyStyle>();

        var rc = GhosttyNative.ghostty_grid_ref_style(in gridRef, ref native);
        var success = !ForceStyleGetFailure && rc == GhosttyNative.Success;
        uint fg = 0;
        uint bg = 0;
        uint underline = 0;
        ushort modifier = 0;
        byte underlineStyle = 0;
        if (success)
        {
            fg = PackNativeColor(native.FgColor);
            bg = PackNativeColor(native.BgColor);
            underline = PackNativeColor(native.UnderlineColor);
            modifier = VtStyleBits.Pack(
                native.Bold != 0,
                native.Faint != 0,
                native.Italic != 0,
                native.Underline != 0,
                native.Inverse != 0,
                native.Invisible != 0,
                native.Strikethrough != 0,
                native.Blink != 0,
                native.Overline != 0);
            underlineStyle = native.Underline is >= 0 and <= 255
                ? (byte)native.Underline
                : (byte)0;
        }

        if (!success)
            return default;
        if (!_resolveColors)
            return new VtPackedStyle(fg, bg, underline, modifier, underlineStyle);

        var colors = _cachedColors ?? LoadDefaultColors();
        _cachedColors ??= colors;
        ReadRowCellsRgbCalls += 2;
        var contentFg = ReadRowCellsRgbPacked(rowCells, GhosttyNative.RowCellsDataFgColor);
        var contentBg = ReadRowCellsRgbPacked(rowCells, GhosttyNative.RowCellsDataBgColor);
        ReadContentBgTokenCalls++;
        var contentBgTag = ReadContentBgPacked(in gridRef);
        return GhosttyCellStyle.ResolvePacked(
            fg, bg, underline, modifier, underlineStyle, colors, contentFg, contentBg, contentBgTag);
    }

    private static uint PackNativeColor(GhosttyNative.GhosttyStyleColor color)
    {
        return color.Tag switch
        {
            GhosttyNative.StyleColorPalette => VtColorPack.FromPalette(color.Value.Palette),
            GhosttyNative.StyleColorRgb => VtColorPack.FromRgb(
                color.Value.Rgb.R, color.Value.Rgb.G, color.Value.Rgb.B),
            _ => 0,
        };
    }

    private static unsafe uint ReadRowCellsRgbPacked(IntPtr rowCells, uint data)
    {
        if (rowCells == IntPtr.Zero)
            return 0;

        var rgb = new GhosttyNative.GhosttyColorRgb();
        var rc = GhosttyNative.ghostty_render_state_row_cells_get(rowCells, data, (IntPtr)(&rgb));
        if (rc != GhosttyNative.Success)
            return 0;
        return VtColorPack.FromRgb(rgb.R, rgb.G, rgb.B);
    }

    private static unsafe uint ReadContentBgPacked(in GhosttyNative.GhosttyGridRef gridRef)
    {
        if (GhosttyNative.ghostty_grid_ref_cell(in gridRef, out var cell) != GhosttyNative.Success)
            return 0;

        uint tag = 0;
        if (GhosttyNative.ghostty_cell_get(
                cell,
                GhosttyNative.CellDataContentTag,
                (IntPtr)(&tag)) != GhosttyNative.Success)
        {
            return 0;
        }

        if (tag == GhosttyNative.CellContentBgPalette)
        {
            byte index = 0;
            if (GhosttyNative.ghostty_cell_get(
                    cell,
                    GhosttyNative.CellDataColorPalette,
                    (IntPtr)(&index)) != GhosttyNative.Success)
            {
                return 0;
            }

            return VtColorPack.FromPalette(index);
        }

        if (tag == GhosttyNative.CellContentBgRgb)
        {
            var rgb = new GhosttyNative.GhosttyColorRgb();
            if (GhosttyNative.ghostty_cell_get(
                    cell,
                    GhosttyNative.CellDataColorRgb,
                    (IntPtr)(&rgb)) != GhosttyNative.Success)
            {
                return 0;
            }

            return VtColorPack.FromRgb(rgb.R, rgb.G, rgb.B);
        }

        return 0;
    }

    private VtCellStyleSnapshot ReadStyle(in GhosttyNative.GhosttyGridRef gridRef, IntPtr rowCells)
    {
        var native = GhosttyNative.EmptyStyle();
        GhosttyNative.ghostty_style_default(ref native);
        native.Size = (nuint)Marshal.SizeOf<GhosttyNative.GhosttyStyle>();

        var rc = GhosttyNative.ghostty_grid_ref_style(in gridRef, ref native);
        var success = !ForceStyleGetFailure && rc == GhosttyNative.Success;
        var raw = success
            ? new VtCellStyleSnapshot
            {
                Fg = FormatColor(native.FgColor),
                Bg = FormatColor(native.BgColor),
                Bold = native.Bold != 0,
                Dim = native.Faint != 0,
                Italic = native.Italic != 0,
                Underline = native.Underline != 0,
                Inverse = native.Inverse != 0,
                Invisible = native.Invisible != 0,
                Strikethrough = native.Strikethrough != 0,
                Blink = native.Blink != 0,
                Overline = native.Overline != 0,
                UnderlineColor = FormatColor(native.UnderlineColor),
                UnderlineStyle = native.Underline,
            }
            : VtCellStyleSnapshot.Default;
        if (!success)
            return VtCellStyleSnapshot.Default;
        if (!_resolveColors)
            return StyleAfterNativeGet(success, resolveColors: false, raw, ResolveStyle);

        var colors = _cachedColors ?? LoadDefaultColors();
        _cachedColors ??= colors;
        ReadRowCellsRgbCalls += 2;
        var contentFg = ReadRowCellsRgb(rowCells, GhosttyNative.RowCellsDataFgColor);
        var contentBg = ReadRowCellsRgb(rowCells, GhosttyNative.RowCellsDataBgColor);
        ReadContentBgTokenCalls++;
        var contentBgTag = ReadContentBgToken(in gridRef);
        return GhosttyCellStyle.Resolve(raw, colors, contentFg, contentBg, contentBgTag);
    }

    private static unsafe string? ReadRowCellsRgb(IntPtr rowCells, uint data)
    {
        if (rowCells == IntPtr.Zero)
            return null;

        var rgb = new GhosttyNative.GhosttyColorRgb();
        var rc = GhosttyNative.ghostty_render_state_row_cells_get(rowCells, data, (IntPtr)(&rgb));
        if (rc != GhosttyNative.Success)
            return null;
        return FormatRgb(rgb);
    }

    private static unsafe string? ReadContentBgToken(in GhosttyNative.GhosttyGridRef gridRef)
    {
        if (GhosttyNative.ghostty_grid_ref_cell(in gridRef, out var cell) != GhosttyNative.Success)
            return null;

        uint tag = 0;
        if (GhosttyNative.ghostty_cell_get(
                cell,
                GhosttyNative.CellDataContentTag,
                (IntPtr)(&tag)) != GhosttyNative.Success)
        {
            return null;
        }

        if (tag == GhosttyNative.CellContentBgPalette)
        {
            byte index = 0;
            if (GhosttyNative.ghostty_cell_get(
                    cell,
                    GhosttyNative.CellDataColorPalette,
                    (IntPtr)(&index)) != GhosttyNative.Success)
            {
                return null;
            }

            return "palette:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (tag == GhosttyNative.CellContentBgRgb)
        {
            var rgb = new GhosttyNative.GhosttyColorRgb();
            if (GhosttyNative.ghostty_cell_get(
                    cell,
                    GhosttyNative.CellDataColorRgb,
                    (IntPtr)(&rgb)) != GhosttyNative.Success)
            {
                return null;
            }

            return FormatRgb(rgb);
        }

        return null;
    }

    private static string FormatRgb(GhosttyNative.GhosttyColorRgb rgb) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}");

    /// <summary>
    /// Style-get miss keeps raw default tags when colours are not resolved.
    /// Attach paint still runs the colour resolver for a successful get.
    /// </summary>
    internal static VtCellStyleSnapshot StyleAfterNativeGet(
        bool success,
        bool resolveColors,
        VtCellStyleSnapshot raw,
        Func<VtCellStyleSnapshot, VtCellStyleSnapshot> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (!success)
            return resolveColors ? resolve(VtCellStyleSnapshot.Default) : VtCellStyleSnapshot.Default;
        return resolveColors ? resolve(raw) : raw;
    }

    private VtCellStyleSnapshot ResolveStyle(VtCellStyleSnapshot raw)
    {
        var colors = _cachedColors ?? LoadDefaultColors();
        _cachedColors ??= colors;
        return GhosttyCellStyle.Resolve(raw, colors);
    }

    private static string? FormatColor(GhosttyNative.GhosttyStyleColor color)
    {
        return color.Tag switch
        {
            GhosttyNative.StyleColorPalette =>
                "palette:" + color.Value.Palette.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GhosttyNative.StyleColorRgb =>
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"#{color.Value.Rgb.R:X2}{color.Value.Rgb.G:X2}{color.Value.Rgb.B:X2}"),
            _ => null,
        };
    }

    /// <summary>
    /// <c>render_state.update(terminal).ok()?</c>. A failed update must
    /// not read the previous paint cursor.
    /// </summary>
    internal static bool ShouldReadPaintCursor(bool includeCursorShape, bool renderStateUpdateOk) =>
        includeCursorShape && renderStateUpdateOk;

    /// <summary>
    /// Paint-path cursor from render_state. Failed get or HAS_VALUE false
    // / is no cursor, never visible.
    /// <c>src/pane/terminal.rs:2279-2298</c>.
    /// </summary>
    private bool TryReadPaintCursorUnlocked(out int col, out int row, out bool visible)
    {
        col = 0;
        row = 0;
        visible = false;
        if (!TryRenderStateGetBool(GhosttyNative.RenderStateDataCursorViewportHasValue, out var hasValue)
            || !hasValue)
        {
            return false;
        }

        if (!TryRenderStateGetU16(GhosttyNative.RenderStateDataCursorViewportX, out var x)
            || !TryRenderStateGetU16(GhosttyNative.RenderStateDataCursorViewportY, out var y))
        {
            return false;
        }

        if (!TryRenderStateGetBool(GhosttyNative.RenderStateDataCursorVisible, out var modeVisible))
            return false;

        col = x;
        row = y;
        visible = modeVisible;
        return true;
    }

    internal static bool TryRenderStateGetBool(int rc, byte value, out bool result)
    {
        if (rc != GhosttyNative.Success)
        {
            result = false;
            return false;
        }

        result = value != 0;
        return true;
    }

    internal static bool TryRenderStateGetU16(int rc, ushort value, out ushort result)
    {
        if (rc != GhosttyNative.Success)
        {
            result = 0;
            return false;
        }

        result = value;
        return true;
    }

    private unsafe bool TryRenderStateGetBool(uint data, out bool result)
    {
        var added = false;
        try
        {
            _renderState.DangerousAddRef(ref added);
            byte value = 0;
            var rc = GhosttyNative.ghostty_render_state_get(
                _renderState.DangerousGetHandle(),
                data,
                (IntPtr)(&value));
            return TryRenderStateGetBool(rc, value, out result);
        }
        finally
        {
            if (added)
                _renderState.DangerousRelease();
        }
    }

    private unsafe bool TryRenderStateGetU16(uint data, out ushort result)
    {
        var added = false;
        try
        {
            _renderState.DangerousAddRef(ref added);
            ushort value = 0;
            var rc = GhosttyNative.ghostty_render_state_get(
                _renderState.DangerousGetHandle(),
                data,
                (IntPtr)(&value));
            return TryRenderStateGetU16(rc, value, out result);
        }
        finally
        {
            if (added)
                _renderState.DangerousRelease();
        }
    }

    private unsafe int ReadCursorShapeUnlocked()
    {
        var added = false;
        try
        {
            _renderState.DangerousAddRef(ref added);
            uint style = GhosttyNative.RenderStateCursorStyleBlock;
            var rc = GhosttyNative.ghostty_render_state_get(
                _renderState.DangerousGetHandle(),
                GhosttyNative.RenderStateDataCursorVisualStyle,
                (IntPtr)(&style));
            if (rc != GhosttyNative.Success)
                return 0;

            byte blinking = 0;
            rc = GhosttyNative.ghostty_render_state_get(
                _renderState.DangerousGetHandle(),
                GhosttyNative.RenderStateDataCursorBlinking,
                (IntPtr)(&blinking));
            var blink = rc == GhosttyNative.Success && blinking != 0;
            return DecscusrFromVisualStyle(style, blink);
        }
        finally
        {
            if (added)
                _renderState.DangerousRelease();
        }
    }

    private static int DecscusrFromVisualStyle(uint style, bool blinking)
    {
        if (style == GhosttyNative.RenderStateCursorStyleUnderline)
            return blinking ? 3 : 4;
        if (style == GhosttyNative.RenderStateCursorStyleBar)
            return blinking ? 5 : 6;
        // Block and hollow block share DECSCUSR 1/2.
        return blinking ? 1 : 2;
    }

    /// <summary>
    // / Update Ghostty render_state.
    /// <c>src/pane/terminal.rs:2275-2276</c>
    /// <c>render_state.update(terminal).ok()?</c> returns None on failure.
    /// </summary>
    private bool UpdateRenderStateUnlocked(out uint dirty)
    {
        dirty = GhosttyNative.RenderStateDirtyFull;
        if (ForceRenderStateUpdateFailure)
            return false;

        var addedState = false;
        var addedTerm = false;
        try
        {
            _renderState.DangerousAddRef(ref addedState);
            _terminal.DangerousAddRef(ref addedTerm);
            var rc = GhosttyNative.ghostty_render_state_update(
                _renderState.DangerousGetHandle(),
                _terminal.DangerousGetHandle());
            if (rc != GhosttyNative.Success)
                return false;

            unsafe
            {
                uint value = GhosttyNative.RenderStateDirtyFull;
                rc = GhosttyNative.ghostty_render_state_get(
                    _renderState.DangerousGetHandle(),
                    GhosttyNative.RenderStateDataDirty,
                    (IntPtr)(&value));
                if (rc == GhosttyNative.Success)
                    dirty = value;
            }

            CacheColorsUnlocked();
            return true;
        }
        finally
        {
            if (addedTerm)
                _terminal.DangerousRelease();
            if (addedState)
                _renderState.DangerousRelease();
        }
    }

    private void EnsureCachedColorsUnlocked()
    {
        if (_cachedColors is not null)
            return;
        CacheColorsUnlocked();
        _cachedColors ??= LoadDefaultColors();
    }

    /// <summary>
    // A failed get leaves
    /// both initials null, so later default emit is always reset.
    /// </summary>
    private void CacheInitialDefaultColorsUnlocked()
    {
        var addedState = false;
        var addedTerm = false;
        try
        {
            _renderState.DangerousAddRef(ref addedState);
            _terminal.DangerousAddRef(ref addedTerm);
            var rc = GhosttyNative.ghostty_render_state_update(
                _renderState.DangerousGetHandle(),
                _terminal.DangerousGetHandle());
            if (rc != GhosttyNative.Success)
                return;
        }
        finally
        {
            if (addedTerm)
                _terminal.DangerousRelease();
            if (addedState)
                _renderState.DangerousRelease();
        }

        if (!TryReadRenderStateColorsUnlocked(out var colors))
            return;
        _initialDefaultForeground = new GhosttyCellStyle.Rgb(
            colors.Foreground.R, colors.Foreground.G, colors.Foreground.B);
        _initialDefaultBackground = new GhosttyCellStyle.Rgb(
            colors.Background.R, colors.Background.G, colors.Background.B);
    }

    private void CacheColorsUnlocked()
    {
        if (!TryReadRenderStateColorsUnlocked(out var colors))
            return;

        var palette = new GhosttyCellStyle.Rgb[GhosttyCellStyle.PaletteSize];
        colors.CopyPalette(palette);
        var fg = new GhosttyCellStyle.Rgb(colors.Foreground.R, colors.Foreground.G, colors.Foreground.B);
        var bg = new GhosttyCellStyle.Rgb(colors.Background.R, colors.Background.G, colors.Background.B);
        // host-matching default encodes as reset (packed 0). Else emit RGB
        // only when the current default differs from the pane initial default.
        _cachedColors = new GhosttyCellStyle.ColorSet(
            palette,
            EmitDefault(fg, _initialDefaultForeground, _hostDefaultForeground),
            EmitDefault(bg, _initialDefaultBackground, _hostDefaultBackground),
            fg,
            bg,
            _appliedDefaultPalette.Length == GhosttyCellStyle.PaletteSize
                ? _appliedDefaultPalette
                : null);
    }

    /// <summary>
    // / Store host OSC 10/11 RGB for encode.
    /// against <c>host_theme.background</c> in <c>ghostty_default_bg</c>.
    /// </summary>
    public void SetHostDefaultColors(HostRgb? foreground, HostRgb? background)
    {
        lock (_gate)
        {
            _hostDefaultForeground = foreground is { } fg
                ? new GhosttyCellStyle.Rgb(fg.R, fg.G, fg.B)
                : null;
            _hostDefaultBackground = background is { } bg
                ? new GhosttyCellStyle.Rgb(bg.R, bg.G, bg.B)
                : null;
            _cachedColors = null;
        }
    }

    /// <summary>
    /// on <c>ghostty_color_palette_default</c>, then
    /// <c>ghostty_terminal_set(GHOSTTY_TERMINAL_OPT_COLOR_PALETTE)</c>
    /// (<c>src/ghostty/mod.rs:874-887</c>). Native set error fails this apply.
    /// Do not Feed OSC 4 as a substitute.
    /// </summary>
    public void SetDefaultPalette(HostPalette palette)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var merged = new GhosttyNative.GhosttyColorRgb[GhosttyCellStyle.PaletteSize];
            unsafe
            {
                fixed (GhosttyNative.GhosttyColorRgb* p = merged)
                    GhosttyNative.ghostty_color_palette_default((IntPtr)p);
            }

            for (var i = 0; i < GhosttyCellStyle.PaletteSize; i++)
            {
                if (palette[i] is { } color)
                {
                    merged[i].R = color.R;
                    merged[i].G = color.G;
                    merged[i].B = color.B;
                }
            }

            var applied = new GhosttyCellStyle.Rgb[GhosttyCellStyle.PaletteSize];
            for (var i = 0; i < applied.Length; i++)
                applied[i] = new GhosttyCellStyle.Rgb(merged[i].R, merged[i].G, merged[i].B);

            var added = false;
            try
            {
                _terminal.DangerousAddRef(ref added);
                unsafe
                {
                    fixed (GhosttyNative.GhosttyColorRgb* p = merged)
                    {
                        var rc = GhosttyNative.ghostty_terminal_set(
                            _terminal.DangerousGetHandle(),
                            GhosttyNative.TerminalOptColorPalette,
                            (IntPtr)p);
                        if (rc != GhosttyNative.Success)
                        {
                            throw new InvalidOperationException(
                                $"ghostty_terminal_set COLOR_PALETTE failed with code {rc}");
                        }
                    }
                }
            }
            finally
            {
                if (added)
                    _terminal.DangerousRelease();
            }

            _appliedDefaultPalette = applied;
            _cachedColors = null;
        }
    }

    internal GhosttyCellStyle.Rgb[] AppliedDefaultPalette
    {
        get
        {
            lock (_gate)
                return _appliedDefaultPalette;
        }
    }

    private static GhosttyCellStyle.Rgb? EmitDefault(
        GhosttyCellStyle.Rgb current,
        GhosttyCellStyle.Rgb? initial,
        GhosttyCellStyle.Rgb? host)
    {
        if (host is { } stored && stored == current)
            return null;
        if (initial is { } start && start != current)
            return current;
        return null;
    }

    private bool TryReadRenderStateColorsUnlocked(out GhosttyNative.GhosttyRenderStateColors colors)
    {
        colors = new GhosttyNative.GhosttyRenderStateColors
        {
            Size = (nuint)GhosttyNative.RenderStateColorsSize,
        };
        var added = false;
        try
        {
            _renderState.DangerousAddRef(ref added);
            unsafe
            {
                var local = colors;
                var rc = GhosttyNative.ghostty_render_state_colors_get(
                    _renderState.DangerousGetHandle(),
                    &local);
                if (rc != GhosttyNative.Success)
                    return false;
                colors = local;
                return true;
            }
        }
        finally
        {
            if (added)
                _renderState.DangerousRelease();
        }
    }

    private static GhosttyCellStyle.ColorSet LoadDefaultColors()
    {
        var palette = new GhosttyCellStyle.Rgb[GhosttyCellStyle.PaletteSize];
        var native = new GhosttyNative.GhosttyColorRgb[GhosttyCellStyle.PaletteSize];
        unsafe
        {
            fixed (GhosttyNative.GhosttyColorRgb* p = native)
                GhosttyNative.ghostty_color_palette_default((IntPtr)p);
        }

        for (var i = 0; i < palette.Length; i++)
            palette[i] = new GhosttyCellStyle.Rgb(native[i].R, native[i].G, native[i].B);
        return GhosttyCellStyle.ColorSet.FromPalette(palette);
    }

    private unsafe IReadOnlyList<int>? CollectDirtyRowsUnlocked()
    {
        IntPtr iterator = IntPtr.Zero;
        var added = false;
        try
        {
            var rc = GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out iterator);
            if (rc != GhosttyNative.Success || iterator == IntPtr.Zero)
                return null;

            _renderState.DangerousAddRef(ref added);
            var iter = iterator;
            rc = GhosttyNative.ghostty_render_state_get(
                _renderState.DangerousGetHandle(),
                GhosttyNative.RenderStateDataRowIterator,
                (IntPtr)(&iter));
            if (rc != GhosttyNative.Success)
                return null;
            iterator = iter;

            var rows = new List<int>();
            var y = 0;
            while (y < _rows && GhosttyNative.ghostty_render_state_row_iterator_next(iterator))
            {
                byte dirty = 0;
                rc = GhosttyNative.ghostty_render_state_row_get(
                    iterator,
                    GhosttyNative.RenderStateRowDataDirty,
                    (IntPtr)(&dirty));
                if (rc == GhosttyNative.Success && dirty != 0)
                    rows.Add(y);
                y++;
            }

            return rows;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (added)
                _renderState.DangerousRelease();
            if (iterator != IntPtr.Zero)
                GhosttyNative.ghostty_render_state_row_iterator_free(iterator);
        }
    }

    private void ClearDirtyUnlocked()
    {
        // ABI: setting global dirty does not unset per-row dirty. Walk rows
        // first, then set global Clean. Iterator failure leaves dirty intact
        // so the next capture falls back to a full grid instead of a patch.
        if (!ClearRowDirtyUnlocked())
            return;

        var added = false;
        try
        {
            _renderState.DangerousAddRef(ref added);
            unsafe
            {
                uint clean = GhosttyNative.RenderStateDirtyClean;
                _ = GhosttyNative.ghostty_render_state_set(
                    _renderState.DangerousGetHandle(),
                    GhosttyNative.RenderStateOptionDirty,
                    (IntPtr)(&clean));
            }
        }
        finally
        {
            if (added)
                _renderState.DangerousRelease();
        }
    }

    private unsafe bool ClearRowDirtyUnlocked()
    {
        IntPtr iterator = IntPtr.Zero;
        var added = false;
        try
        {
            var rc = GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out iterator);
            if (rc != GhosttyNative.Success || iterator == IntPtr.Zero)
                return false;

            _renderState.DangerousAddRef(ref added);
            var iter = iterator;
            rc = GhosttyNative.ghostty_render_state_get(
                _renderState.DangerousGetHandle(),
                GhosttyNative.RenderStateDataRowIterator,
                (IntPtr)(&iter));
            if (rc != GhosttyNative.Success)
                return false;
            iterator = iter;

            var y = 0;
            while (y < _rows && GhosttyNative.ghostty_render_state_row_iterator_next(iterator))
            {
                bool dirty = false;
                rc = GhosttyNative.ghostty_render_state_row_set(
                    iterator,
                    GhosttyNative.RenderStateRowOptionDirty,
                    (IntPtr)(&dirty));
                if (rc != GhosttyNative.Success)
                    return false;
                y++;
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (added)
                _renderState.DangerousRelease();
            if (iterator != IntPtr.Zero)
                GhosttyNative.ghostty_render_state_row_iterator_free(iterator);
        }
    }

    /// <summary>
    /// Dual-track scroll region from the byte stream: DECSTBM (CSI Ps;Ps r),
    /// RIS (ESC c), and soft-reset DECSTR (CSI ! p). Top/bottom in DECSTBM are
    /// 1-based; missing params mean full screen.
    /// </summary>
    private void TrackScrollRegionBytes(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            switch (_csiState)
            {
                case CsiTrackState.Ground:
                    if (b == 0x1B)
                        _csiState = CsiTrackState.Esc;
                    break;

                case CsiTrackState.Esc:
                    if (b == (byte)'[')
                    {
                        _csiState = CsiTrackState.Csi;
                        _csiParams.Clear();
                    }
                    else if (b == (byte)'c')
                    {
                        // RIS — full reset; scroll region becomes full screen.
                        ResetScrollRegionFullScreen();
                        _csiState = CsiTrackState.Ground;
                        _csiParams.Clear();
                    }
                    else
                    {
                        _csiState = CsiTrackState.Ground;
                    }
                    break;

                case CsiTrackState.Csi:
                    // Collect parameter and intermediate bytes; final byte ends CSI.
                    if (b is >= 0x30 and <= 0x3F or >= 0x20 and <= 0x2F)
                    {
                        if (_csiParams.Length < 64)
                            _csiParams.Append((char)b);
                        break;
                    }

                    // Final byte
                    _csiState = CsiTrackState.Ground;
                    if (b == (byte)'r' && IsDecstbmParams(_csiParams))
                        ApplyDecstbm(_csiParams.ToString());
                    else if (b == (byte)'p' && IsSoftResetParams(_csiParams))
                        ResetScrollRegionFullScreen();
                    _csiParams.Clear();
                    break;
            }
        }
    }

    private void ResetScrollRegionFullScreen()
    {
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    private static bool IsDecstbmParams(StringBuilder sb)
    {
        // DECSTBM has no intermediate bytes; only digits, ';', and optional '?'.
        // Reject if any intermediate (0x20-0x2F) was collected.
        for (var i = 0; i < sb.Length; i++)
        {
            var ch = sb[i];
            if (ch is >= '0' and <= '9' or ';' or '?')
                continue;
            return false;
        }

        // Private-mode params starting with '?' are not DECSTBM.
        if (sb.Length > 0 && sb[0] == '?')
            return false;
        return true;
    }

    /// <summary>
    /// DECSTR soft reset is CSI ! p — a single intermediate <c>!</c> and no
    /// parameter digits (xterm / Ghostty soft terminal reset).
    /// </summary>
    private static bool IsSoftResetParams(StringBuilder sb)
    {
        if (sb.Length != 1)
            return false;
        return sb[0] == '!';
    }

    private void ApplyDecstbm(string paramsText)
    {
        // Empty → full screen. "top;bottom" 1-based inclusive.
        int top = 1;
        int bottom = _rows;
        if (!string.IsNullOrEmpty(paramsText))
        {
            var parts = paramsText.Split(';');
            if (parts.Length >= 1 && parts[0].Length > 0
                && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0)
            {
                top = t;
            }

            if (parts.Length >= 2 && parts[1].Length > 0
                && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0)
            {
                bottom = b;
            }
            else if (parts.Length == 1 && parts[0].Length > 0)
            {
                // Only top specified — bottom stays full screen.
            }
        }

        // Convert to 0-based inclusive; clamp.
        var top0 = Math.Clamp(top - 1, 0, _rows - 1);
        var bottom0 = Math.Clamp(bottom - 1, top0, _rows - 1);
        if (bottom0 < top0)
            bottom0 = top0;
        _scrollTop = top0;
        _scrollBottom = bottom0;
    }
}
