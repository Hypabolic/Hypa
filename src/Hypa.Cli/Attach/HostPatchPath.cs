using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Status;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;

namespace Hypa.Cli.Attach;

/// <summary>
/// Why a cells delta took the full compose and not the patch path.
/// herdr <c>fast_path_blocker</c> (<c>src/client/shell/surface_patch.rs:57-94</c>)
/// and <c>present_surface_patch</c> (<c>src/client/state.rs:325-371</c>).
/// Each reason has its own counter.
/// </summary>
internal enum HostPatchFallback
{
    /// <summary>The chrome, the layout, the UI config or the theme changed.</summary>
    Chrome,

    /// <summary>The status error, the notice or the status chips changed or are shown.</summary>
    Status,

    /// <summary>The host size changed.</summary>
    HostSize,

    /// <summary>There is no kept frame, or the host frame is not equal to it.</summary>
    NoBaseline,

    /// <summary>A repaint is pending on the host encoder.</summary>
    Repaint,

    /// <summary>Presentation is frozen.</summary>
    Frozen,

    /// <summary>
    /// The cells payload is a reanchor body, or a full body that does not
    /// restate the kept grid: the pane has no kept grid, or the size or the
    /// occupant changed. A full body with no reanchor of the same size and
    /// occupant takes the patch path with all rows of its pane.
    /// </summary>
    FullFrame,

    /// <summary>The frame size is not the pane box size, so the stamp clips or pads.</summary>
    SizeMismatch,

    /// <summary>The pane changed between the main and the alternate screen.</summary>
    AltScreen,

    /// <summary>A tiled popup or a modal overlay covers a part of the pane.</summary>
    PopupOcclusion,

    /// <summary>The viewport origin or the scrollback position changed, or the pane is scrolled back.</summary>
    Viewport,

    /// <summary>A selection is active in a pane or across the host.</summary>
    Selection,

    /// <summary>A toast is visible.</summary>
    Toast,

    /// <summary>A modal overlay or the history overlay is visible.</summary>
    Overlay,

    /// <summary>A popup is open.</summary>
    Popup,

    /// <summary>A paint-mode overlay or a dialog is visible.</summary>
    PaintMode,

    /// <summary>The startup splash plays.</summary>
    Splash,

    /// <summary>A wide character is on the right edge of the pane box.</summary>
    WideEdge,

    /// <summary>The host cursor is a drawn glyph in the host cells.</summary>
    DrawnCursor,

    /// <summary>The delta does not chain on the pane frame of the last host paint.</summary>
    Baseline,

    /// <summary>Another pane has a frame that the last host paint did not show.</summary>
    OtherPane,

    /// <summary>The paint is inside a captured compose.</summary>
    Composing,
}

/// <summary>
/// Paint counters of one attach: patch paints, full composes, no-op paints,
/// and one counter for each fallback reason. The latency report shows them.
/// </summary>
internal sealed class HostPaintCounters
{
    private static readonly HostPatchFallback[] Reasons = Enum.GetValues<HostPatchFallback>();

    private readonly long[] _fallbacks = new long[Reasons.Length];
    private long _patches;
    private long _fullComposes;
    private long _noOps;

    public long Patches => Interlocked.Read(ref _patches);

    public long FullComposes => Interlocked.Read(ref _fullComposes);

    public long NoOps => Interlocked.Read(ref _noOps);

    public void NotePatch() => Interlocked.Increment(ref _patches);

    public void NoteFullCompose() => Interlocked.Increment(ref _fullComposes);

    public void NoteNoOp() => Interlocked.Increment(ref _noOps);

    public void NoteFallback(HostPatchFallback reason) =>
        Interlocked.Increment(ref _fallbacks[(int)reason]);

    public long Fallbacks(HostPatchFallback reason) => Interlocked.Read(ref _fallbacks[(int)reason]);

    public long TotalFallbacks()
    {
        long total = 0;
        foreach (var reason in Reasons)
            total += Fallbacks(reason);
        return total;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _patches, 0);
        Interlocked.Exchange(ref _fullComposes, 0);
        Interlocked.Exchange(ref _noOps, 0);
        for (var i = 0; i < _fallbacks.Length; i++)
            Interlocked.Exchange(ref _fallbacks[i], 0);
    }

    public Dictionary<string, long> Report()
    {
        var report = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["patch"] = Patches,
            ["full_compose"] = FullComposes,
            ["noop"] = NoOps,
        };
        foreach (var reason in Reasons)
            report[FallbackName(reason)] = Fallbacks(reason);
        return report;
    }

    public static string FallbackName(HostPatchFallback reason) => "patch.fallback." + reason switch
    {
        HostPatchFallback.Chrome => "chrome",
        HostPatchFallback.Status => "status",
        HostPatchFallback.HostSize => "host_size",
        HostPatchFallback.NoBaseline => "no_baseline",
        HostPatchFallback.Repaint => "repaint",
        HostPatchFallback.Frozen => "frozen",
        HostPatchFallback.FullFrame => "full_frame",
        HostPatchFallback.SizeMismatch => "size_mismatch",
        HostPatchFallback.AltScreen => "alt_screen",
        HostPatchFallback.PopupOcclusion => "popup_occlusion",
        HostPatchFallback.Viewport => "viewport",
        HostPatchFallback.Selection => "selection",
        HostPatchFallback.Toast => "toast",
        HostPatchFallback.Overlay => "overlay",
        HostPatchFallback.Popup => "popup",
        HostPatchFallback.PaintMode => "paint_mode",
        HostPatchFallback.Splash => "splash",
        HostPatchFallback.WideEdge => "wide_edge",
        HostPatchFallback.DrawnCursor => "drawn_cursor",
        HostPatchFallback.Baseline => "baseline",
        HostPatchFallback.OtherPane => "other_pane",
        HostPatchFallback.Composing => "composing",
        _ => reason.ToString(),
    };
}

/// <summary>
/// The inputs of a host compose other than the pane cells and the host
/// cursor. A patch is safe only when these inputs are equal to the inputs of
/// the last accepted host paint, and no input adds a layer over the panes.
/// </summary>
internal sealed record HostComposeKey
{
    public required int Cols { get; init; }

    public required int Rows { get; init; }

    public LayoutChromeGeometry? Chrome { get; init; }

    public required AttachUiConfig Ui { get; init; }

    public required ThemeRuntime Theme { get; init; }

    /// <summary>
    /// The palette of <see cref="Theme"/> at capture. The theme changes its
    /// palette in place, so the key keeps the palette instance too.
    /// </summary>
    public required ThemePalette Palette { get; init; }

    public string? StatusError { get; init; }

    public string? StatusNotice { get; init; }

    public StatusChipComposeInput? Chips { get; init; }

    public int ToastCount { get; init; }

    public IReadOnlyList<ToastHit>? Toasts { get; init; }

    public bool PopupOpen { get; init; }

    public bool OverlayModal { get; init; }

    public bool HistoryOverlay { get; init; }

    public AttachClientMode PaintMode { get; init; }

    public bool DialogOpen { get; init; }

    public bool SplashPlaying { get; init; }

    public bool PaneSelection { get; init; }

    public bool HostSelection { get; init; }

    public bool HasLivePane { get; init; }

    public string? FocusedPaneId { get; init; }

    public bool DrawnCursor { get; init; }

    /// <summary>
    /// A reason when this compose has a layer over the pane cells that a
    /// patch cannot restamp.
    /// </summary>
    public HostPatchFallback? Blocker()
    {
        if (SplashPlaying)
            return HostPatchFallback.Splash;
        if (DialogOpen || PaintMode is not AttachClientMode.Terminal || !HasLivePane)
            return HostPatchFallback.PaintMode;
        if (OverlayModal || HistoryOverlay)
            return HostPatchFallback.Overlay;
        if (PopupOpen)
            return HostPatchFallback.Popup;
        if (ToastCount > 0 || Chrome is { ToastHits.Count: > 0 })
            return HostPatchFallback.Toast;
        if (PaneSelection || HostSelection)
            return HostPatchFallback.Selection;
        if (!string.IsNullOrWhiteSpace(StatusError) || !string.IsNullOrWhiteSpace(StatusNotice))
            return HostPatchFallback.Status;
        if (DrawnCursor)
            return HostPatchFallback.DrawnCursor;
        return null;
    }

    /// <summary>
    /// The first input that differs from <paramref name="baseline"/>, or null
    /// when a full compose would paint the same chrome and layers.
    /// </summary>
    public HostPatchFallback? DifferenceFrom(HostComposeKey baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (Cols != baseline.Cols || Rows != baseline.Rows)
            return HostPatchFallback.HostSize;
        if (!ReferenceEquals(Chrome, baseline.Chrome)
            || !ReferenceEquals(Ui, baseline.Ui)
            || !ReferenceEquals(Theme, baseline.Theme)
            || !ReferenceEquals(Palette, baseline.Palette)
            || !string.Equals(FocusedPaneId, baseline.FocusedPaneId, StringComparison.Ordinal)
            || DrawnCursor != baseline.DrawnCursor)
        {
            return HostPatchFallback.Chrome;
        }

        if (!string.Equals(StatusError, baseline.StatusError, StringComparison.Ordinal)
            || !string.Equals(StatusNotice, baseline.StatusNotice, StringComparison.Ordinal)
            || !ChipsEqual(Chips, baseline.Chips))
        {
            return HostPatchFallback.Status;
        }

        if (ToastCount != baseline.ToastCount
            || (ToastCount > 0 && !ReferenceEquals(Toasts, baseline.Toasts)))
        {
            return HostPatchFallback.Toast;
        }

        if (PopupOpen != baseline.PopupOpen)
            return HostPatchFallback.Popup;
        if (OverlayModal != baseline.OverlayModal || HistoryOverlay != baseline.HistoryOverlay)
            return HostPatchFallback.Overlay;
        if (PaintMode != baseline.PaintMode
            || DialogOpen != baseline.DialogOpen
            || HasLivePane != baseline.HasLivePane)
        {
            return HostPatchFallback.PaintMode;
        }

        if (SplashPlaying != baseline.SplashPlaying)
            return HostPatchFallback.Splash;
        if (PaneSelection != baseline.PaneSelection || HostSelection != baseline.HostSelection)
            return HostPatchFallback.Selection;
        return null;
    }

    private static bool ChipsEqual(StatusChipComposeInput? a, StatusChipComposeInput? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return string.Equals(a.SessionName, b.SessionName, StringComparison.Ordinal)
            && string.Equals(a.WorkspaceLabel, b.WorkspaceLabel, StringComparison.Ordinal)
            && string.Equals(a.PaneLabel, b.PaneLabel, StringComparison.Ordinal)
            && string.Equals(a.PaneId, b.PaneId, StringComparison.Ordinal)
            && string.Equals(a.AgentName, b.AgentName, StringComparison.Ordinal)
            && string.Equals(a.AgentState, b.AgentState, StringComparison.Ordinal)
            && a.StatusIndicators == b.StatusIndicators
            && string.Equals(a.Error, b.Error, StringComparison.Ordinal)
            && a.MaxCols == b.MaxCols
            && a.Extras.SequenceEqual(b.Extras);
    }
}

/// <summary>
/// What the host shows after the last accepted host paint: the compose
/// inputs and the pane frame that each pane box shows. The attach keeps one
/// baseline only while <c>live.Host</c> is equal to the kept frame of the
/// host encoder. Any compose clears it first. Only an accepted write sets it
/// again. herdr commits only after a successful write and flush
/// (<c>src/client/state.rs:325-371</c>, <c>:556-591</c>).
/// </summary>
internal sealed class HostPatchBaseline
{
    private readonly Dictionary<string, AssembledSnapshot> _panes;

    private HostPatchBaseline(HostComposeKey key, Dictionary<string, AssembledSnapshot> panes)
    {
        Key = key;
        _panes = panes;
    }

    public HostComposeKey Key { get; }

    public IReadOnlyDictionary<string, AssembledSnapshot> Panes => _panes;

    public static HostPatchBaseline FromCompose(HostComposeKey key, IEnumerable<LastPaintedPane> painted)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(painted);
        var panes = new Dictionary<string, AssembledSnapshot>(StringComparer.Ordinal);
        foreach (var pane in painted)
            panes[pane.Frame.PaneId] = pane.Frame;
        return new HostPatchBaseline(key, panes);
    }

    /// <summary>A baseline with the same inputs and the patched pane frames.</summary>
    public HostPatchBaseline WithPatched(HostComposeKey key, IEnumerable<LastPaintedPane> painted)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(painted);
        var panes = new Dictionary<string, AssembledSnapshot>(_panes, StringComparer.Ordinal);
        foreach (var pane in painted)
            panes[pane.Frame.PaneId] = pane.Frame;
        return new HostPatchBaseline(key, panes);
    }

    /// <summary>A baseline with the same pane frames and new compose inputs.</summary>
    public HostPatchBaseline WithKey(HostComposeKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new HostPatchBaseline(key, _panes);
    }
}

/// <summary>
/// One pane of a patch paint: the newest frame, the frame that the first
/// delta chained on, and the pane rows that the deltas changed.
/// </summary>
internal sealed class HostPatchPane
{
    public HostPatchPane(string paneId, AssembledSnapshot? baseFrame)
    {
        PaneId = paneId;
        BaseFrame = baseFrame;
    }

    public string PaneId { get; }

    /// <summary>The stored pane frame before the first delta of this paint.</summary>
    public AssembledSnapshot? BaseFrame { get; }

    /// <summary>The pane frame after the last delta of this paint.</summary>
    public AssembledSnapshot? Frame { get; set; }

    /// <summary>Pane rows that a delta of this paint changed.</summary>
    public HashSet<int> Rows { get; } = [];

    /// <summary>The first reason that a delta of this pane cannot use the patch path.</summary>
    public HostPatchFallback? Blocker { get; set; }
}

/// <summary>The panes that one host paint patches. Built by the cells apply.</summary>
internal sealed class HostPatchRequest
{
    private readonly List<HostPatchPane> _panes = [];

    public IReadOnlyList<HostPatchPane> Panes => _panes;

    public HostPatchPane For(string paneId, AssembledSnapshot? baseFrame)
    {
        foreach (var pane in _panes)
        {
            if (string.Equals(pane.PaneId, paneId, StringComparison.Ordinal))
                return pane;
        }

        var added = new HostPatchPane(paneId, baseFrame);
        _panes.Add(added);
        return added;
    }

    public bool Has(string paneId)
    {
        foreach (var pane in _panes)
        {
            if (string.Equals(pane.PaneId, paneId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>The first blocker of any pane, in pane order.</summary>
    public HostPatchFallback? Blocker()
    {
        foreach (var pane in _panes)
        {
            if (pane.Blocker is { } reason)
                return reason;
        }

        return null;
    }
}
