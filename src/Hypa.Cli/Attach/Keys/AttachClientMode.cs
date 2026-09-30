namespace Hypa.Cli.Attach.Keys;

public enum AttachClientMode
{
    Terminal = 0,
    Prefix,
    Navigate,
    Copy,
    Resize,
    KeybindHelp,
    Settings,
    Onboarding,
    RenameWorkspace,
    RenameTab,
    RenamePane,
    ConfirmClose,
    ConfirmMoveWork,
    NewTabName,
    NewWorkspaceName,
    ContextMenu,
    Navigator,
    WorkspacePicker,
    TransferPicker,
    GlobalMenu,
    WhatsNew,
    MobileSwitcher,
}

public enum ModeBarSlot
{
    Top = 0,
    Bottom = 1,
}

public enum KeyEngineEventKind
{
    SendPaneBytes = 0,
    SendPopupBytes,
    Dispatch,
    EnterMode,
    LeaveMode,
    Detach,
    HelpFilter,
    PromptEdit,
    CopyChanged,
    CopyYank,
    MenuMove,
    MenuApply,
    HideOverlay,
}

public sealed record KeyEngineEvent(
    KeyEngineEventKind Kind,
    KeyActionId? Action = null,
    int? Index = null,
    byte[]? Bytes = null,
    AttachClientMode? Mode = null,
    string? Filter = null,
    string? PromptText = null,
    string? TargetId = null)
{
    public KeyActionRequest? ToRequest() =>
        Kind is KeyEngineEventKind.Dispatch or KeyEngineEventKind.Detach && Action is { } action
            ? new KeyActionRequest(action, Index, PromptText, TargetId)
            : null;
}

public sealed record KeyActionRequest(
    KeyActionId Action,
    int? Index = null,
    string? PromptText = null,
    string? TargetId = null);

public sealed record KeyCompileError(string Code, string Message)
{
    public const string NavigateForbidden = "navigate_forbidden_chord";
    public const string DuplicateBinding = "duplicate_binding";
    public const string InvalidChord = "invalid_chord";
    public const string MissingPrefix = "missing_prefix";

    public override string ToString() => $"{Code}: {Message}";
}

public sealed record KeyBindingRow(
    KeyActionId Action,
    KeyChord Chord,
    AttachClientMode Mode,
    int? Index,
    string Spec);

public readonly record struct BoundAction(KeyActionId Action, int? Index);
