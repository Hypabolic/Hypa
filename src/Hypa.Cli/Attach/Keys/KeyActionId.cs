namespace Hypa.Cli.Attach.Keys;

/// <summary>Closed set of <c>keys.*</c> action names.</summary>
public enum KeyActionId
{
    Help = 1,
    Settings,
    NewWorkspace,
    NewWorktree,
    OpenWorktree,
    RemoveWorktree,
    RenameWorkspace,
    CloseWorkspace,
    WorkspacePicker,
    Goto,
    Navigate,
    NavigateWorkspaceUp,
    NavigateWorkspaceDown,
    NavigatePaneLeft,
    NavigatePaneDown,
    NavigatePaneUp,
    NavigatePaneRight,
    Detach,
    ReloadConfig,
    OpenNotificationTarget,
    PreviousWorkspace,
    NextWorkspace,
    PreviousAgent,
    NextAgent,
    FocusAgent,
    RemoteImagePaste,
    NewTab,
    RenameTab,
    PreviousTab,
    NextTab,
    MoveTabPrevious,
    MoveTabNext,
    SwitchTab,
    SwitchWorkspace,
    CloseTab,
    RenamePane,
    EditScrollback,
    AnnotateHandoff,
    CopyMode,
    FocusPaneLeft,
    FocusPaneDown,
    FocusPaneUp,
    FocusPaneRight,
    SwapPaneLeft,
    SwapPaneDown,
    SwapPaneUp,
    SwapPaneRight,
    CyclePaneNext,
    CyclePanePrevious,
    LastPane,
    SplitVertical,
    SplitHorizontal,
    ClosePane,
    Zoom,
    ResizeMode,
    ResizePaneLeft,
    ResizePaneDown,
    ResizePaneUp,
    ResizePaneRight,
    ToggleSidebar,
    IndexedTabs,
    IndexedWorkspaces,
    IndexedAgents,
    Command,
    Prefix,
}

public static class KeyActionNames
{
    public const string Help = "help";
    public const string Settings = "settings";
    public const string NewWorkspace = "new_workspace";
    public const string NewWorktree = "new_worktree";
    public const string OpenWorktree = "open_worktree";
    public const string RemoveWorktree = "remove_worktree";
    public const string RenameWorkspace = "rename_workspace";
    public const string CloseWorkspace = "close_workspace";
    public const string WorkspacePicker = "workspace_picker";
    public const string Goto = "goto";
    public const string Navigate = "navigate";
    public const string NavigateWorkspaceUp = "navigate_workspace_up";
    public const string NavigateWorkspaceDown = "navigate_workspace_down";
    public const string NavigatePaneLeft = "navigate_pane_left";
    public const string NavigatePaneDown = "navigate_pane_down";
    public const string NavigatePaneUp = "navigate_pane_up";
    public const string NavigatePaneRight = "navigate_pane_right";
    public const string Detach = "detach";
    public const string ReloadConfig = "reload_config";
    public const string OpenNotificationTarget = "open_notification_target";
    public const string PreviousWorkspace = "previous_workspace";
    public const string NextWorkspace = "next_workspace";
    public const string PreviousAgent = "previous_agent";
    public const string NextAgent = "next_agent";
    public const string FocusAgent = "focus_agent";
    public const string RemoteImagePaste = "remote_image_paste";
    public const string NewTab = "new_tab";
    public const string RenameTab = "rename_tab";
    public const string PreviousTab = "previous_tab";
    public const string NextTab = "next_tab";
    public const string MoveTabPrevious = "move_tab_previous";
    public const string MoveTabNext = "move_tab_next";
    public const string SwitchTab = "switch_tab";
    public const string SwitchWorkspace = "switch_workspace";
    public const string CloseTab = "close_tab";
    public const string RenamePane = "rename_pane";
    public const string EditScrollback = "edit_scrollback";
    public const string AnnotateHandoff = "annotate_handoff";
    public const string CopyMode = "copy_mode";
    public const string FocusPaneLeft = "focus_pane_left";
    public const string FocusPaneDown = "focus_pane_down";
    public const string FocusPaneUp = "focus_pane_up";
    public const string FocusPaneRight = "focus_pane_right";
    public const string SwapPaneLeft = "swap_pane_left";
    public const string SwapPaneDown = "swap_pane_down";
    public const string SwapPaneUp = "swap_pane_up";
    public const string SwapPaneRight = "swap_pane_right";
    public const string CyclePaneNext = "cycle_pane_next";
    public const string CyclePanePrevious = "cycle_pane_previous";
    public const string LastPane = "last_pane";
    public const string SplitVertical = "split_vertical";
    public const string SplitHorizontal = "split_horizontal";
    public const string ClosePane = "close_pane";
    public const string Zoom = "zoom";
    public const string Fullscreen = "fullscreen";
    public const string ResizeMode = "resize_mode";
    public const string ResizePaneLeft = "resize_pane_left";
    public const string ResizePaneDown = "resize_pane_down";
    public const string ResizePaneUp = "resize_pane_up";
    public const string ResizePaneRight = "resize_pane_right";
    public const string ToggleSidebar = "toggle_sidebar";
    public const string IndexedTabs = "indexed.tabs";
    public const string IndexedWorkspaces = "indexed.workspaces";
    public const string IndexedAgents = "indexed.agents";
    public const string Command = "command";
    public const string Prefix = "prefix";

    public static string WireName(this KeyActionId id) => id switch
    {
        KeyActionId.Help => Help,
        KeyActionId.Settings => Settings,
        KeyActionId.NewWorkspace => NewWorkspace,
        KeyActionId.NewWorktree => NewWorktree,
        KeyActionId.OpenWorktree => OpenWorktree,
        KeyActionId.RemoveWorktree => RemoveWorktree,
        KeyActionId.RenameWorkspace => RenameWorkspace,
        KeyActionId.CloseWorkspace => CloseWorkspace,
        KeyActionId.WorkspacePicker => WorkspacePicker,
        KeyActionId.Goto => Goto,
        KeyActionId.Navigate => Navigate,
        KeyActionId.NavigateWorkspaceUp => NavigateWorkspaceUp,
        KeyActionId.NavigateWorkspaceDown => NavigateWorkspaceDown,
        KeyActionId.NavigatePaneLeft => NavigatePaneLeft,
        KeyActionId.NavigatePaneDown => NavigatePaneDown,
        KeyActionId.NavigatePaneUp => NavigatePaneUp,
        KeyActionId.NavigatePaneRight => NavigatePaneRight,
        KeyActionId.Detach => Detach,
        KeyActionId.ReloadConfig => ReloadConfig,
        KeyActionId.OpenNotificationTarget => OpenNotificationTarget,
        KeyActionId.PreviousWorkspace => PreviousWorkspace,
        KeyActionId.NextWorkspace => NextWorkspace,
        KeyActionId.PreviousAgent => PreviousAgent,
        KeyActionId.NextAgent => NextAgent,
        KeyActionId.FocusAgent => FocusAgent,
        KeyActionId.RemoteImagePaste => RemoteImagePaste,
        KeyActionId.NewTab => NewTab,
        KeyActionId.RenameTab => RenameTab,
        KeyActionId.PreviousTab => PreviousTab,
        KeyActionId.NextTab => NextTab,
        KeyActionId.MoveTabPrevious => MoveTabPrevious,
        KeyActionId.MoveTabNext => MoveTabNext,
        KeyActionId.SwitchTab => SwitchTab,
        KeyActionId.SwitchWorkspace => SwitchWorkspace,
        KeyActionId.CloseTab => CloseTab,
        KeyActionId.RenamePane => RenamePane,
        KeyActionId.EditScrollback => EditScrollback,
        KeyActionId.AnnotateHandoff => AnnotateHandoff,
        KeyActionId.CopyMode => CopyMode,
        KeyActionId.FocusPaneLeft => FocusPaneLeft,
        KeyActionId.FocusPaneDown => FocusPaneDown,
        KeyActionId.FocusPaneUp => FocusPaneUp,
        KeyActionId.FocusPaneRight => FocusPaneRight,
        KeyActionId.SwapPaneLeft => SwapPaneLeft,
        KeyActionId.SwapPaneDown => SwapPaneDown,
        KeyActionId.SwapPaneUp => SwapPaneUp,
        KeyActionId.SwapPaneRight => SwapPaneRight,
        KeyActionId.CyclePaneNext => CyclePaneNext,
        KeyActionId.CyclePanePrevious => CyclePanePrevious,
        KeyActionId.LastPane => LastPane,
        KeyActionId.SplitVertical => SplitVertical,
        KeyActionId.SplitHorizontal => SplitHorizontal,
        KeyActionId.ClosePane => ClosePane,
        KeyActionId.Zoom => Zoom,
        KeyActionId.ResizeMode => ResizeMode,
        KeyActionId.ResizePaneLeft => ResizePaneLeft,
        KeyActionId.ResizePaneDown => ResizePaneDown,
        KeyActionId.ResizePaneUp => ResizePaneUp,
        KeyActionId.ResizePaneRight => ResizePaneRight,
        KeyActionId.ToggleSidebar => ToggleSidebar,
        KeyActionId.IndexedTabs => IndexedTabs,
        KeyActionId.IndexedWorkspaces => IndexedWorkspaces,
        KeyActionId.IndexedAgents => IndexedAgents,
        KeyActionId.Command => Command,
        KeyActionId.Prefix => Prefix,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "unknown key action"),
    };

    public static bool TryParse(string? name, out KeyActionId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var key = name.Trim();
        if (key.StartsWith("keys.", StringComparison.Ordinal))
            key = key[5..];

        id = key switch
        {
            Help => KeyActionId.Help,
            Settings => KeyActionId.Settings,
            NewWorkspace => KeyActionId.NewWorkspace,
            NewWorktree => KeyActionId.NewWorktree,
            OpenWorktree => KeyActionId.OpenWorktree,
            RemoveWorktree => KeyActionId.RemoveWorktree,
            RenameWorkspace => KeyActionId.RenameWorkspace,
            CloseWorkspace => KeyActionId.CloseWorkspace,
            WorkspacePicker => KeyActionId.WorkspacePicker,
            Goto => KeyActionId.Goto,
            Navigate => KeyActionId.Navigate,
            NavigateWorkspaceUp => KeyActionId.NavigateWorkspaceUp,
            NavigateWorkspaceDown => KeyActionId.NavigateWorkspaceDown,
            NavigatePaneLeft => KeyActionId.NavigatePaneLeft,
            NavigatePaneDown => KeyActionId.NavigatePaneDown,
            NavigatePaneUp => KeyActionId.NavigatePaneUp,
            NavigatePaneRight => KeyActionId.NavigatePaneRight,
            Detach => KeyActionId.Detach,
            ReloadConfig => KeyActionId.ReloadConfig,
            OpenNotificationTarget => KeyActionId.OpenNotificationTarget,
            PreviousWorkspace => KeyActionId.PreviousWorkspace,
            NextWorkspace => KeyActionId.NextWorkspace,
            PreviousAgent => KeyActionId.PreviousAgent,
            NextAgent => KeyActionId.NextAgent,
            FocusAgent => KeyActionId.FocusAgent,
            RemoteImagePaste => KeyActionId.RemoteImagePaste,
            NewTab => KeyActionId.NewTab,
            RenameTab => KeyActionId.RenameTab,
            PreviousTab => KeyActionId.PreviousTab,
            NextTab => KeyActionId.NextTab,
            MoveTabPrevious => KeyActionId.MoveTabPrevious,
            MoveTabNext => KeyActionId.MoveTabNext,
            SwitchTab => KeyActionId.SwitchTab,
            SwitchWorkspace => KeyActionId.SwitchWorkspace,
            CloseTab => KeyActionId.CloseTab,
            RenamePane => KeyActionId.RenamePane,
            EditScrollback => KeyActionId.EditScrollback,
            AnnotateHandoff => KeyActionId.AnnotateHandoff,
            CopyMode => KeyActionId.CopyMode,
            FocusPaneLeft => KeyActionId.FocusPaneLeft,
            FocusPaneDown => KeyActionId.FocusPaneDown,
            FocusPaneUp => KeyActionId.FocusPaneUp,
            FocusPaneRight => KeyActionId.FocusPaneRight,
            SwapPaneLeft => KeyActionId.SwapPaneLeft,
            SwapPaneDown => KeyActionId.SwapPaneDown,
            SwapPaneUp => KeyActionId.SwapPaneUp,
            SwapPaneRight => KeyActionId.SwapPaneRight,
            CyclePaneNext => KeyActionId.CyclePaneNext,
            CyclePanePrevious => KeyActionId.CyclePanePrevious,
            LastPane => KeyActionId.LastPane,
            SplitVertical => KeyActionId.SplitVertical,
            SplitHorizontal => KeyActionId.SplitHorizontal,
            ClosePane => KeyActionId.ClosePane,
            Zoom or Fullscreen => KeyActionId.Zoom,
            ResizeMode => KeyActionId.ResizeMode,
            ResizePaneLeft => KeyActionId.ResizePaneLeft,
            ResizePaneDown => KeyActionId.ResizePaneDown,
            ResizePaneUp => KeyActionId.ResizePaneUp,
            ResizePaneRight => KeyActionId.ResizePaneRight,
            ToggleSidebar => KeyActionId.ToggleSidebar,
            IndexedTabs => KeyActionId.IndexedTabs,
            IndexedWorkspaces => KeyActionId.IndexedWorkspaces,
            IndexedAgents => KeyActionId.IndexedAgents,
            Command or "[[keys.command]]" => KeyActionId.Command,
            Prefix => KeyActionId.Prefix,
            _ => default,
        };
        return id != default;
    }

    public static bool IsNavigate(KeyActionId id) =>
        id is KeyActionId.NavigateWorkspaceUp
            or KeyActionId.NavigateWorkspaceDown
            or KeyActionId.NavigatePaneLeft
            or KeyActionId.NavigatePaneDown
            or KeyActionId.NavigatePaneUp
            or KeyActionId.NavigatePaneRight;

    public static bool IsRemoteOnly(KeyActionId id) => id is KeyActionId.RemoteImagePaste;
}
