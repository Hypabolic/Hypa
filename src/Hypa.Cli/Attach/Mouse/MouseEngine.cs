using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Copy;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Sidebar;

namespace Hypa.Cli.Attach.Mouse;

public enum MouseEngineState
{
    Idle,
    PendingClick,
    DraggingSplit,
    DraggingTab,
    DraggingSidebar,
    DraggingSidebarSection,
    DraggingScrollbar,
    Selecting,
    Menu,
}

public enum MouseCommandKind
{
    None,
    ApplyChromeHit,
    SetSplitRatio,
    MoveTab,
    SetSidebarWidth,
    SetSidebarSectionSplit,
    Detach,
    OpenMenu,
    CloseMenu,
    ApplyMenu,
    Yank,
    ForwardSgr,
    ScrollHistory,
    SetHistoryTop,
    PreviewSplit,
    PreviewSidebar,
    PreviewSidebarSection,
    ScrollSwitcher,
    ScrollSidebar,
    ActivateLink,
}

public sealed record MouseEngineOptions
{
    public bool CaptureEnabled { get; init; } = true;

    public bool CopyOnSelect { get; init; } = true;

    public int ScrollLines { get; init; } = 3;

    public string RightClickPassthroughModifier { get; init; } = "";

    public string DefaultRightClick { get; init; } = "hypa";

    public IMouseForwarder Forwarder { get; init; } = ChildMouseForwarder.Instance;

    public static MouseEngineOptions FromUi(
        Hypa.AgentRuntime.Domain.AttachConfig.AttachUiConfig ui,
        bool captureOverride) =>
        new()
        {
            CaptureEnabled = captureOverride && ui.MouseCapture,
            CopyOnSelect = ui.CopyOnSelect,
            ScrollLines = Math.Max(1, ui.MouseScrollLines),
            RightClickPassthroughModifier = ui.RightClickPassthroughModifier ?? "",
        };
}

public sealed record MouseFeedContext(
    LayoutChromeGeometry? Geometry,
    string? ChildMouseMode = null,
    string? RightClickPolicy = null,
    string? FocusedPaneId = null,
    AssembledSnapshot? Snapshot = null,
    PaneHistoryView? History = null,
    bool PopupOpen = false,
    int MaxOffset = 0,
    MouseProtocolEncoding Encoding = MouseProtocolEncoding.Sgr,
    IReadOnlyList<SidebarCubeItem>? Cubes = null)
{
    public bool ContinuityEnabled { get; init; }

    public IReadOnlyList<SidebarWorkspaceItem>? Workspaces { get; init; }

    public IReadOnlySet<string>? CollapsedWorktreeGroups { get; init; }

    public string? OverlayPaneId { get; init; }

    public IReadOnlyList<InstalledPlugin>? LinkedPlugins { get; init; }

    /// <summary>
    /// Replay of an unhandled activate must not send activate again.
    /// </summary>
    public bool ReplayingLinkActivate { get; init; }

    /// <summary>
    // / Exclusive UI (share, settings, overlay) owns the pointer.
    /// <c>src/client/shell/mouse.rs:1608-1642</c> returns from overlay
    /// hits without starting a pane select. A click without a drag must
    /// not hit chrome under the modal.
    /// </summary>
    public bool ModalBlocksChrome { get; init; }
}

public sealed record MouseEngineResult(
    MouseCommandKind Kind,
    ChromeHit? Hit = null,
    IReadOnlyList<int>? Path = null,
    double? Ratio = null,
    string? TabId = null,
    string? WorkspaceId = null,
    int? TabIndex = null,
    int? SidebarWidth = null,
    string? PaneId = null,
    byte[]? Osc52 = null,
    byte[]? ForwardKeys = null,
    int? ScrollDelta = null,
    int? ViewportTop = null,
    int? Offset = null,
    int? TrackRow = null,
    ContextMenuModel? Menu = null,
    ContextMenuItem? MenuItem = null,
    string? PlacementId = null,
    MouseEngineState State = MouseEngineState.Idle,
    SidebarPaneSlot? SidebarSlot = null,
    string? SidebarSectionId = null,
    int? ViewportRow = null,
    int? Col = null,
    long? Generation = null,
    MouseEvent? SourceEvent = null);

/// <summary>Live mouse state machine. Feed decoded events and geometry.</summary>
public sealed class MouseEngine
{
    public const int DoubleClickMs = 400;

    private readonly TimeProvider _time;
    private MouseEngineOptions _options;
    private MouseEngineState _state = MouseEngineState.Idle;
    private ChromeHit? _pressHit;
    private int _pressCol;
    private int _pressRow;
    private long _lastLeftMs;
    private int _lastLeftCol;
    private int _lastLeftRow;
    private string? _lastLeftPane;
    private int _clickCount;
    private IReadOnlyList<int>? _splitPath;
    private CellRect _splitParent;
    private string _splitDirection = "right";
    private double _splitStartRatio = 0.5;
    private string? _dragTabId;
    private int _dragTabStartCol;
    private string? _scrollPaneId;
    private CellRect _scrollBar;
    private int _scrollMaxOffset;
    private PaneChromeScrollState _scrollMetrics;
    private int _scrollGrabOffset;
    private bool _forwardingChild;
    private bool _forwardingPopup;
    private bool _ignoreMenuRelease;
    private bool _menuClickArmed;
    private int _sidebarStartWidth;
    private int _sidebarStartCol;
    private string? _selectPaneId;
    private CellRect _selectContent;
    private bool _pressShift;
    private bool _pressAlt;
    private bool _pressCtrl;

    public MouseEngine(TimeProvider? time = null, MouseEngineOptions? options = null)
    {
        _time = time ?? TimeProvider.System;
        _options = options ?? new MouseEngineOptions();
    }

    public MouseEngineState State => _state;

    public MouseEngineOptions Options => _options;

    public ContextMenuModel? Menu { get; private set; }

    public MouseSelection Selection { get; } = new();

    public double? PreviewRatio { get; private set; }

    public int? PreviewSidebarWidth { get; private set; }

    public float? PreviewSidebarSectionSplit { get; private set; }

    /// <summary>Pane that owns an in-progress content select. Used to seed history.</summary>
    internal string? ActiveHistoryPaneId
    {
        get
        {
            if (_state is MouseEngineState.Selecting && !string.IsNullOrWhiteSpace(_selectPaneId))
                return _selectPaneId;
            if (_state is MouseEngineState.PendingClick
                && _pressHit is { Kind: ChromeHitKind.Pane, IsFrame: false, PaneId: { Length: > 0 } paneId })
            {
                return paneId;
            }

            return null;
        }
    }

    public void Configure(MouseEngineOptions options)
    {
        _options = options ?? new MouseEngineOptions();
        if (!_options.CaptureEnabled)
            Reset();
    }

    public void Reset()
    {
        _state = MouseEngineState.Idle;
        _pressHit = null;
        Menu = null;
        Selection.Clear();
        PreviewRatio = null;
        PreviewSidebarWidth = null;
        PreviewSidebarSectionSplit = null;
        _clickCount = 0;
        _scrollMaxOffset = 0;
        _forwardingChild = false;
        _forwardingPopup = false;
        _ignoreMenuRelease = false;
        _menuClickArmed = false;
    }

    private void AbortTiledMouseState()
    {
        if (_state is not (MouseEngineState.DraggingSplit
            or MouseEngineState.DraggingTab
            or MouseEngineState.DraggingSidebar
            or MouseEngineState.DraggingSidebarSection
            or MouseEngineState.DraggingScrollbar
            or MouseEngineState.Selecting
            or MouseEngineState.PendingClick))
        {
            return;
        }

        _state = MouseEngineState.Idle;
        _pressHit = null;
        Selection.Clear();
        PreviewRatio = null;
        PreviewSidebarWidth = null;
        PreviewSidebarSectionSplit = null;
    }

    public IReadOnlyList<MouseEngineResult> Feed(MouseEvent ev, MouseFeedContext context)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(context);
        if (!_options.CaptureEnabled)
            return [];

        if (_state is MouseEngineState.Menu || Menu is not null)
            return FeedMenu(ev, context);

        var geo = context.Geometry;
        if (geo is null)
            return [];

        if (_state is MouseEngineState.Selecting)
            return FeedSelect(ev, context);

        if (context.PopupOpen)
        {
            if (geo.PopupFrame is { } popup)
                return FeedPopup(ev, popup, context);

            // Overlay hidden below min; keys stay modal. Do not hit tabs/splits.
            _forwardingPopup = false;
            _forwardingChild = false;
            AbortTiledMouseState();
            return [];
        }

        _forwardingPopup = false;
        if (ev.IsWheel)
            return FeedWheel(ev, geo, context);

        return _state switch
        {
            MouseEngineState.DraggingSplit => FeedSplitDrag(ev),
            MouseEngineState.DraggingTab => FeedTabDrag(ev, geo),
            MouseEngineState.DraggingSidebar => FeedSidebarDrag(ev, geo),
            MouseEngineState.DraggingSidebarSection => FeedSidebarSectionDrag(ev, geo),
            MouseEngineState.DraggingScrollbar => FeedScrollbarDrag(ev, context),
            MouseEngineState.Selecting => FeedSelect(ev, context),
            _ => FeedIdle(ev, geo, context),
        };
    }

    public IReadOnlyList<MouseEngineResult> FeedKey(KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (Menu is null)
            return [];

        if (chord.IsEscape)
            return CloseMenuResults();

        if (chord is { Key: "j" or "down", Ctrl: false, Alt: false })
        {
            Menu.Move(1);
            return [MenuState(MouseCommandKind.None)];
        }

        if (chord is { Key: "k" or "up", Ctrl: false, Alt: false })
        {
            Menu.Move(-1);
            return [MenuState(MouseCommandKind.None)];
        }

        if (chord.IsEnter)
            return ApplyMenu(Menu.SelectedItem);

        return [];
    }

    private IReadOnlyList<MouseEngineResult> FeedIdle(
        MouseEvent ev,
        LayoutChromeGeometry geo,
        MouseFeedContext context)
    {
        var hit = ChromeHitTest.Hit(geo, ev.Col, ev.Row);
        if (_forwardingChild)
            return FeedChildForward(ev, geo, context);
        if (ev.Action is MouseAction.Press)
            return FeedPress(ev, hit, geo, context);
        if (ev.Action is MouseAction.Drag && _state is MouseEngineState.PendingClick)
            return FeedPendingMotion(ev, geo, context);
        if (ev.Action is MouseAction.Release)
            return FeedPendingRelease(ev, geo, context);
        if (ev.Action is MouseAction.Move)
            return MaybeForward(ev, hit, geo, context);
        return [];
    }

    private IReadOnlyList<MouseEngineResult> FeedPress(
        MouseEvent ev,
        ChromeHit? hit,
        LayoutChromeGeometry geo,
        MouseFeedContext context)
    {
        if (ev.Button is MouseButton.Right)
            return FeedRightPress(ev, hit, geo, context);

        if (ev.Button is not MouseButton.Left)
            return MaybeForward(ev, hit, geo, context);

        // CONTROL + left Down on
        // pane inner cell sends pane.link.activate. Client does not send a
        // URL. Do not write pane-relative ANSI to the host TTY.
        if (!context.ReplayingLinkActivate
            && ev.Ctrl
            && hit is { Kind: ChromeHitKind.Pane, IsFrame: false, PaneId: { Length: > 0 } linkPane })
        {
            var pane = FindPane(geo, linkPane);
            var content = pane?.Content ?? geo.Content;
            var viewportRow = Math.Max(0, ev.Row - content.Row);
            var col = Math.Max(0, ev.Col - content.Col);
            long? generation = context.Snapshot is { } snap
                && string.Equals(snap.PaneId, linkPane, StringComparison.Ordinal)
                    ? snap.Generation
                    : null;
            int? offset = pane?.Scroll.MetricsKnown == true
                ? pane.Scroll.OffsetFromBottom
                : null;
            _pressHit = null;
            _state = MouseEngineState.Idle;
            return
            [
                new MouseEngineResult(
                    MouseCommandKind.ActivateLink,
                    PaneId: linkPane,
                    ViewportRow: viewportRow,
                    Col: col,
                    Offset: offset,
                    State: _state,
                    Generation: generation,
                    SourceEvent: ev),
            ];
        }

        _pressHit = hit;
        _pressCol = ev.Col;
        _pressRow = ev.Row;
        _pressShift = ev.Shift;
        _pressAlt = ev.Alt;
        _pressCtrl = ev.Ctrl;

        if (hit is { Kind: ChromeHitKind.SplitBorder, Path: { } path })
        {
            var split = FindSplit(geo, path);
            _splitPath = path;
            _splitParent = split?.Parent ?? geo.Content;
            _splitDirection = split?.Direction ?? "right";
            _splitStartRatio = split?.Ratio ?? 0.5;
            PreviewRatio = _splitStartRatio;
            _state = MouseEngineState.DraggingSplit;
            return [new MouseEngineResult(MouseCommandKind.None, State: _state)];
        }

        if (hit is { Kind: ChromeHitKind.Scrollbar, PaneId: { } scrollPane })
        {
            var pane = FindPane(geo, scrollPane);
            _scrollPaneId = scrollPane;
            _scrollBar = pane?.Scrollbar ?? new CellRect(ev.Col, ev.Row, 1, 1);
            _scrollMetrics = pane?.Scroll ?? PaneChromeScrollState.Unknown;
            if (!_scrollMetrics.ShowsScrollbar && context.MaxOffset > 0)
            {
                _scrollMetrics = _scrollMetrics with
                {
                    MetricsKnown = true,
                    MaxOffsetFromBottom = context.MaxOffset,
                    ViewportRows = _scrollMetrics.ViewportRows > 0
                        ? _scrollMetrics.ViewportRows
                        : _scrollBar.Rows,
                };
            }

            _scrollMaxOffset = Math.Max(0, _scrollMetrics.MaxOffsetFromBottom);
            // Thumb press stores
            // grab offset. Track press jumps with thumb-center mapping.
            if (PaneChromeGutter.ThumbGrabOffset(_scrollMetrics, _scrollBar, ev.Row) is { } grab)
            {
                _scrollGrabOffset = grab;
                _state = MouseEngineState.DraggingScrollbar;
                return [new MouseEngineResult(MouseCommandKind.None, State: _state)];
            }

            var offset = PaneChromeGutter.OffsetFromRow(_scrollMetrics, _scrollBar, ev.Row);
            _state = MouseEngineState.Idle;
            return
            [
                new MouseEngineResult(
                    MouseCommandKind.SetHistoryTop,
                    PaneId: scrollPane,
                    Offset: offset,
                    TrackRow: ev.Row,
                    State: _state),
            ];
        }

        if (hit is { Kind: ChromeHitKind.SidebarEdge })
        {
            if (geo.SidebarCompact)
            {
                _state = MouseEngineState.PendingClick;
                return [];
            }

            _sidebarStartWidth = geo.SidebarWidth > 0 ? geo.SidebarWidth : geo.Sidebar?.Cols ?? 18;
            _sidebarStartCol = ev.Col;
            PreviewSidebarWidth = _sidebarStartWidth;
            _state = MouseEngineState.DraggingSidebar;
            return [new MouseEngineResult(MouseCommandKind.None, State: _state)];
        }

        if (hit is { Kind: ChromeHitKind.SidebarSectionDivider })
        {
            if (geo.SidebarCompact)
            {
                _state = MouseEngineState.PendingClick;
                return [];
            }

            var ratio = SidebarTwoPaneLayoutPolicy.SplitRatioFromRow(geo.Sidebar ?? default, ev.Row);
            PreviewSidebarSectionSplit = ratio;
            _state = MouseEngineState.DraggingSidebarSection;
            return
            [
                new MouseEngineResult(
                    MouseCommandKind.PreviewSidebarSection,
                    Ratio: ratio,
                    State: _state),
            ];
        }

        if (hit is { Kind: ChromeHitKind.Tab })
        {
            _dragTabId = hit.TabId;
            _dragTabStartCol = ev.Col;
            _state = MouseEngineState.PendingClick;
            return [];
        }

        if (hit is
            {
                Kind: ChromeHitKind.TabNew or ChromeHitKind.TabClose or ChromeHitKind.TabOverflowPrev
                    or ChromeHitKind.TabOverflowNext or ChromeHitKind.Zoom or ChromeHitKind.PaneClose
                    or ChromeHitKind.SidebarMenu or ChromeHitKind.SidebarWorkspace
                    or ChromeHitKind.SidebarWorktreeGroupToggle
                    or ChromeHitKind.SidebarAgent or ChromeHitKind.SidebarHiddenPane
                    or ChromeHitKind.SidebarTreeToggle
                    or ChromeHitKind.SidebarCube or ChromeHitKind.SidebarNew
                    or ChromeHitKind.SidebarAddCube or ChromeHitKind.SidebarShareMux
                    or ChromeHitKind.SidebarUpdateNotice
                    or ChromeHitKind.SidebarSection or ChromeHitKind.SidebarSort
                    or ChromeHitKind.SidebarWorkspaceClose
                    or ChromeHitKind.Toast
                    or ChromeHitKind.MobileSwitch or ChromeHitKind.MobileSwitcherClose
                    or ChromeHitKind.MobileSwitcherMenu
            })
        {
            _state = MouseEngineState.PendingClick;
            return [];
        }

        if (hit is { Kind: ChromeHitKind.Pane, PaneId: { } paneId })
        {
            // gesture when mouse_reporting. Hypa keeps click-without-drag
            // as that forward. A drag selects host cells so the operator
            // can copy any visible glyph, including child mouse mode.
            NoteLeftClick(ev, paneId);
            if (_clickCount == 2 && !hit.IsFrame && !ChildOwns(context))
            {
                SeedSelection(context, paneId);
                var content = FindPane(geo, paneId)?.Content ?? geo.Content;
                var (row, col) = ToPaneCell(ev, content, context.History);
                Selection.SelectWord(row, col);
                _state = MouseEngineState.Idle;
                var yank = MaybeYank(paneId);
                return yank is null
                    ? [new MouseEngineResult(MouseCommandKind.None, State: _state)]
                    : [yank];
            }

            if (_clickCount >= 3)
                _clickCount = 1;

            _selectPaneId = paneId;
            _selectContent = FindPane(geo, paneId)?.Content ?? geo.Content;
            _state = MouseEngineState.PendingClick;
            return [];
        }

        // Mode bar, ERROR row, and any other painted cell. Click without
        // drag stays idle. Drag starts a host-cell select.
        _state = MouseEngineState.PendingClick;
        return [];
    }

    private IReadOnlyList<MouseEngineResult> FeedPendingMotion(
        MouseEvent ev,
        LayoutChromeGeometry geo,
        MouseFeedContext context)
    {
        if (_pressHit is { Kind: ChromeHitKind.Tab }
            && _dragTabId is { Length: > 0 } sourceTabId
            && TabDragMoved(ev, geo)
            && TabDropTarget(geo, ev.Col, ev.Row) is { TabId: { } dropTabId }
            && !string.Equals(dropTabId, sourceTabId, StringComparison.Ordinal))
        {
            _state = MouseEngineState.DraggingTab;
            return FeedTabDrag(ev, geo);
        }

        if (ev.Col == _pressCol && ev.Row == _pressRow)
            return [];

        // pointer. Do not start a host-cell select under exclusive UI.
        if (context.ModalBlocksChrome)
            return [];

        // on Down. Hypa also anchors chrome and child-mouse panes on the
        // composed host so every visible glyph can copy.
        Selection.BeginHost(_pressCol, _pressRow);
        Selection.ExtendHost(ev.Col, ev.Row);
        _state = MouseEngineState.Selecting;
        return [new MouseEngineResult(MouseCommandKind.None, State: _state)];
    }

    private IReadOnlyList<MouseEngineResult> FeedPendingRelease(
        MouseEvent ev,
        LayoutChromeGeometry geo,
        MouseFeedContext context)
    {
        var hit = _pressHit;
        _pressHit = null;
        _state = MouseEngineState.Idle;
        if (hit is null)
            return [];
        if (context.ModalBlocksChrome)
        {
            // A blocked release must not leave HostRange inverse on the pane.
            Selection.Clear();
            return [];
        }

        List<MouseEngineResult>? forwarded = null;
        if (hit is { Kind: ChromeHitKind.Pane, IsFrame: false } && ChildOwns(context))
        {
            var press = new MouseEvent(
                ev.Button,
                MouseAction.Press,
                _pressCol,
                _pressRow,
                _pressShift,
                _pressAlt,
                _pressCtrl);
            var fwdPress = Forward(press, hit, geo, context);
            var fwdRelease = Forward(ev, hit, geo, context);
            if (fwdPress is not null || fwdRelease is not null)
            {
                forwarded = [];
                if (fwdPress is not null)
                    forwarded.Add(fwdPress);
                if (fwdRelease is not null)
                    forwarded.Add(fwdRelease);
            }
        }

        if (hit.Kind is ChromeHitKind.SidebarMenu)
        {
            var col = ev.Col;
            var row = ev.Row + 1;
            if (geo.TryGlobalMenuAnchor(out var anchorCol, out var anchorRow))
            {
                col = anchorCol;
                row = anchorRow;
            }

            return OpenMenu(ContextMenuModel.ForGlobal(
                col,
                row,
                geo.Cols,
                geo.Rows,
                context.LinkedPlugins));
        }

        if (hit.Kind is ChromeHitKind.SidebarCollectionItem && ev.Shift)
            hit = hit with { SecondaryActivation = true };

        var apply = new MouseEngineResult(MouseCommandKind.ApplyChromeHit, Hit: hit, State: _state);
        if (forwarded is null)
            return [apply];
        forwarded.Add(apply);
        return forwarded;
    }

    private IReadOnlyList<MouseEngineResult> FeedRightPress(
        MouseEvent ev,
        ChromeHit? hit,
        LayoutChromeGeometry geo,
        MouseFeedContext context)
    {
        if (hit is { Kind: ChromeHitKind.SidebarHiddenPane, PaneId: { } hiddenPane })
        {
            var hidden = true;
            foreach (var row in geo.SidebarRows)
            {
                if (string.Equals(row.Id, hiddenPane, StringComparison.Ordinal))
                {
                    hidden = row.Hidden;
                    break;
                }
            }

            var overlay = string.Equals(hiddenPane, context.OverlayPaneId, StringComparison.Ordinal);
            return OpenMenu(HiddenPaneMenuModel.ForPane(
                hiddenPane, ev.Col, ev.Row, geo.Cols, geo.Rows, hidden, overlay));
        }

        if (hit is { Kind: ChromeHitKind.SidebarAgent or ChromeHitKind.SidebarCollectionItem })
            return [];

        if (hit is
            {
                Kind: ChromeHitKind.SidebarWorkspace or ChromeHitKind.SidebarWorktreeGroupToggle,
                WorkspaceId: { } workspaceId
            })
        {
            var target = ResolveWorkspaceWorktree(context, workspaceId);
            return OpenMenu(ContextMenuModel.ForWorkspace(
                workspaceId,
                ev.Col,
                ev.Row,
                geo.Cols,
                geo.Rows,
                context.ContinuityEnabled,
                target.IsGit,
                target.IsLinked,
                target.HasChildren,
                target.Collapsed));
        }

        if (hit is { Kind: ChromeHitKind.SidebarCube, PlacementId: { } placementId })
            return OpenMenu(ContextMenuModel.ForCube(
                placementId,
                ev.Col,
                ev.Row,
                geo.Cols,
                geo.Rows,
                context.ContinuityEnabled));

        if (hit is { Kind: ChromeHitKind.Tab, TabId: { } tabId })
            return OpenMenu(ContextMenuModel.ForTab(tabId, ev.Col, ev.Row, geo.Cols, geo.Rows));

        if (hit is { Kind: ChromeHitKind.PaneClose or ChromeHitKind.Scrollbar, PaneId: { } chromePane })
            return OpenMenu(ContextMenuModel.ForPane(
                chromePane, ev.Col, ev.Row, geo.Cols, geo.Rows, context.Cubes, context.ContinuityEnabled));

        if (hit is { Kind: ChromeHitKind.Pane, PaneId: { } paneId })
        {
            var passthrough = _options.RightClickPassthroughModifier.Length > 0
                && ev.HasModifier(_options.RightClickPassthroughModifier);
            var policy = string.IsNullOrWhiteSpace(context.RightClickPolicy)
                ? _options.DefaultRightClick
                : context.RightClickPolicy;
            var forwardContent = !hit.IsFrame
                && (passthrough
                    || string.Equals(policy, "pane", StringComparison.OrdinalIgnoreCase)
                    || ChildOwns(context));
            if (forwardContent)
            {
                var fwd = Forward(ev, hit, geo, context);
                if (fwd is null)
                    return [];
                _pressHit = hit;
                _forwardingChild = true;
                return [fwd];
            }

            return OpenMenu(ContextMenuModel.ForPane(
                paneId, ev.Col, ev.Row, geo.Cols, geo.Rows, context.Cubes, context.ContinuityEnabled));
        }

        return [];
    }

    private IReadOnlyList<MouseEngineResult> FeedSplitDrag(MouseEvent ev)
    {
        var ratio = ComputeSplitRatio(ev);
        PreviewRatio = ratio;
        if (ev.Action is MouseAction.Release)
        {
            var path = _splitPath ?? [];
            _state = MouseEngineState.Idle;
            PreviewRatio = null;
            return
            [
                new MouseEngineResult(
                    MouseCommandKind.SetSplitRatio,
                    Path: path,
                    Ratio: ratio,
                    State: _state),
            ];
        }

        return
        [
            new MouseEngineResult(
                MouseCommandKind.PreviewSplit,
                Path: _splitPath,
                Ratio: ratio,
                State: _state),
        ];
    }

    private IReadOnlyList<MouseEngineResult> FeedTabDrag(MouseEvent ev, LayoutChromeGeometry geo)
    {
        if (ev.Action is not MouseAction.Release)
            return [new MouseEngineResult(MouseCommandKind.None, State: _state)];

        var tabId = _dragTabId;
        var dropTarget = TabDropTarget(geo, ev.Col, ev.Row);
        var pressHit = _pressHit;
        _state = MouseEngineState.Idle;
        _dragTabId = null;
        _pressHit = null;
        if (string.IsNullOrWhiteSpace(tabId))
            return [];

        if (dropTarget is null)
        {
            return pressHit is null
                ? []
                : [new MouseEngineResult(MouseCommandKind.ApplyChromeHit, Hit: pressHit, State: _state)];
        }

        var index = DropTabIndex(geo, ev.Col, ev.Row);
        return
        [
            new MouseEngineResult(
                MouseCommandKind.MoveTab,
                TabId: tabId,
                TabIndex: index,
                State: _state),
        ];
    }

    private IReadOnlyList<MouseEngineResult> FeedSidebarDrag(MouseEvent ev, LayoutChromeGeometry geo)
    {
        var width = SidebarHitModel.ClampWidth(
            _sidebarStartWidth + (ev.Col - _sidebarStartCol),
            geo.UiMinSidebarWidth,
            geo.UiMaxSidebarWidth);
        PreviewSidebarWidth = width;
        if (ev.Action is MouseAction.Release)
        {
            _state = MouseEngineState.Idle;
            PreviewSidebarWidth = null;
            return
            [
                new MouseEngineResult(
                    MouseCommandKind.SetSidebarWidth,
                    SidebarWidth: width,
                    State: _state),
            ];
        }

        return
        [
            new MouseEngineResult(
                MouseCommandKind.PreviewSidebar,
                SidebarWidth: width,
                State: _state),
        ];
    }

    private IReadOnlyList<MouseEngineResult> FeedSidebarSectionDrag(MouseEvent ev, LayoutChromeGeometry geo)
    {
        var ratio = SidebarTwoPaneLayoutPolicy.SplitRatioFromRow(geo.Sidebar ?? default, ev.Row);
        PreviewSidebarSectionSplit = ratio;
        if (ev.Action is MouseAction.Release)
        {
            _state = MouseEngineState.Idle;
            PreviewSidebarSectionSplit = null;
            return
            [
                new MouseEngineResult(
                    MouseCommandKind.SetSidebarSectionSplit,
                    Ratio: ratio,
                    State: _state),
            ];
        }

        return
        [
            new MouseEngineResult(
                MouseCommandKind.PreviewSidebarSection,
                Ratio: ratio,
                State: _state),
        ];
    }

    private IReadOnlyList<MouseEngineResult> FeedScrollbarDrag(MouseEvent ev, MouseFeedContext context)
    {
        var maxOffset = _scrollMaxOffset > 0 ? _scrollMaxOffset : Math.Max(0, context.MaxOffset);
        _scrollMetrics = _scrollMetrics with
        {
            MetricsKnown = true,
            MaxOffsetFromBottom = maxOffset,
            ViewportRows = _scrollMetrics.ViewportRows > 0
                ? _scrollMetrics.ViewportRows
                : _scrollBar.Rows,
        };
        var offset = PaneChromeGutter.OffsetFromDragRow(
            _scrollMetrics,
            _scrollBar,
            ev.Row,
            _scrollGrabOffset);
        if (ev.Action is MouseAction.Release)
        {
            var pane = _scrollPaneId;
            _state = MouseEngineState.Idle;
            _scrollPaneId = null;
            _scrollMaxOffset = 0;
            _scrollGrabOffset = 0;
            _scrollMetrics = PaneChromeScrollState.Unknown;
            return
            [
                new MouseEngineResult(
                    MouseCommandKind.SetHistoryTop,
                    PaneId: pane,
                    Offset: offset,
                    TrackRow: ev.Row,
                    State: _state),
            ];
        }

        return
        [
            new MouseEngineResult(
                MouseCommandKind.SetHistoryTop,
                PaneId: _scrollPaneId,
                Offset: offset,
                TrackRow: ev.Row,
                State: _state),
        ];
    }

    private IReadOnlyList<MouseEngineResult> FeedSelect(MouseEvent ev, MouseFeedContext context)
    {
        if (context.ModalBlocksChrome)
        {
            Selection.Clear();
            _state = MouseEngineState.Idle;
            return [];
        }

        if (Selection.HostRange)
            Selection.ExtendHost(ev.Col, ev.Row);
        else
        {
            var next = ToPaneCell(ev, _selectContent, context.History);
            Selection.Extend(next.Row, next.Col);
        }

        if (ev.Action is not MouseAction.Release)
            return [new MouseEngineResult(MouseCommandKind.None, State: _state)];

        var pane = _selectPaneId;
        _state = MouseEngineState.Idle;
        var yank = MaybeYank(pane);
        return yank is null
            ? [new MouseEngineResult(MouseCommandKind.None, State: _state)]
            : [yank];
    }

    private IReadOnlyList<MouseEngineResult> FeedWheel(
        MouseEvent ev,
        LayoutChromeGeometry geo,
        MouseFeedContext context)
    {
        var hit = ChromeHitTest.Hit(geo, ev.Col, ev.Row);
        if (geo.MobileSwitcher is { Open: true } switcher
            && (switcher.Viewport.Contains(ev.Col, ev.Row) || hit is { Kind: ChromeHitKind.MobileSwitcherClose }))
        {
            if (hit is { Kind: ChromeHitKind.MobileSwitcherClose })
                return [];

            var switchDelta = ev.Button is MouseButton.WheelUp ? -1 : 1;
            return
            [
                new MouseEngineResult(
                    MouseCommandKind.ScrollSwitcher,
                    ScrollDelta: switchDelta,
                    State: _state),
            ];
        }

        if (SidebarContentContains(geo, ev.Col, ev.Row))
        {
            // rects. Header, footer, and the section divider do not scroll.
            var overAgents = geo.AgentsBody is { } agentsBody
                && agentsBody.Cols > 0
                && agentsBody.Rows > 0
                && agentsBody.Contains(ev.Col, ev.Row);
            var overSpaces = geo.SpacesBody is { } spacesBody
                && spacesBody.Cols > 0
                && spacesBody.Rows > 0
                && spacesBody.Contains(ev.Col, ev.Row);
            string? resourceSectionId = null;
            foreach (var section in geo.ResourceSectionBodies)
            {
                if (section.Body.Cols > 0
                    && section.Body.Rows > 0
                    && section.Body.Contains(ev.Col, ev.Row))
                {
                    resourceSectionId = section.SectionId;
                    break;
                }
            }

            var overResource = resourceSectionId is not null;
            if (!overAgents && !overSpaces && !overResource)
                return [];

            var sidebarDelta = ev.Button is MouseButton.WheelUp ? -1 : 1;
            return
            [
                new MouseEngineResult(
                    MouseCommandKind.ScrollSidebar,
                    ScrollDelta: sidebarDelta,
                    State: _state,
                    SidebarSlot: overAgents
                        ? SidebarPaneSlot.Agents
                        : overResource
                            ? SidebarPaneSlot.Resource
                            : SidebarPaneSlot.Spaces,
                    SidebarSectionId: resourceSectionId),
            ];
        }

        if (hit is { Kind: ChromeHitKind.Pane, IsFrame: false } && ChildOwns(context))
        {
            var fwd = Forward(ev, hit, geo, context);
            return fwd is null ? [] : [fwd];
        }

        var paneId = hit?.PaneId ?? context.FocusedPaneId ?? geo.FocusedPaneId;
        if (string.IsNullOrWhiteSpace(paneId))
            return [];

        var delta = ev.Button is MouseButton.WheelUp ? -_options.ScrollLines : _options.ScrollLines;
        return
        [
            new MouseEngineResult(
                MouseCommandKind.ScrollHistory,
                PaneId: paneId,
                ScrollDelta: delta,
                State: _state),
        ];
    }

    private static bool SidebarContentContains(LayoutChromeGeometry geo, int col, int row)
    {
        if (geo.Sidebar is not { } sidebar || !sidebar.Contains(col, row))
            return false;
        if (geo.SidebarEdge is { } edge && edge.Contains(col, row))
            return false;
        return true;
    }

    private IReadOnlyList<MouseEngineResult> FeedMenu(MouseEvent ev, MouseFeedContext context)
    {
        _ = context;
        if (Menu is null)
        {
            _state = MouseEngineState.Idle;
            _ignoreMenuRelease = false;
            _menuClickArmed = false;
            return [];
        }

        if (_ignoreMenuRelease && ev.Action is MouseAction.Release && ev.Button is not MouseButton.Left)
        {
            _ignoreMenuRelease = false;
            return [];
        }

        if (ev.Action is MouseAction.Move or MouseAction.Drag)
        {
            var before = Menu.Selected;
            if (Menu.TryHit(ev.Col, ev.Row, out _) && Menu.Selected != before)
                return [MenuState(MouseCommandKind.None)];
            return [];
        }

        if (ev.Action is MouseAction.Press && Menu.TryHit(ev.Col, ev.Row, out _))
        {
            _ignoreMenuRelease = false;
            if (ev.Button is MouseButton.Left)
                _menuClickArmed = true;
            return [];
        }

        if (ev.Action is MouseAction.Release
            && _menuClickArmed
            && ev.Button is MouseButton.Left
            && Menu.TryHit(ev.Col, ev.Row, out _))
        {
            return ApplyMenu(Menu.SelectedItem);
        }

        if (ev.Action is MouseAction.Press)
            return CloseMenuResults();

        return [];
    }

    private IReadOnlyList<MouseEngineResult> OpenMenu(ContextMenuModel menu)
    {
        AdoptOpenMenu(menu);
        return
        [
            MenuTargetResult(MouseCommandKind.OpenMenu, menu, item: null, _state),
        ];
    }

    internal void AdoptOpenMenu(ContextMenuModel menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        Menu = menu;
        _state = MouseEngineState.Menu;
        _ignoreMenuRelease = true;
        _menuClickArmed = false;
    }

    private IReadOnlyList<MouseEngineResult> ApplyMenu(ContextMenuItem? item)
    {
        var menu = Menu;
        var closed = CloseMenuResults();
        if (item is null || menu is null)
            return closed;

        var list = new List<MouseEngineResult>(closed)
        {
            MenuTargetResult(MouseCommandKind.ApplyMenu, menu, item, MouseEngineState.Idle),
        };
        return list;
    }

    private static MouseEngineResult MenuTargetResult(
        MouseCommandKind kind,
        ContextMenuModel menu,
        ContextMenuItem? item,
        MouseEngineState state) =>
        new(
            kind,
            Menu: menu,
            MenuItem: item,
            PaneId: menu.Kind is ContextMenuKind.Pane or ContextMenuKind.HiddenPane
                ? menu.TargetId
                : null,
            TabId: menu.Kind is ContextMenuKind.Tab ? menu.TargetId : null,
            WorkspaceId: menu.Kind is ContextMenuKind.Workspace ? menu.TargetId : null,
            PlacementId: ContextMenuModel.MoveWorkPlacementId(item?.Id, menu.Kind is ContextMenuKind.Cube ? menu.TargetId : null)
                ?? (menu.Kind is ContextMenuKind.Cube ? menu.TargetId : null),
            State: state);

    private IReadOnlyList<MouseEngineResult> CloseMenuResults()
    {
        Menu = null;
        _state = MouseEngineState.Idle;
        _ignoreMenuRelease = false;
        _menuClickArmed = false;
        return [new MouseEngineResult(MouseCommandKind.CloseMenu, State: _state)];
    }

    private MouseEngineResult MenuState(MouseCommandKind kind) =>
        new(kind, Menu: Menu, State: _state);

    private MouseEngineResult? MaybeYank(string? paneId)
    {
        if (!_options.CopyOnSelect)
            return new MouseEngineResult(MouseCommandKind.None, State: _state);

        if (Selection.HostRange)
        {
            return new MouseEngineResult(
                MouseCommandKind.Yank,
                PaneId: paneId,
                State: _state);
        }

        var text = Selection.Extract();
        if (text.Length == 0)
            return new MouseEngineResult(MouseCommandKind.None, State: _state);

        return new MouseEngineResult(
            MouseCommandKind.Yank,
            PaneId: paneId,
            Osc52: Osc52Yank.Encode(text),
            State: _state);
    }

    private MouseEngineResult? Forward(
        MouseEvent ev,
        ChromeHit hit,
        LayoutChromeGeometry geo,
        MouseFeedContext context,
        bool clampToContent = false)
    {
        if (hit.PaneId is null)
            return null;
        var pane = FindPane(geo, hit.PaneId);
        if (pane is null)
            return null;
        if (!clampToContent && !pane.Content.Contains(ev.Col, ev.Row))
            return null;

        // requires MODE_MOUSE_ANY_MOTION (1003).
        if (ev.Action is MouseAction.Move
            && !string.Equals(context.ChildMouseMode, PaneMouseMode.Any, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var maxCol = Math.Max(pane.Content.Col, pane.Content.EndCol - 1);
        var maxRow = Math.Max(pane.Content.Row, pane.Content.EndRow - 1);
        var col = Math.Clamp(ev.Col, pane.Content.Col, maxCol) - pane.Content.Col + 1;
        var row = Math.Clamp(ev.Row, pane.Content.Row, maxRow) - pane.Content.Row + 1;
        var keys = MouseDecoder.Encode(ev, col, row, context.Encoding);
        if (keys is null || keys.Length == 0)
            return null;
        return new MouseEngineResult(
            MouseCommandKind.ForwardSgr,
            PaneId: hit.PaneId,
            ForwardKeys: keys,
            State: _state);
    }

    private IReadOnlyList<MouseEngineResult> FeedChildForward(
        MouseEvent ev,
        LayoutChromeGeometry geo,
        MouseFeedContext context)
    {
        var hit = _pressHit;
        if (hit is not { Kind: ChromeHitKind.Pane, PaneId: not null, IsFrame: false })
        {
            if (ev.Action is MouseAction.Release)
            {
                _forwardingChild = false;
                _pressHit = null;
                _state = MouseEngineState.Idle;
            }

            return [];
        }

        if (ev.Action is not (MouseAction.Drag or MouseAction.Release or MouseAction.Wheel or MouseAction.Move)
            && !ev.IsWheel)
        {
            return [];
        }

        var fwd = Forward(ev, hit, geo, context, clampToContent: true);
        if (ev.Action is MouseAction.Release)
        {
            _forwardingChild = false;
            _pressHit = null;
            _state = MouseEngineState.Idle;
        }

        return fwd is null ? [] : [fwd];
    }

    private IReadOnlyList<MouseEngineResult> FeedPopup(
        MouseEvent ev,
        PopupChromeFrame popup,
        MouseFeedContext context)
    {
        var inner = popup.Inner;
        if (inner.Cols < 1 || inner.Rows < 1)
        {
            _forwardingPopup = false;
            return [];
        }

        if (ev.IsWheel || ev.Action is MouseAction.Wheel)
        {
            if (!ChildOwns(context))
                return [];
            if (!_forwardingPopup && !inner.Contains(ev.Col, ev.Row))
                return [];
            return [ForwardPopupSgr(ev, inner)];
        }

        if (ev.Action is MouseAction.Press)
        {
            if (!inner.Contains(ev.Col, ev.Row))
            {
                _forwardingPopup = false;
                return [];
            }

            _pressCol = ev.Col;
            _pressRow = ev.Row;
            _pressShift = ev.Shift;
            _pressAlt = ev.Alt;
            _pressCtrl = ev.Ctrl;
            _state = MouseEngineState.PendingClick;
            _forwardingPopup = ChildOwns(context);
            return [];
        }

        if (ev.Action is MouseAction.Drag && _state is MouseEngineState.PendingClick)
        {
            if (ev.Col == _pressCol && ev.Row == _pressRow)
                return [];
            _forwardingPopup = false;
            Selection.BeginHost(_pressCol, _pressRow);
            Selection.ExtendHost(ev.Col, ev.Row);
            _state = MouseEngineState.Selecting;
            return [new MouseEngineResult(MouseCommandKind.None, State: _state)];
        }

        if (ev.Action is MouseAction.Release && _state is MouseEngineState.PendingClick)
        {
            _state = MouseEngineState.Idle;
            if (!_forwardingPopup || !ChildOwns(context))
            {
                _forwardingPopup = false;
                return [];
            }

            _forwardingPopup = false;
            var press = new MouseEvent(
                ev.Button,
                MouseAction.Press,
                _pressCol,
                _pressRow,
                _pressShift,
                _pressAlt,
                _pressCtrl);
            return
            [
                ForwardPopupSgr(press, inner),
                ForwardPopupSgr(ev, inner),
            ];
        }

        if (!_forwardingPopup || !ChildOwns(context))
        {
            if (ev.Action is MouseAction.Release)
                _forwardingPopup = false;
            return [];
        }

        if (ev.Action is not (MouseAction.Drag or MouseAction.Release or MouseAction.Wheel)
            && !ev.IsWheel)
        {
            return [];
        }

        var fwd = ForwardPopupSgr(ev, inner);
        if (ev.Action is MouseAction.Release)
            _forwardingPopup = false;
        return [fwd];
    }

    private static MouseEngineResult ForwardPopupSgr(MouseEvent ev, CellRect inner)
    {
        var maxCol = Math.Max(inner.Col, inner.EndCol - 1);
        var maxRow = Math.Max(inner.Row, inner.EndRow - 1);
        var col = Math.Clamp(ev.Col, inner.Col, maxCol) - inner.Col + 1;
        var row = Math.Clamp(ev.Row, inner.Row, maxRow) - inner.Row + 1;
        return new MouseEngineResult(
            MouseCommandKind.ForwardSgr,
            PaneId: ProtocolEventTypes.TerminalRenderTargetPopup,
            ForwardKeys: MouseDecoder.EncodeSgr(ev, col, row),
            State: MouseEngineState.Idle);
    }

    private IReadOnlyList<MouseEngineResult> MaybeForward(
        MouseEvent ev,
        ChromeHit? hit,
        LayoutChromeGeometry geo,
        MouseFeedContext context)
    {
        if (hit is not { Kind: ChromeHitKind.Pane, IsFrame: false } || !ChildOwns(context))
            return [];
        var fwd = Forward(ev, hit, geo, context);
        return fwd is null ? [] : [fwd];
    }

    private bool ChildOwns(MouseFeedContext context) =>
        _options.Forwarder.OwnsContent(context.ChildMouseMode);

    private void NoteLeftClick(MouseEvent ev, string paneId)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var same = paneId == _lastLeftPane
            && ev.Col == _lastLeftCol
            && ev.Row == _lastLeftRow
            && now - _lastLeftMs <= DoubleClickMs;
        _clickCount = same ? _clickCount + 1 : 1;
        _lastLeftMs = now;
        _lastLeftCol = ev.Col;
        _lastLeftRow = ev.Row;
        _lastLeftPane = paneId;
    }

    private void SeedSelection(MouseFeedContext context, string paneId)
    {
        if (context.History is { IsSeeded: true } history
            && string.Equals(history.PaneId, paneId, StringComparison.Ordinal))
        {
            Selection.Seed(history.Session.CapturePaintSnapshot(), paneId);
            return;
        }

        if (context.Snapshot is { } snap
            && string.Equals(snap.PaneId, paneId, StringComparison.Ordinal))
        {
            Selection.Seed(snap);
            return;
        }

        // Fail closed: never keep another pane's grid for Extract/yank.
        Selection.Clear();
        Selection.SeedText(string.Empty, 1, paneId);
    }

    private static (int Row, int Col) ToPaneCell(
        MouseEvent ev,
        CellRect content,
        PaneHistoryView? history)
    {
        var col = ev.Col - content.Col;
        var row = ev.Row - content.Row;
        if (history is { IsSeeded: true })
            row += history.Session.ViewportTop;
        return (Math.Max(0, row), Math.Max(0, col));
    }

    private double ComputeSplitRatio(MouseEvent ev)
    {
        var parent = _splitParent;
        var horizontal = LayoutRectAllocator.IsHorizontalSplit(_splitDirection);
        double ratio;
        if (horizontal)
        {
            var span = Math.Max(1, parent.Cols);
            ratio = (ev.Col - parent.Col + 0.5) / span;
        }
        else
        {
            var span = Math.Max(1, parent.Rows);
            ratio = (ev.Row - parent.Row + 0.5) / span;
        }

        if (ratio <= 0)
            return 0.01;
        if (ratio >= 1)
            return 0.99;
        return ratio;
    }

    internal void NoteScrollMaxOffset(int maxOffset)
    {
        if (_state is not MouseEngineState.DraggingScrollbar)
            return;
        _scrollMaxOffset = Math.Max(0, maxOffset);
        _scrollMetrics = _scrollMetrics with
        {
            MetricsKnown = true,
            MaxOffsetFromBottom = _scrollMaxOffset,
        };
    }

    // Callers without full metrics
    // treat the track height as the viewport.
    internal static int MapScrollbarOffset(int y, CellRect bar, int maxOffset)
    {
        if (maxOffset <= 0 || bar.Rows <= 0)
            return 0;
        var metrics = new PaneChromeScrollState(
            MetricsKnown: true,
            MaxOffsetFromBottom: maxOffset,
            ViewportRows: bar.Rows);
        return PaneChromeGutter.OffsetFromRow(metrics, bar, y);
    }

    private bool TabDragMoved(MouseEvent ev, LayoutChromeGeometry geo)
    {
        if (geo.MobileSwitcher is { Open: true })
            return false;
        return Math.Abs(ev.Col - _dragTabStartCol) >= 1;
    }

    private static int DropTabIndex(LayoutChromeGeometry geo, int col, int row)
    {
        if (geo.MobileSwitcher is { Open: true } switcher)
            return switcher.DropTabIndex(row);

        var tabs = geo.TabBar.Tabs;
        if (tabs.Count == 0)
            return Math.Max(0, geo.TabBar.OverflowOffset);
        var visible = 0;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (col >= tabs[i].Rect.Col)
                visible = i;
        }

        var index = geo.TabBar.OverflowOffset + visible;
        var full = geo.TabBar.TabCount > 0
            ? geo.TabBar.TabCount
            : geo.TabBar.OverflowOffset + tabs.Count;
        var last = Math.Max(0, full - 1);
        return Math.Clamp(index, 0, last);
    }

    // A tab drop target is a tab body or close cell. Ghostty can send stray motion
    private static (bool IsGit, bool IsLinked, bool HasChildren, bool Collapsed) ResolveWorkspaceWorktree(
        MouseFeedContext context,
        string workspaceId)
    {
        var workspaces = context.Workspaces;
        if (workspaces is null)
            return (false, false, false, false);
        for (var i = 0; i < workspaces.Count; i++)
        {
            if (!string.Equals(workspaces[i].Id, workspaceId, StringComparison.Ordinal))
                continue;
            var workspace = workspaces[i];
            var isGit = workspace.WorktreeKey.Length > 0 || workspace.Branch.Length > 0;
            var hasChildren = WorktreeWorkspaceGrouping.ParentGroupKey(workspaces, i) is not null;
            var collapsed = hasChildren
                && context.CollapsedWorktreeGroups is { } groups
                && groups.Contains(workspace.WorktreeKey);
            return (isGit, workspace.IsLinkedWorktree, hasChildren, collapsed);
        }

        return (false, false, false, false);
    }

    // over pane content after a tab press, which must remain a click until a target is hit.
    private static ChromeHit? TabDropTarget(LayoutChromeGeometry geo, int col, int row)
    {
        var hit = ChromeHitTest.Hit(geo, col, row);
        return hit is
        {
            Kind: ChromeHitKind.Tab or ChromeHitKind.TabClose,
            TabId: { Length: > 0 },
        }
            ? hit
            : null;
    }

    private static ChromeSplitHit? FindSplit(LayoutChromeGeometry geo, IReadOnlyList<int> path)
    {
        foreach (var split in geo.SplitBorders)
        {
            if (split.Path.Count == path.Count && split.Path.SequenceEqual(path))
                return split;
        }

        return null;
    }

    private static ChromePaneFrame? FindPane(LayoutChromeGeometry geo, string paneId)
    {
        foreach (var pane in geo.Panes)
        {
            if (pane.PaneId == paneId)
                return pane;
        }

        return null;
    }
}
