using Hypa.Cli.Attach.Copy;
using Hypa.Cli.Attach.Onboarding;
using Hypa.Cli.Attach.ReleaseNotes;
using Hypa.Cli.Attach.Settings;
using Hypa.Cli.Attach.Sidebar;

namespace Hypa.Cli.Attach.Keys;

/// <summary>Feeds decoded keys. Owns mode. Does not call sockets.</summary>
public sealed class KeyEngine
{
    private KeyBindingTable _table;
    private readonly IKeyActionSink? _sink;
    private AttachChromePolicy _chrome;
    private AttachClientMode _returnMode = AttachClientMode.Terminal;
    private string _prompt = "";
    private KeyActionId? _pendingClose;
    private bool _confirmAccept;
    private bool _confirmMoveWorkAccept;
    private string? _transferPlacementId;
    private bool _dropLeftoverConfirmAccept;
    private bool _popupOpen;
    private string? _pendingClosePaneTarget;
    private string? _pendingCloseTabTarget;
    private string? _pendingCloseWorkspaceTarget;

    public KeyEngine(
        KeyBindingTable table,
        IKeyActionSink? sink = null,
        AttachChromePolicy? chrome = null,
        SettingsPageRegistry? settingsPages = null)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _sink = sink;
        _chrome = chrome ?? AttachChromePolicy.Passthrough;
        _mode = AttachClientMode.Terminal;
        _paintMode = AttachClientMode.Terminal;
        Help = KeybindHelpModel.FromTable(table);
        Copy = new CopyModeSession();
        Navigator = new GotoPickerModel();
        Settings = new SettingsOverlayModel(settingsPages ?? SettingsPageRegistry.Product());
        Onboarding = new OnboardingOverlayModel();
    }

    public KeyBindingTable Table => _table;

    /// <summary>
    /// Replace the binding table and chrome in place. Do not construct a new engine.
    /// Copy, settings, and onboarding stay.
    /// </summary>
    public void ReplaceBindings(KeyBindingTable table, AttachChromePolicy chrome)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(chrome);
        _table = table;
        _chrome = chrome;
        Help = KeybindHelpModel.FromTable(table);
    }

    // Input Mode updates on Feed. PaintMode stays on the overlay until restore
    // commits it so ReadRenderAsync cannot treat leave as a live write window.
    private volatile AttachClientMode _mode;
    private volatile AttachClientMode _paintMode;

    public AttachClientMode Mode => _mode;

    public AttachClientMode PaintMode => _paintMode;

    internal void CommitPaintMode()
    {
        if (_mode is AttachClientMode.Prefix && _returnMode is AttachClientMode.Copy)
            _paintMode = AttachClientMode.Copy;
        else
            _paintMode = _mode;
    }

    public KeybindHelpModel Help { get; private set; }

    public CopyModeSession Copy { get; }

    public GotoPickerModel Navigator { get; }

    public SettingsOverlayModel Settings { get; }

    public OnboardingOverlayModel Onboarding { get; }

    public ReleaseNotesOverlayModel ReleaseNotes { get; } = new();

    /// <summary>
    /// When true, prefix+b and prefix+space open the mobile switcher
    /// instead of the wide sidebar or navigate mode.
    /// </summary>
    public bool NarrowLayout { get; set; }

    /// <summary>
    /// Session-modal popup is open. All keys including Escape and prefix
    /// go to the popup PTY until command exit or <c>popup.close</c>.
    /// Opening a popup clears a leftover confirm-accept drop.
    /// </summary>
    public bool PopupOpen
    {
        get => _popupOpen;
        set
        {
            _popupOpen = value;
            if (value)
                _dropLeftoverConfirmAccept = false;
        }
    }

    public bool OverlayOpen { get; set; }

    /// <summary>
    /// Exclusive surface outside <see cref="Mode"/> (worktree dialogs).
    /// Hidden list or context can set this later without dropping
    /// worktree busy.
    /// </summary>
    public bool ExclusiveSurfaceOpen { get; set; }

    public string? OverlayPaneId { get; set; }

    public string PromptText => _prompt;

    /// <summary>
    /// DECCKM on the focused pane. The host TTY stays in normal cursor mode.
    /// The pane input path translates cursor keys.
    /// </summary>
    public bool ApplicationCursor { get; set; }

    public bool WantsServerStop => false;

    public IReadOnlyList<KeyEngineEvent> Feed(ReadOnlySpan<byte> bytes)
    {
        var events = new List<KeyEngineEvent>();
        foreach (var decoded in KeyEventDecoder.Decode(bytes))
            events.AddRange(Feed(decoded.Chord, decoded.Raw));
        return events;
    }

    public IReadOnlyList<KeyEngineEvent> Feed(KeyChord chord) =>
        Feed(chord, KeyEventDecoder.Encode(chord));

    public IReadOnlyList<KeyEngineEvent> Feed(byte b) => Feed([b]);

    public IReadOnlyList<KeyEngineEvent> EnterContextMenu()
    {
        if (Mode is AttachClientMode.ContextMenu)
            return [];
        return Enter(AttachClientMode.ContextMenu, pinReturn: AttachClientMode.Terminal);
    }

    public IReadOnlyList<KeyEngineEvent> EnterGlobalMenu()
    {
        if (Mode is AttachClientMode.GlobalMenu)
            return [];
        return Enter(AttachClientMode.GlobalMenu, pinReturn: AttachClientMode.Terminal);
    }

    public IReadOnlyList<KeyEngineEvent> EnterWhatsNew(PackNotesDocument? notes = null)
    {
        if (Mode is AttachClientMode.WhatsNew)
            return [];
        if (notes is null || !ReleaseNotes.Open(notes))
            return [];
        return Enter(AttachClientMode.WhatsNew, pinReturn: AttachClientMode.Terminal);
    }

    public IReadOnlyList<KeyEngineEvent> EnterOnboarding()
    {
        if (Mode is AttachClientMode.Onboarding)
            return [];
        if (!Onboarding.IsOpen)
            Onboarding.Open(_table.PrefixChord.Format());
        return Enter(AttachClientMode.Onboarding, pinReturn: AttachClientMode.Terminal);
    }

    public IReadOnlyList<KeyEngineEvent> EnterSettings(string? pageId = null)
    {
        if (Onboarding.IsOpen)
            Onboarding.Close();
        if (Mode is AttachClientMode.Settings)
            return [];
        if (!Settings.IsOpen)
            Settings.Open(pageId);
        else if (pageId is not null)
            Settings.SelectPage(pageId);
        return Enter(AttachClientMode.Settings, pinReturn: AttachClientMode.Terminal);
    }

    public IReadOnlyList<KeyEngineEvent> LeaveSettings(bool restore)
    {
        if (Mode is not AttachClientMode.Settings
            && Mode is not AttachClientMode.Prefix)
        {
            if (restore)
                Settings.Cancel();
            else
                Settings.Commit();
            return [];
        }

        if (restore)
            Settings.Cancel();
        else
            Settings.Commit();
        return LeaveTo(AttachClientMode.Terminal);
    }

    public IReadOnlyList<KeyEngineEvent> EnterNavigate()
    {
        if (Mode is AttachClientMode.Navigate)
            return [];
        return Enter(AttachClientMode.Navigate, pinReturn: AttachClientMode.Terminal);
    }

    public IReadOnlyList<KeyEngineEvent> EnterMobileSwitcher()
    {
        if (Mode is AttachClientMode.MobileSwitcher)
            return [];
        return Enter(AttachClientMode.MobileSwitcher, pinReturn: AttachClientMode.Terminal);
    }

    public IReadOnlyList<KeyEngineEvent> LeaveMobileSwitcher()
    {
        if (Mode is not AttachClientMode.MobileSwitcher)
            return [];
        return LeaveTo(AttachClientMode.Terminal);
    }

    public IReadOnlyList<KeyEngineEvent> LeaveContextMenu()
    {
        if (Mode is not AttachClientMode.ContextMenu)
            return [];
        return LeaveTo(AttachClientMode.Terminal);
    }

    public IReadOnlyList<KeyEngineEvent> LeaveGlobalMenu()
    {
        if (Mode is not AttachClientMode.GlobalMenu)
            return [];
        return LeaveTo(AttachClientMode.Terminal);
    }

    /// <summary>Leaves copy, help, resize, prefix, and navigate without a submit.</summary>
    public IReadOnlyList<KeyEngineEvent> CancelInteractiveMode()
    {
        if (Mode is AttachClientMode.TransferPicker)
            return CancelTransferPicker();

        return Mode switch
        {
            AttachClientMode.Copy => LeaveCopy(),
            AttachClientMode.Settings => LeaveSettings(restore: true),
            AttachClientMode.KeybindHelp
                or AttachClientMode.Resize
                or AttachClientMode.Navigate
                or AttachClientMode.Navigator
                or AttachClientMode.WorkspacePicker
                or AttachClientMode.GlobalMenu
                or AttachClientMode.WhatsNew
                or AttachClientMode.MobileSwitcher
                or AttachClientMode.Prefix => LeaveTo(AttachClientMode.Terminal),
            _ => [],
        };
    }

    /// <summary>Leaves confirm-close without dispatching the pending close.</summary>
    public IReadOnlyList<KeyEngineEvent> CancelConfirmClose()
    {
        if (Mode is not AttachClientMode.ConfirmClose)
            return [];
        ClearPendingClose();
        return LeaveTo(AttachClientMode.Terminal);
    }

    /// <summary>Enters Move Work confirm. Not gated by <c>ui.confirm_close</c>.</summary>
    public IReadOnlyList<KeyEngineEvent> EnterConfirmMoveWork(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        _confirmMoveWorkAccept = false;
        _prompt = prompt;
        if (Mode is AttachClientMode.ConfirmMoveWork)
            return [];
        var events = new List<KeyEngineEvent>();
        if (Mode is AttachClientMode.ContextMenu or AttachClientMode.GlobalMenu)
            events.AddRange(LeaveTo(AttachClientMode.ConfirmMoveWork));
        if (Mode != AttachClientMode.ConfirmMoveWork)
            events.AddRange(Enter(AttachClientMode.ConfirmMoveWork, pinReturn: AttachClientMode.Terminal));
        return events;
    }

    /// <summary>Leaves Move Work confirm without calling handoff.</summary>
    public IReadOnlyList<KeyEngineEvent> CancelConfirmMoveWork()
    {
        if (Mode is not AttachClientMode.ConfirmMoveWork)
            return [];
        _confirmMoveWorkAccept = false;
        _prompt = "";
        return LeaveTo(AttachClientMode.Terminal);
    }

    /// <summary>Enters the Transfer destination picker from a pane menu.</summary>
    public IReadOnlyList<KeyEngineEvent> EnterTransferPicker()
    {
        Navigator.Reset();
        Navigator.TransferMode = true;
        _transferPlacementId = null;
        if (Mode is AttachClientMode.TransferPicker)
            return [];
        var events = new List<KeyEngineEvent>();
        if (Mode is AttachClientMode.ContextMenu or AttachClientMode.GlobalMenu)
            events.AddRange(LeaveTo(AttachClientMode.TransferPicker));
        if (Mode != AttachClientMode.TransferPicker)
            events.AddRange(Enter(AttachClientMode.TransferPicker, pinReturn: AttachClientMode.Terminal));
        return events;
    }

    /// <summary>Leaves Transfer picker without calling handoff.</summary>
    public IReadOnlyList<KeyEngineEvent> CancelTransferPicker()
    {
        if (Mode is not AttachClientMode.TransferPicker)
            return [];
        _transferPlacementId = null;
        Navigator.Reset();
        return LeaveTo(AttachClientMode.Terminal);
    }

    /// <summary>Applies a chrome hit or other synthetic action through the same policy as keys.</summary>
    public IReadOnlyList<KeyEngineEvent> RequestAction(KeyActionId action, int? index = null) =>
        RequestAction(action, index, promptName: true);

    /// <summary>
    /// Mouse + and tab-menu New tab skip the name prompt. Prefix+c still uses
    /// <see cref="AttachChromePolicy.PromptNewTabName"/>.
    /// </summary>
    public IReadOnlyList<KeyEngineEvent> RequestAction(KeyActionId action, int? index, bool promptName)
    {
        if (Mode is AttachClientMode.ConfirmClose && !IsCloseAction(action))
        {
            ClearPendingClose();
            var events = new List<KeyEngineEvent>();
            events.AddRange(LeaveTo(AttachClientMode.Terminal));
            events.AddRange(DispatchCore(new BoundAction(action, index), fromPrefix: false, promptName));
            return events;
        }

        if (Mode is AttachClientMode.ConfirmMoveWork)
        {
            _confirmMoveWorkAccept = false;
            _prompt = "";
            var events = new List<KeyEngineEvent>();
            events.AddRange(LeaveTo(AttachClientMode.Terminal));
            events.AddRange(DispatchCore(new BoundAction(action, index), fromPrefix: false, promptName));
            return events;
        }

        if (Mode is AttachClientMode.TransferPicker)
        {
            _transferPlacementId = null;
            Navigator.Reset();
            var events = new List<KeyEngineEvent>();
            events.AddRange(LeaveTo(AttachClientMode.Terminal));
            events.AddRange(DispatchCore(new BoundAction(action, index), fromPrefix: false, promptName));
            return events;
        }

        return DispatchCore(new BoundAction(action, index), fromPrefix: false, promptName);
    }

    internal bool HasConfirmAccept => _confirmAccept;

    internal bool HasConfirmMoveWorkAccept => _confirmMoveWorkAccept;

    internal bool HasTransferPick => !string.IsNullOrWhiteSpace(_transferPlacementId);

    internal string? PendingClosePaneTarget => _pendingClosePaneTarget;

    internal void BindPendingCloseTarget(string? paneId, string? tabId, string? workspaceId)
    {
        _pendingClosePaneTarget = string.IsNullOrWhiteSpace(paneId) ? null : paneId;
        _pendingCloseTabTarget = string.IsNullOrWhiteSpace(tabId) ? null : tabId;
        _pendingCloseWorkspaceTarget = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId;
    }

    internal void ClearBoundCloseTargets()
    {
        _pendingClosePaneTarget = null;
        _pendingCloseTabTarget = null;
        _pendingCloseWorkspaceTarget = null;
    }

    internal bool TakeConfirmAccept()
    {
        var accept = _confirmAccept;
        _confirmAccept = false;
        return accept;
    }

    internal bool TakeConfirmMoveWorkAccept()
    {
        var accept = _confirmMoveWorkAccept;
        _confirmMoveWorkAccept = false;
        return accept;
    }

    internal string? TakeTransferPlacementId()
    {
        var id = _transferPlacementId;
        _transferPlacementId = null;
        return id;
    }

    /// <summary>
    /// Drops the next leftover y/enter after an editor overlay dismisses
    /// confirm-close. Other keys clear the arm without being dropped.
    /// </summary>
    internal void ArmDropLeftoverConfirmAccept() => _dropLeftoverConfirmAccept = true;

    /// <summary>
    /// Returns false with <c>ui_busy</c> when a modal (or another popup) is active.
    /// Custom commands leave Prefix/Copy to Terminal before <c>popup.open</c>.
    /// Settings, Help, and an already-open popup stay busy.
    /// </summary>
    public bool TryBeginPopup(out string? busyMessage)
    {
        if (PopupOpen || OverlayOpen || ExclusiveSurfaceOpen || Mode is not AttachClientMode.Terminal)
        {
            busyMessage = "ui_busy";
            return false;
        }

        busyMessage = null;
        return true;
    }

    /// <summary>
    /// Session-modal popup owns the surface. Leave Settings/Copy/Help so paint
    /// </summary>
    public IReadOnlyList<KeyEngineEvent> ForceTerminalForPopup()
    {
        var events = new List<KeyEngineEvent>();
        events.AddRange(CancelInteractiveMode());
        events.AddRange(CancelConfirmClose());
        events.AddRange(CancelConfirmMoveWork());
        events.AddRange(CancelTransferPicker());
        if (Mode is AttachClientMode.Settings)
            events.AddRange(LeaveSettings(restore: true));
        if (Mode is AttachClientMode.Onboarding)
        {
            Onboarding.Close();
            events.AddRange(LeaveTo(AttachClientMode.Terminal));
        }

        if (Mode is not AttachClientMode.Terminal)
            events.AddRange(LeaveTo(AttachClientMode.Terminal));
        _dropLeftoverConfirmAccept = false;
        CommitPaintMode();
        return events;
    }

    /// <summary>Wire token for <c>client_mode</c> / lease-scoped UI lock.</summary>
    public string ClientModeToken =>
        OverlayOpen
            ? "overlay"
            : Mode is AttachClientMode.Terminal or AttachClientMode.Prefix
                ? "terminal"
                : Mode.ToString().ToLowerInvariant();

    private IReadOnlyList<KeyEngineEvent> Feed(KeyChord chord, byte[] raw)
    {
        if (PopupOpen)
        {
            _dropLeftoverConfirmAccept = false;
            return SendPopupBytes(raw);
        }

        if (OverlayOpen)
        {
            _dropLeftoverConfirmAccept = false;
            if (chord.IsEscape && !chord.Ctrl && !chord.Alt && !chord.Prefix)
                return [new KeyEngineEvent(KeyEngineEventKind.HideOverlay, TargetId: OverlayPaneId)];
            return
            [
                new KeyEngineEvent(
                    KeyEngineEventKind.SendPaneBytes,
                    Bytes: raw,
                    TargetId: OverlayPaneId),
            ];
        }

        if (TryDropLeftoverConfirmAccept(chord))
            return [];

        return Mode switch
        {
            AttachClientMode.KeybindHelp => FeedHelp(chord),
            AttachClientMode.Navigator => FeedNavigator(chord),
            AttachClientMode.WorkspacePicker => FeedNavigator(chord),
            AttachClientMode.TransferPicker => FeedNavigator(chord),
            AttachClientMode.WhatsNew => FeedWhatsNew(chord),
            AttachClientMode.RenameWorkspace
                or AttachClientMode.RenameTab
                or AttachClientMode.RenamePane
                or AttachClientMode.NewTabName
                or AttachClientMode.NewWorkspaceName => FeedPrompt(chord, raw),
            AttachClientMode.ConfirmClose => FeedConfirm(chord, raw),
            AttachClientMode.ConfirmMoveWork => FeedConfirmMoveWork(chord, raw),
            AttachClientMode.ContextMenu => FeedContextMenu(chord),
            AttachClientMode.GlobalMenu => FeedContextMenu(chord),
            AttachClientMode.MobileSwitcher => FeedMobileSwitcher(chord, raw),
            AttachClientMode.Prefix => FeedPrefix(chord, raw),
            AttachClientMode.Navigate => FeedNavigate(chord, raw),
            AttachClientMode.Copy => FeedCopy(chord, raw),
            AttachClientMode.Resize => FeedResize(chord, raw),
            AttachClientMode.Settings => FeedSettings(chord, raw),
            AttachClientMode.Onboarding => FeedOnboarding(chord, raw),
            _ => FeedTerminal(chord, raw),
        };
    }

    private List<KeyEngineEvent> FeedTerminal(KeyChord chord, byte[] raw)
    {
        if (chord.Equals(_table.PrefixChord))
            return Enter(AttachClientMode.Prefix, pinReturn: AttachClientMode.Terminal);

        if (_table.TryLookup(AttachClientMode.Terminal, chord, out var bound))
        {
            if (KeyActionNames.IsRemoteOnly(bound.Action))
                return SendBytes(PaneBytes(chord, raw));
            return DispatchFromTerminal(bound);
        }

        return SendBytes(PaneBytes(chord, raw));
    }

    private byte[] PaneBytes(KeyChord chord, byte[] raw)
    {
        if (chord.Prefix || chord.Ctrl || chord.Alt || chord.Shift)
            return raw;
        if (chord.Key is not ("up" or "down" or "left" or "right" or "home" or "end"))
            return raw;
        return KeyEventDecoder.EncodePlainCursor(chord.Key, ApplicationCursor);
    }

    private List<KeyEngineEvent> FeedPrefix(KeyChord chord, byte[] raw)
    {
        _ = raw;
        if (chord.Equals(_table.PrefixChord))
        {
            var dest = _returnMode;
            var events = LeaveTo(dest);
            events.Insert(0, new KeyEngineEvent(
                KeyEngineEventKind.SendPaneBytes,
                Bytes: [.. _table.PrefixBytes]));
            return events;
        }

        if (chord.IsEscape)
            return LeaveTo(_returnMode);

        if (_table.TryLookup(AttachClientMode.Prefix, chord, out var bound))
            return DispatchFromPrefix(bound);

        return LeaveTo(_returnMode);
    }

    private List<KeyEngineEvent> FeedNavigate(KeyChord chord, byte[] raw)
    {
        _ = raw;
        if (chord.Equals(_table.PrefixChord))
            return Enter(AttachClientMode.Prefix, pinReturn: AttachClientMode.Navigate);

        if (chord.IsEscape)
            return LeaveTo(AttachClientMode.Terminal);

        if (chord.IsEnter)
            return [new KeyEngineEvent(KeyEngineEventKind.MenuApply)];

        if (_table.TryLookup(AttachClientMode.Navigate, chord, out var bound))
            return DispatchOnly(bound);

        return [];
    }

    private List<KeyEngineEvent> FeedCopy(KeyChord chord, byte[] raw)
    {
        _ = raw;
        if (chord.Equals(_table.PrefixChord))
            return Enter(AttachClientMode.Prefix, pinReturn: AttachClientMode.Copy);

        if (chord.IsEscape)
            return FeedCopyEscape();

        if (Copy.SearchPromptActive)
            return FeedCopySearchPrompt(chord);

        if (chord is { Ctrl: false, Alt: false, Shift: false, Key: "q" })
            return LeaveCopy();

        if (TryCopyMotion(chord, out var moved) && moved)
            return CopyChanged();

        if (chord is { Ctrl: false, Alt: false, Key: "slash" or "/" })
        {
            Copy.BeginSearch(forward: true);
            return CopyChanged();
        }

        if (chord is { Ctrl: false, Alt: false, Shift: false, Key: "?" })
        {
            Copy.BeginSearch(forward: false);
            return CopyChanged();
        }

        if (chord is { Ctrl: false, Alt: false, Shift: false, Key: "n" })
        {
            if (Copy.RepeatSearch(reverse: false))
                return CopyChanged();
            return [];
        }

        if (chord is { Ctrl: false, Alt: false, Shift: true, Key: "n" })
        {
            if (Copy.RepeatSearch(reverse: true))
                return CopyChanged();
            return [];
        }

        if (chord is { Ctrl: false, Alt: false, Key: "v" or "space" })
        {
            if (Copy.StartSelection())
                return CopyChanged();
            return [];
        }

        if (chord.IsEnter || chord is { Ctrl: false, Alt: false, Shift: false, Key: "y" })
            return FeedCopyYank();

        return [];
    }

    private List<KeyEngineEvent> FeedCopyEscape()
    {
        if (Copy.ClearSelection())
            return CopyChanged();
        if (Copy.ClearSearch())
            return CopyChanged();
        return LeaveCopy();
    }

    private List<KeyEngineEvent> FeedCopySearchPrompt(KeyChord chord)
    {
        if (chord.IsEnter)
        {
            Copy.CommitSearch();
            return CopyChanged();
        }

        if (chord is { Key: "backspace", Ctrl: false })
        {
            Copy.SearchBackspace();
            return CopyChanged();
        }

        if (TryPrintable(chord, out var text))
        {
            Copy.AppendSearch(text);
            return CopyChanged();
        }

        return [];
    }

    private List<KeyEngineEvent> FeedCopyYank()
    {
        var text = Copy.Yank();
        if (text is null)
            return [];
        return
        [
            new KeyEngineEvent(KeyEngineEventKind.CopyYank, Bytes: Osc52Yank.Encode(text), PromptText: text),
            new KeyEngineEvent(KeyEngineEventKind.CopyChanged),
        ];
    }

    private List<KeyEngineEvent> LeaveCopy()
    {
        Copy.Reset();
        return LeaveTo(AttachClientMode.Terminal);
    }

    private static List<KeyEngineEvent> CopyChanged() =>
        [new KeyEngineEvent(KeyEngineEventKind.CopyChanged)];

    private bool TryCopyMotion(KeyChord chord, out bool moved)
    {
        moved = false;
        if (chord.Alt)
            return false;

        if (chord is { Ctrl: false, Shift: false, Key: "h" or "left" })
        {
            moved = Copy.MoveLeft();
            return true;
        }

        if (chord is { Ctrl: false, Shift: false, Key: "l" or "right" })
        {
            moved = Copy.MoveRight();
            return true;
        }

        if (chord is { Ctrl: false, Shift: false, Key: "k" or "up" })
        {
            moved = Copy.MoveUp();
            return true;
        }

        if (chord is { Ctrl: false, Shift: false, Key: "j" or "down" })
        {
            moved = Copy.MoveDown();
            return true;
        }

        if (chord is { Ctrl: false, Shift: false, Key: "w" })
        {
            moved = Copy.WordForward(big: false);
            return true;
        }

        if (chord is { Ctrl: false, Shift: false, Key: "b" })
        {
            moved = Copy.WordBack(big: false);
            return true;
        }

        if (chord is { Ctrl: false, Shift: false, Key: "e" })
        {
            moved = Copy.WordEnd(big: false);
            return true;
        }

        if (chord is { Ctrl: false, Shift: true, Key: "w" })
        {
            moved = Copy.WordForward(big: true);
            return true;
        }

        if (chord is { Ctrl: false, Shift: true, Key: "b" })
        {
            moved = Copy.WordBack(big: true);
            return true;
        }

        if (chord is { Ctrl: false, Shift: true, Key: "e" })
        {
            moved = Copy.WordEnd(big: true);
            return true;
        }

        if (chord is { Ctrl: false, Key: "{" })
        {
            moved = Copy.ParagraphBack();
            return true;
        }

        if (chord is { Ctrl: false, Key: "}" })
        {
            moved = Copy.ParagraphForward();
            return true;
        }

        if (chord is { Ctrl: false, Key: "pageup" })
        {
            moved = Copy.PageUp();
            return true;
        }

        if (chord is { Ctrl: false, Key: "pagedown" })
        {
            moved = Copy.PageDown();
            return true;
        }

        if (chord is { Ctrl: true, Shift: false, Key: "f" })
        {
            moved = Copy.PageDown();
            return true;
        }

        if (chord is { Ctrl: true, Shift: false, Key: "u" })
        {
            moved = Copy.HalfUp();
            return true;
        }

        if (chord is { Ctrl: true, Shift: false, Key: "d" })
        {
            moved = Copy.HalfDown();
            return true;
        }

        if (chord is { Ctrl: true, Shift: false, Key: "b" } &&
            !chord.Equals(_table.PrefixChord))
        {
            moved = Copy.PageUp();
            return true;
        }

        return false;
    }

    private List<KeyEngineEvent> FeedResize(KeyChord chord, byte[] raw)
    {
        _ = raw;
        if (chord.Equals(_table.PrefixChord))
            return Enter(AttachClientMode.Prefix, pinReturn: AttachClientMode.Resize);

        if (chord.IsEscape)
            return LeaveTo(AttachClientMode.Terminal);

        if (_table.TryLookup(AttachClientMode.Resize, chord, out var bound))
            return DispatchOnly(bound);

        return [];
    }

    private List<KeyEngineEvent> FeedSettings(KeyChord chord, byte[] raw)
    {
        _ = raw;
        if (chord.Equals(_table.PrefixChord))
            return Enter(AttachClientMode.Prefix, pinReturn: AttachClientMode.Settings);

        if (chord.IsEscape)
            return [.. LeaveSettings(restore: true)];

        if (chord.IsTab
            || chord is { Key: "right" or "l", Ctrl: false, Alt: false, Shift: false })
        {
            Settings.SelectNext();
            return OverlayChanged(SettingsOverlayModel.FilterPage);
        }

        if (chord.IsShiftTab
            || chord is { Key: "left" or "h", Ctrl: false, Alt: false, Shift: false })
        {
            Settings.SelectPrev();
            return OverlayChanged(SettingsOverlayModel.FilterPage);
        }

        if (chord is { Key: "down" or "j", Ctrl: false, Alt: false, Shift: false })
        {
            Settings.MoveList(1);
            return OverlayChanged(SettingsOverlayModel.FilterPreview);
        }

        if (chord is { Key: "up" or "k", Ctrl: false, Alt: false, Shift: false })
        {
            Settings.MoveList(-1);
            return OverlayChanged(SettingsOverlayModel.FilterPreview);
        }

        if (Settings.ActivePage.Kind is SettingsPageKind.Integrations
            && chord is { Key: "space", Ctrl: false, Alt: false, Shift: false })
        {
            Settings.ToggleHighlightedSelection();
            return OverlayChanged(SettingsOverlayModel.FilterPreview);
        }

        if (Settings.ActivePage.Kind is SettingsPageKind.Integrations
            && chord is { Key: "u", Ctrl: false, Alt: false, Shift: false })
        {
            if (!Settings.RequestUninstall())
                return [];
            return OverlayChanged(SettingsOverlayModel.FilterUninstall);
        }

        if (chord.IsEnter || chord is { Key: "space", Ctrl: false, Alt: false, Shift: false })
        {
            _ = Settings.Apply();
            return OverlayChanged(SettingsOverlayModel.FilterApply);
        }

        return [];
    }

    private List<KeyEngineEvent> FeedOnboarding(KeyChord chord, byte[] raw)
    {
        _ = raw;
        if (Onboarding.PrefixArmed)
        {
            Onboarding.ClearPrefixArmed();
            if (_table.TryLookup(AttachClientMode.Prefix, chord, out var bound)
                && bound.Action is KeyActionId.Detach)
            {
                return EmitDetach();
            }

            return [];
        }

        if (chord.Equals(_table.PrefixChord))
        {
            Onboarding.ArmPrefix();
            return [];
        }

        if (chord.IsEnter
            || chord is { Key: "l" or "right", Ctrl: false, Alt: false, Shift: false })
        {
            Onboarding.RequestComplete();
            return OverlayChanged(OnboardingOverlayModel.FilterComplete);
        }

        return [];
    }

    private static List<KeyEngineEvent> OverlayChanged(string filter) =>
        [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: filter)];

    private List<KeyEngineEvent> FeedWhatsNew(KeyChord chord)
    {
        var maxScroll = ReleaseNotes.Layout?.MaxScroll ?? 0;
        if (chord is { Key: "down" or "j", Ctrl: false, Alt: false, Shift: false })
        {
            if (ReleaseNotes.ScrollBy(1, maxScroll))
                return OverlayChanged(ReleaseNotesOverlayModel.FilterScroll);
            return [];
        }

        if (chord is { Key: "up" or "k", Ctrl: false, Alt: false, Shift: false })
        {
            if (ReleaseNotes.ScrollBy(-1, maxScroll))
                return OverlayChanged(ReleaseNotesOverlayModel.FilterScroll);
            return [];
        }

        if (chord is { Key: "g", Ctrl: false, Alt: false, Shift: true })
        {
            if (ReleaseNotes.SetScroll(0, maxScroll))
                return OverlayChanged(ReleaseNotesOverlayModel.FilterScroll);
            return [];
        }

        if (chord is { Key: "g", Ctrl: false, Alt: false, Shift: false })
        {
            if (ReleaseNotes.SetScroll(maxScroll, maxScroll))
                return OverlayChanged(ReleaseNotesOverlayModel.FilterScroll);
            return [];
        }

        if (chord.IsEscape || chord is { Ctrl: false, Alt: false, Key: "q" } || chord.IsEnter)
            return DismissReleaseNotes();
        return [];
    }

    private List<KeyEngineEvent> DismissReleaseNotes()
    {
        ReleaseNotes.Close();
        return
        [
            .. LeaveTo(AttachClientMode.Terminal),
            new KeyEngineEvent(
                KeyEngineEventKind.HelpFilter,
                Filter: ReleaseNotesOverlayModel.FilterDismiss),
        ];
    }

    private List<KeyEngineEvent> FeedNavigator(KeyChord chord)
    {
        if (chord.IsEscape)
        {
            _transferPlacementId = null;
            return LeaveTo(AttachClientMode.Terminal);
        }

        if (!Navigator.FilterFocused && chord is { Ctrl: false, Alt: false, Key: "q" })
        {
            _transferPlacementId = null;
            return LeaveTo(AttachClientMode.Terminal);
        }

        if (chord.IsEnter)
        {
            var selected = Navigator.SelectedMatch;
            if (selected is null)
            {
                _transferPlacementId = null;
                return LeaveTo(AttachClientMode.Terminal);
            }

            if (Mode is AttachClientMode.TransferPicker)
            {
                _transferPlacementId = selected.Target.Id;
                return LeaveTo(AttachClientMode.Terminal);
            }

            var events = LeaveTo(AttachClientMode.Terminal);
            events.Insert(0, new KeyEngineEvent(
                KeyEngineEventKind.Dispatch,
                Action: KeyActionId.Goto,
                Index: (int)selected.Target.Kind,
                PromptText: selected.Target.Id));
            return events;
        }

        // Arrows always move. j/k move only when the filter is not focused so
        // they stay typeable in the query.
        if (chord is { Key: "down", Ctrl: false, Alt: false }
            || (chord is { Key: "j", Ctrl: false, Alt: false } && !Navigator.FilterFocused))
        {
            Navigator.Move(1);
            return [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: Navigator.Filter)];
        }

        if (chord is { Key: "up", Ctrl: false, Alt: false }
            || (chord is { Key: "k", Ctrl: false, Alt: false } && !Navigator.FilterFocused))
        {
            Navigator.Move(-1);
            return [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: Navigator.Filter)];
        }

        if (!Navigator.FilterFocused && chord is { Ctrl: false, Alt: false, Key: "slash" or "/" })
        {
            Navigator.FocusFilter();
            return [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: Navigator.Filter)];
        }

        if (chord is { Ctrl: true, Key: "u" })
        {
            Navigator.ClearFilter();
            return [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: Navigator.Filter)];
        }

        if (chord is { Key: "backspace", Ctrl: false })
        {
            Navigator.Backspace();
            return [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: Navigator.Filter)];
        }

        if (TryPrintable(chord, out var text))
        {
            if (!Navigator.FilterFocused)
                Navigator.FocusFilter();
            Navigator.AppendFilter(text);
            return [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: Navigator.Filter)];
        }

        return [];
    }

    private List<KeyEngineEvent> FeedHelp(KeyChord chord)
    {
        if (chord.IsEscape)
            return LeaveTo(AttachClientMode.Terminal);

        if (!Help.FilterFocused && chord is { Ctrl: false, Alt: false, Key: "q" })
            return LeaveTo(AttachClientMode.Terminal);

        if (!Help.FilterFocused && chord is { Ctrl: false, Alt: false, Key: "slash" or "/" })
        {
            Help.FocusFilter();
            return [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: Help.Filter)];
        }

        if (chord is { Ctrl: true, Key: "u" })
        {
            Help.ClearFilter();
            return [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: Help.Filter)];
        }

        if (chord is { Key: "backspace", Ctrl: false })
        {
            Help.Backspace();
            return [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: Help.Filter)];
        }

        if (TryPrintable(chord, out var text))
        {
            if (!Help.FilterFocused)
                Help.FocusFilter();
            Help.AppendFilter(text);
            return [new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: Help.Filter)];
        }

        return [];
    }

    private List<KeyEngineEvent> FeedPrompt(KeyChord chord, byte[] raw)
    {
        _ = raw;
        if (chord.IsEscape)
        {
            _prompt = "";
            return LeaveTo(AttachClientMode.Terminal);
        }

        if (chord.IsEnter)
        {
            var text = _prompt;
            _prompt = "";
            var action = Mode switch
            {
                AttachClientMode.RenameTab => KeyActionId.RenameTab,
                AttachClientMode.RenamePane => KeyActionId.RenamePane,
                AttachClientMode.NewTabName => KeyActionId.NewTab,
                AttachClientMode.NewWorkspaceName => KeyActionId.NewWorkspace,
                _ => KeyActionId.RenameWorkspace,
            };
            var events = LeaveTo(AttachClientMode.Terminal);
            events.InsertRange(0, EmitDispatch(action, null, text));
            return events;
        }

        if (chord is { Key: "backspace", Ctrl: false })
        {
            if (_prompt.Length > 0)
                _prompt = _prompt[..^1];
            return [new KeyEngineEvent(KeyEngineEventKind.PromptEdit, PromptText: _prompt)];
        }

        if (TryPrintable(chord, out var ch))
        {
            _prompt += ch;
            return [new KeyEngineEvent(KeyEngineEventKind.PromptEdit, PromptText: _prompt)];
        }

        return [];
    }

    private List<KeyEngineEvent> FeedMobileSwitcher(KeyChord chord, byte[] raw)
    {
        _ = raw;
        if (chord.Equals(_table.PrefixChord))
            return Enter(AttachClientMode.Prefix, pinReturn: AttachClientMode.MobileSwitcher);

        if (chord.IsEscape || chord is { Key: "q", Ctrl: false, Alt: false })
            return LeaveTo(AttachClientMode.Terminal);

        if (chord is { Key: "j" or "down", Ctrl: false, Alt: false })
            return [new KeyEngineEvent(KeyEngineEventKind.MenuMove, Index: 1)];

        if (chord is { Key: "k" or "up", Ctrl: false, Alt: false })
            return [new KeyEngineEvent(KeyEngineEventKind.MenuMove, Index: -1)];

        if (chord.IsEnter)
            return [new KeyEngineEvent(KeyEngineEventKind.MenuApply)];

        return [];
    }

    private List<KeyEngineEvent> FeedContextMenu(KeyChord chord)
    {
        if (chord.IsEscape)
            return LeaveTo(AttachClientMode.Terminal);

        if (chord is { Key: "j" or "down", Ctrl: false, Alt: false })
            return [new KeyEngineEvent(KeyEngineEventKind.MenuMove, Index: 1)];

        if (chord is { Key: "k" or "up", Ctrl: false, Alt: false })
            return [new KeyEngineEvent(KeyEngineEventKind.MenuMove, Index: -1)];

        if (chord.IsEnter)
            return [new KeyEngineEvent(KeyEngineEventKind.MenuApply)];

        return [];
    }

    private List<KeyEngineEvent> FeedConfirm(KeyChord chord, byte[] raw)
    {
        _ = raw;
        if (chord.IsEscape || chord is { Key: "n", Ctrl: false, Alt: false })
        {
            ClearPendingClose();
            return LeaveTo(AttachClientMode.Terminal);
        }

        if (chord.IsEnter || chord is { Key: "y", Ctrl: false, Alt: false })
        {
            var action = _pendingClose ?? KeyActionId.ClosePane;
            var target = CloseTargetFor(action);
            _pendingClose = null;
            _confirmAccept = true;
            var events = LeaveTo(AttachClientMode.Terminal);
            events.InsertRange(0, EmitDispatch(action, null, targetId: target));
            return events;
        }

        return [];
    }

    private List<KeyEngineEvent> FeedConfirmMoveWork(KeyChord chord, byte[] raw)
    {
        _ = raw;
        if (chord.IsEscape || chord is { Key: "n", Ctrl: false, Alt: false })
        {
            _confirmMoveWorkAccept = false;
            _prompt = "";
            return LeaveTo(AttachClientMode.Terminal);
        }

        if (chord.IsEnter || chord is { Key: "y", Ctrl: false, Alt: false })
        {
            _confirmMoveWorkAccept = true;
            return LeaveTo(AttachClientMode.Terminal);
        }

        return [];
    }

    private List<KeyEngineEvent> DispatchFromPrefix(BoundAction bound)
    {
        if (bound.Action is KeyActionId.ResizeMode && _returnMode is AttachClientMode.Resize)
            return LeaveTo(AttachClientMode.Terminal);

        return DispatchCore(bound, fromPrefix: true);
    }

    private List<KeyEngineEvent> DispatchFromTerminal(BoundAction bound) =>
        DispatchCore(bound, fromPrefix: false);

    private List<KeyEngineEvent> DispatchCore(BoundAction bound, bool fromPrefix, bool promptName = true)
    {
        var dest = fromPrefix ? _returnMode : AttachClientMode.Terminal;
        switch (bound.Action)
        {
            case KeyActionId.Detach:
                return EmitDetach();
            case KeyActionId.Help:
                Help.Reset();
                return EnterAfterDispatch(bound, AttachClientMode.KeybindHelp);
            case KeyActionId.WorkspacePicker:
                Navigator.Reset();
                Navigator.WorkspaceOnly = true;
                return EnterAfterDispatch(bound, AttachClientMode.WorkspacePicker);
            case KeyActionId.Goto:
                Navigator.Reset();
                Navigator.WorkspaceOnly = false;
                return EnterAfterDispatch(bound, AttachClientMode.Navigator);
            case KeyActionId.Navigate:
                return EnterAfterDispatch(
                    bound,
                    NarrowLayout ? AttachClientMode.MobileSwitcher : AttachClientMode.Navigate);
            case KeyActionId.ToggleSidebar:
                if (NarrowLayout)
                {
                    if (Mode is AttachClientMode.MobileSwitcher
                        || (fromPrefix && _returnMode is AttachClientMode.MobileSwitcher))
                    {
                        return DispatchAndLeavePrefix(bound, fromPrefix, AttachClientMode.Terminal);
                    }

                    return EnterAfterDispatch(bound, AttachClientMode.MobileSwitcher);
                }

                return DispatchAndLeavePrefix(bound, fromPrefix, dest);
            case KeyActionId.CopyMode:
                return EnterAfterDispatch(bound, AttachClientMode.Copy);
            case KeyActionId.ResizeMode:
                return EnterAfterDispatch(bound, AttachClientMode.Resize);
            case KeyActionId.Settings:
                if (!Settings.IsOpen)
                    Settings.Open();
                return EnterAfterDispatch(bound, AttachClientMode.Settings);
            case KeyActionId.RenameWorkspace:
                _prompt = "";
                return EnterAfterDispatch(bound, AttachClientMode.RenameWorkspace);
            case KeyActionId.RenameTab:
                _prompt = "";
                return EnterAfterDispatch(bound, AttachClientMode.RenameTab);
            case KeyActionId.RenamePane:
                _prompt = "";
                return EnterAfterDispatch(bound, AttachClientMode.RenamePane);
            case KeyActionId.EditScrollback:
                return DispatchEditScrollback(bound, fromPrefix);
            case KeyActionId.AnnotateHandoff:
                return DispatchAnnotateHandoff(bound, fromPrefix);
            case KeyActionId.Command:
                return DispatchCustomCommand(bound, fromPrefix);
            case KeyActionId.ClosePane:
            case KeyActionId.CloseTab:
            case KeyActionId.CloseWorkspace:
                if (_chrome.ConfirmClose)
                    return EnterConfirm(bound, fromPrefix, dest);
                return DispatchAndLeavePrefix(bound, fromPrefix, dest);
            case KeyActionId.NewTab:
                if (promptName && _chrome.PromptNewTabName)
                    return EnterNamePrompt(bound, AttachClientMode.NewTabName, fromPrefix, dest);
                return DispatchAndLeavePrefix(bound, fromPrefix, dest);
            case KeyActionId.NewWorkspace:
                if (_chrome.PromptNewWorkspaceName)
                    return EnterNamePrompt(bound, AttachClientMode.NewWorkspaceName, fromPrefix, dest);
                return DispatchAndLeavePrefix(bound, fromPrefix, dest);
            default:
                return DispatchAndLeavePrefix(bound, fromPrefix, dest);
        }
    }

    private List<KeyEngineEvent> EnterConfirm(
        BoundAction bound,
        bool fromPrefix,
        AttachClientMode dest)
    {
        _pendingClose = bound.Action;
        _confirmAccept = false;
        _prompt = "";
        var events = new List<KeyEngineEvent>();
        if (fromPrefix && Mode is AttachClientMode.Prefix)
            events.AddRange(LeaveTo(AttachClientMode.ConfirmClose));
        if (Mode != AttachClientMode.ConfirmClose)
            events.AddRange(Enter(AttachClientMode.ConfirmClose, pinReturn: dest));
        return events;
    }

    private List<KeyEngineEvent> EnterNamePrompt(
        BoundAction bound,
        AttachClientMode next,
        bool fromPrefix,
        AttachClientMode dest)
    {
        _ = bound;
        ClearPendingClose();
        _prompt = "";
        var events = new List<KeyEngineEvent>();
        if (Mode is AttachClientMode.ConfirmClose)
            events.AddRange(LeaveTo(next));
        else if (fromPrefix && Mode is AttachClientMode.Prefix)
            events.AddRange(LeaveTo(next));
        if (Mode != next)
            events.AddRange(Enter(next, pinReturn: dest));
        return events;
    }

    private List<KeyEngineEvent> DispatchAndLeavePrefix(
        BoundAction bound,
        bool fromPrefix,
        AttachClientMode dest)
    {
        var events = new List<KeyEngineEvent>();
        if (fromPrefix && Mode is AttachClientMode.Prefix)
            events.AddRange(LeaveTo(dest));
        events.AddRange(EmitDispatch(bound.Action, bound.Index));
        return events;
    }

    private List<KeyEngineEvent> DispatchEditScrollback(BoundAction bound, bool fromPrefix)
    {
        var events = new List<KeyEngineEvent>();
        if (Mode is AttachClientMode.Copy)
            events.AddRange(LeaveCopy());
        else if (fromPrefix && Mode is AttachClientMode.Prefix)
            events.AddRange(LeaveTo(AttachClientMode.Terminal));
        events.AddRange(EmitDispatch(bound.Action, bound.Index));
        return events;
    }

    private List<KeyEngineEvent> DispatchAnnotateHandoff(BoundAction bound, bool fromPrefix)
    {
        var prefilled = Copy.ExtractSelection();
        if (SelectionHandoffFiles.IsBlank(prefilled))
            prefilled = null;

        var events = new List<KeyEngineEvent>();
        if (fromPrefix && Mode is AttachClientMode.Prefix)
        {
            var dest = _returnMode is AttachClientMode.Copy or AttachClientMode.Terminal
                or AttachClientMode.Resize or AttachClientMode.Navigate
                ? AttachClientMode.Terminal
                : _returnMode;
            events.AddRange(LeaveTo(dest));
        }

        events.AddRange(EmitDispatch(bound.Action, bound.Index, prefilled));
        return events;
    }

    private List<KeyEngineEvent> DispatchCustomCommand(BoundAction bound, bool fromPrefix)
    {
        var events = new List<KeyEngineEvent>();
        if (Mode is AttachClientMode.Copy)
            events.AddRange(LeaveCopy());
        else if (fromPrefix && Mode is AttachClientMode.Prefix)
        {
            var dest = _returnMode is AttachClientMode.Copy or AttachClientMode.Terminal
                or AttachClientMode.Resize or AttachClientMode.Navigate
                ? AttachClientMode.Terminal
                : _returnMode;
            events.AddRange(LeaveTo(dest));
        }

        events.AddRange(EmitDispatch(bound.Action, bound.Index));
        return events;
    }

    private List<KeyEngineEvent> DispatchOnly(BoundAction bound) =>
        EmitDispatch(bound.Action, bound.Index);

    private List<KeyEngineEvent> EnterAfterDispatch(BoundAction bound, AttachClientMode next)
    {
        var events = new List<KeyEngineEvent>();
        if (Mode is AttachClientMode.Prefix)
            events.AddRange(LeaveTo(next));
        events.AddRange(EmitDispatch(bound.Action, bound.Index));
        if (Mode != next)
            events.AddRange(Enter(next, pinReturn: AttachClientMode.Terminal));
        return events;
    }

    private List<KeyEngineEvent> Enter(AttachClientMode next, AttachClientMode pinReturn)
    {
        ReleaseOpenSettings(next);
        ReleaseOpenOnboarding(next);
        if (next is AttachClientMode.Prefix)
            _returnMode = pinReturn;
        var prev = Mode;
        _mode = next;
        if (next is AttachClientMode.Prefix && pinReturn is AttachClientMode.Copy)
            _paintMode = AttachClientMode.Copy;
        else
            _paintMode = next;
        var events = new List<KeyEngineEvent>();
        if (prev != next)
        {
            if (IsPromptMode(prev)
                || (IsChrome(prev) && !IsChrome(next) && next is not AttachClientMode.Prefix))
            {
                events.Add(new KeyEngineEvent(KeyEngineEventKind.LeaveMode, Mode: prev));
            }

            events.Add(new KeyEngineEvent(KeyEngineEventKind.EnterMode, Mode: next));
        }

        if (next is not AttachClientMode.ConfirmClose)
            ClearPendingClose(preserveAccept: next is AttachClientMode.Terminal && _confirmAccept);

        return events;
    }

    private List<KeyEngineEvent> LeaveTo(AttachClientMode dest)
    {
        var prev = Mode;
        if (prev == dest)
            return [];
        ReleaseOpenSettings(dest);
        ReleaseOpenOnboarding(dest);
        if (prev is AttachClientMode.ConfirmClose && dest is not AttachClientMode.ConfirmClose)
            ClearPendingClose(preserveAccept: _confirmAccept);
        if (prev is AttachClientMode.Prefix
            && _returnMode is AttachClientMode.Copy
            && dest is not AttachClientMode.Copy)
        {
            Copy.Reset();
        }

        _mode = dest;
        _returnMode = AttachClientMode.Terminal;
        var events = new List<KeyEngineEvent>
        {
            new(KeyEngineEventKind.LeaveMode, Mode: prev),
        };
        if (dest is not AttachClientMode.Terminal)
            events.Add(new KeyEngineEvent(KeyEngineEventKind.EnterMode, Mode: dest));
        return events;
    }

    private void ReleaseOpenSettings(AttachClientMode dest)
    {
        if (!Settings.IsOpen)
            return;
        if (dest is AttachClientMode.Settings or AttachClientMode.Prefix)
            return;
        Settings.Cancel();
    }

    private void ReleaseOpenOnboarding(AttachClientMode dest)
    {
        if (!Onboarding.IsOpen)
            return;
        if (dest is AttachClientMode.Onboarding)
            return;
        Onboarding.Close();
    }

    private List<KeyEngineEvent> EmitDispatch(
        KeyActionId action,
        int? index,
        string? prompt = null,
        string? targetId = null)
    {
        var request = new KeyActionRequest(action, index, prompt, targetId);
        _sink?.Handle(request);
        return
        [
            new KeyEngineEvent(
                KeyEngineEventKind.Dispatch,
                Action: action,
                Index: index,
                PromptText: prompt,
                TargetId: targetId),
        ];
    }

    private List<KeyEngineEvent> EmitDetach()
    {
        var request = new KeyActionRequest(KeyActionId.Detach);
        _sink?.Handle(request);
        return
        [
            new KeyEngineEvent(KeyEngineEventKind.Dispatch, Action: KeyActionId.Detach),
            new KeyEngineEvent(KeyEngineEventKind.Detach, Action: KeyActionId.Detach),
        ];
    }

    private static List<KeyEngineEvent> SendBytes(byte[] raw)
    {
        if (raw.Length == 0)
            return [];
        return [new KeyEngineEvent(KeyEngineEventKind.SendPaneBytes, Bytes: raw)];
    }

    private static List<KeyEngineEvent> SendPopupBytes(byte[] raw)
    {
        if (raw.Length == 0)
            return [];
        return
        [
            new KeyEngineEvent(
                KeyEngineEventKind.SendPopupBytes,
                Bytes: raw,
                TargetId: "popup"),
        ];
    }

    private static bool TryPrintable(KeyChord chord, out string text)
    {
        text = "";
        if (chord.Ctrl || chord.Alt || chord.Prefix)
            return false;
        if (chord.Key is "esc" or "enter" or "tab" or "backspace" or "up" or "down" or "left" or "right"
            or "delete" or "home" or "end" or "pageup" or "pagedown" or "insert" or "csi"
            or "f1" or "f2" or "f3" or "f4")
        {
            return false;
        }

        if (chord.Key.Length > 1 && chord.Key is not (
            "space" or "minus" or "plus" or "comma" or "period" or "slash"))
        {
            return false;
        }

        text = chord.Key switch
        {
            "space" => " ",
            "minus" => "-",
            "plus" => "+",
            "comma" => ",",
            "period" => ".",
            "slash" => "/",
            _ when chord.Shift && chord.Key.Length == 1 && char.IsAsciiLetter(chord.Key[0]) =>
                char.ToUpperInvariant(chord.Key[0]).ToString(),
            _ => chord.Key,
        };
        return text.Length > 0;
    }

    internal static bool IsChrome(AttachClientMode mode) =>
        mode is AttachClientMode.Prefix
            or AttachClientMode.Navigate
            or AttachClientMode.Copy
            or AttachClientMode.Resize;

    internal static bool SuppressesLiveRemap(AttachClientMode mode) =>
        mode is AttachClientMode.KeybindHelp
            or AttachClientMode.Copy
            or AttachClientMode.ContextMenu
            or AttachClientMode.Navigator
            or AttachClientMode.WorkspacePicker
            or AttachClientMode.TransferPicker
            or AttachClientMode.GlobalMenu
            or AttachClientMode.WhatsNew
            or AttachClientMode.Settings
            or AttachClientMode.Onboarding
            or AttachClientMode.MobileSwitcher;

    internal static bool IsPromptMode(AttachClientMode mode) =>
        mode is AttachClientMode.RenameWorkspace
            or AttachClientMode.RenameTab
            or AttachClientMode.RenamePane
            or AttachClientMode.NewTabName
            or AttachClientMode.NewWorkspaceName
            or AttachClientMode.ConfirmClose
            or AttachClientMode.ConfirmMoveWork
            or AttachClientMode.ContextMenu;

    internal static bool HoldsPromptTarget(AttachClientMode mode) =>
        mode is AttachClientMode.RenameWorkspace
            or AttachClientMode.RenameTab
            or AttachClientMode.RenamePane
            or AttachClientMode.NewTabName
            or AttachClientMode.NewWorkspaceName
            or AttachClientMode.ConfirmClose
            or AttachClientMode.ConfirmMoveWork;

    internal static bool IsDismissibleOverlay(AttachClientMode mode) =>
        mode is AttachClientMode.Copy
            or AttachClientMode.KeybindHelp
            or AttachClientMode.Resize
            or AttachClientMode.Prefix
            or AttachClientMode.Navigate
            or AttachClientMode.Navigator
            or AttachClientMode.WorkspacePicker
            or AttachClientMode.TransferPicker
            or AttachClientMode.GlobalMenu
            or AttachClientMode.WhatsNew
            or AttachClientMode.Settings
            or AttachClientMode.MobileSwitcher;

    internal static bool IsConfirmAcceptChord(KeyChord chord) =>
        chord.IsEnter || chord is { Key: "y", Ctrl: false, Alt: false };

    private bool TryDropLeftoverConfirmAccept(KeyChord chord)
    {
        if (!_dropLeftoverConfirmAccept)
            return false;
        _dropLeftoverConfirmAccept = false;
        return IsConfirmAcceptChord(chord);
    }

    internal static bool IsCloseAction(KeyActionId action) =>
        action is KeyActionId.ClosePane or KeyActionId.CloseTab or KeyActionId.CloseWorkspace;

    private string? CloseTargetFor(KeyActionId action) =>
        action switch
        {
            KeyActionId.ClosePane => _pendingClosePaneTarget,
            KeyActionId.CloseTab => _pendingCloseTabTarget,
            KeyActionId.CloseWorkspace => _pendingCloseWorkspaceTarget,
            _ => null,
        };

    private void ClearPendingClose(bool preserveAccept = false)
    {
        _pendingClose = null;
        if (!preserveAccept)
        {
            _confirmAccept = false;
            ClearBoundCloseTargets();
        }
    }
}
