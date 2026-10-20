namespace Hypa.Terminal.Vt;

/// <summary>
/// The reason that a live paint stamped the full viewport and not only the
/// rows that Ghostty marked dirty. Each reason has its own counter.
/// </summary>
public enum LiveStampFallbackReason
{
    None = 0,

    /// <summary>No retained grid exists. This is the first stamp, or a failed capture removed it.</summary>
    NoRetainedGrid,

    /// <summary>The pane size changed.</summary>
    Resize,

    /// <summary>The active screen changed (primary or alternate).</summary>
    ScreenSwitch,

    /// <summary>The scroll origin or the scrollback extent changed since the retained stamp.</summary>
    ScrollOffsetChange,

    /// <summary>The view shows rows from the history store.</summary>
    DeepScroll,

    /// <summary>The view is scrolled back inside the Ghostty window.</summary>
    ScrollOffset,

    /// <summary>The periodic check frame.</summary>
    VerifyFrame,

    /// <summary>The palette, the default colours or reverse video changed. Ghostty marks no row dirty for these.</summary>
    ColorChange,

    /// <summary>A commit cleared dirty flags that a different capture read.</summary>
    DirtyFlagsCleared,

    /// <summary>The retained style or glyph table reached its limit.</summary>
    TableLimit,

    /// <summary>Ghostty reported a full redraw.</summary>
    GhosttyFull,

    /// <summary>The render-state update failed.</summary>
    RenderStateUpdate,

    /// <summary><c>ghostty_render_state_row_cells_new</c> failed.</summary>
    RowCellsNew,

    /// <summary><c>ghostty_render_state_row_iterator_new</c> failed.</summary>
    RowIteratorNew,

    /// <summary>The render-state get of the row iterator failed.</summary>
    RowIteratorGet,

    /// <summary>The row iterator ended before the last row.</summary>
    RowIteratorEnded,

    /// <summary>The read of a row dirty flag failed.</summary>
    RowDirtyRead,

    /// <summary>Ghostty reported a partial redraw, but no row is marked dirty.</summary>
    NoDirtyRows,

    /// <summary>The page lookup of a dirty row failed.</summary>
    RowGridRef,

    /// <summary>A dirty row holds a selection.</summary>
    RowSelection,

    /// <summary>A cell on a dirty row holds a hyperlink.</summary>
    Hyperlink,

    /// <summary>
    /// The attach capture failed closed too many times in sequence. The
    /// attach then posts a snapshot that can hold old styles or blank cells,
    /// so the new attach gets a first frame.
    /// </summary>
    AttachCaptureLimit,
}

/// <summary>Wire names for the fallback counters.</summary>
public static class LiveStampFallbackReasons
{
    private static readonly LiveStampFallbackReason[] Values = Enum.GetValues<LiveStampFallbackReason>();

    /// <summary>Every reason except <see cref="LiveStampFallbackReason.None"/>.</summary>
    public static IReadOnlyList<LiveStampFallbackReason> All { get; } =
        Array.FindAll(Values, static r => r != LiveStampFallbackReason.None);

    public static string Name(LiveStampFallbackReason reason) => reason switch
    {
        LiveStampFallbackReason.NoRetainedGrid => "live_stamp.fallback.no_retained_grid",
        LiveStampFallbackReason.Resize => "live_stamp.fallback.resize",
        LiveStampFallbackReason.ScreenSwitch => "live_stamp.fallback.screen_switch",
        LiveStampFallbackReason.ScrollOffsetChange => "live_stamp.fallback.scroll_offset_change",
        LiveStampFallbackReason.DeepScroll => "live_stamp.fallback.deep_scroll",
        LiveStampFallbackReason.ScrollOffset => "live_stamp.fallback.scroll_offset",
        LiveStampFallbackReason.VerifyFrame => "live_stamp.fallback.verify_frame",
        LiveStampFallbackReason.ColorChange => "live_stamp.fallback.color_change",
        LiveStampFallbackReason.DirtyFlagsCleared => "live_stamp.fallback.dirty_flags_cleared",
        LiveStampFallbackReason.TableLimit => "live_stamp.fallback.table_limit",
        LiveStampFallbackReason.GhosttyFull => "live_stamp.fallback.ghostty_full",
        LiveStampFallbackReason.RenderStateUpdate => "live_stamp.fallback.render_state_update",
        LiveStampFallbackReason.RowCellsNew => "live_stamp.fallback.row_cells_new",
        LiveStampFallbackReason.RowIteratorNew => "live_stamp.fallback.row_iterator_new",
        LiveStampFallbackReason.RowIteratorGet => "live_stamp.fallback.row_iterator_get",
        LiveStampFallbackReason.RowIteratorEnded => "live_stamp.fallback.row_iterator_ended",
        LiveStampFallbackReason.RowDirtyRead => "live_stamp.fallback.row_dirty_read",
        LiveStampFallbackReason.NoDirtyRows => "live_stamp.fallback.no_dirty_rows",
        LiveStampFallbackReason.RowGridRef => "live_stamp.fallback.row_grid_ref",
        LiveStampFallbackReason.RowSelection => "live_stamp.fallback.row_selection",
        LiveStampFallbackReason.Hyperlink => "live_stamp.fallback.hyperlink",
        LiveStampFallbackReason.AttachCaptureLimit => "live_stamp.fallback.attach_capture_limit",
        _ => "live_stamp.fallback.none",
    };
}

/// <summary>
/// Port for the live stamp counters. A server meter can implement it and
/// report the counts. The pane calls it with no lock that a reader needs.
/// </summary>
public interface ILiveStampMeter
{
    /// <summary>A capture stamped the full viewport for this reason.</summary>
    void RecordFallback(LiveStampFallbackReason reason);

    /// <summary>A capture stamped only the dirty rows.</summary>
    void RecordDirtyRowStamp(int rows, int cells);

    /// <summary>A check frame found rows in the retained grid that differ from a full stamp.</summary>
    void RecordVerifyMismatch(int rows);
}

/// <summary>Counts of the live stamp outcomes for one pane.</summary>
public sealed record LiveStampCounterSnapshot(
    IReadOnlyDictionary<string, long> Fallbacks,
    long DirtyRowStamps,
    long DirtyRows,
    long DirtyRowCells,
    long VerifyMismatchFrames,
    long VerifyMismatchRows);

/// <summary>
/// Default in-memory <see cref="ILiveStampMeter"/>. One instance for each
/// pane. Thread safe.
/// </summary>
public sealed class LiveStampCounters : ILiveStampMeter
{
    private readonly long[] _fallbacks = new long[Enum.GetValues<LiveStampFallbackReason>().Length];
    private long _dirtyRowStamps;
    private long _dirtyRows;
    private long _dirtyRowCells;
    private long _verifyMismatchFrames;
    private long _verifyMismatchRows;

    public void RecordFallback(LiveStampFallbackReason reason)
    {
        var index = (int)reason;
        if (reason == LiveStampFallbackReason.None || (uint)index >= (uint)_fallbacks.Length)
            return;
        Interlocked.Increment(ref _fallbacks[index]);
    }

    public void RecordDirtyRowStamp(int rows, int cells)
    {
        Interlocked.Increment(ref _dirtyRowStamps);
        Interlocked.Add(ref _dirtyRows, rows);
        Interlocked.Add(ref _dirtyRowCells, cells);
    }

    public void RecordVerifyMismatch(int rows)
    {
        if (rows <= 0)
            return;
        Interlocked.Increment(ref _verifyMismatchFrames);
        Interlocked.Add(ref _verifyMismatchRows, rows);
    }

    public long Fallbacks(LiveStampFallbackReason reason)
    {
        var index = (int)reason;
        return (uint)index < (uint)_fallbacks.Length ? Interlocked.Read(ref _fallbacks[index]) : 0;
    }

    public long DirtyRowStamps => Interlocked.Read(ref _dirtyRowStamps);

    public long VerifyMismatchRows => Interlocked.Read(ref _verifyMismatchRows);

    public LiveStampCounterSnapshot Snapshot()
    {
        var fallbacks = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var reason in LiveStampFallbackReasons.All)
            fallbacks[LiveStampFallbackReasons.Name(reason)] = Fallbacks(reason);
        return new LiveStampCounterSnapshot(
            fallbacks,
            Interlocked.Read(ref _dirtyRowStamps),
            Interlocked.Read(ref _dirtyRows),
            Interlocked.Read(ref _dirtyRowCells),
            Interlocked.Read(ref _verifyMismatchFrames),
            Interlocked.Read(ref _verifyMismatchRows));
    }
}
