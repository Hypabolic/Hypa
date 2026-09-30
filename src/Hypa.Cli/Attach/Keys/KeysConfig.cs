using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.Cli.Attach.Keys;

/// <summary>One field per <c>keys.*</c> action. <see cref="Default"/> matches <c>keys.json</c>.</summary>
public sealed record KeysConfig
{
    public BindingSpec Prefix { get; init; } = BindingSpec.Unset;
    public BindingSpec Help { get; init; } = BindingSpec.Unset;
    public BindingSpec Settings { get; init; } = BindingSpec.Unset;
    public BindingSpec NewWorkspace { get; init; } = BindingSpec.Unset;
    public BindingSpec NewWorktree { get; init; } = BindingSpec.Unset;
    public BindingSpec OpenWorktree { get; init; } = BindingSpec.Unset;
    public BindingSpec RemoveWorktree { get; init; } = BindingSpec.Unset;
    public BindingSpec RenameWorkspace { get; init; } = BindingSpec.Unset;
    public BindingSpec CloseWorkspace { get; init; } = BindingSpec.Unset;
    public BindingSpec WorkspacePicker { get; init; } = BindingSpec.Unset;
    public BindingSpec Goto { get; init; } = BindingSpec.Unset;
    public BindingSpec Navigate { get; init; } = BindingSpec.Unset;
    public BindingSpec NavigateWorkspaceUp { get; init; } = BindingSpec.Unset;
    public BindingSpec NavigateWorkspaceDown { get; init; } = BindingSpec.Unset;
    public BindingSpec NavigatePaneLeft { get; init; } = BindingSpec.Unset;
    public BindingSpec NavigatePaneDown { get; init; } = BindingSpec.Unset;
    public BindingSpec NavigatePaneUp { get; init; } = BindingSpec.Unset;
    public BindingSpec NavigatePaneRight { get; init; } = BindingSpec.Unset;
    public BindingSpec Detach { get; init; } = BindingSpec.Unset;
    public BindingSpec ReloadConfig { get; init; } = BindingSpec.Unset;
    public BindingSpec OpenNotificationTarget { get; init; } = BindingSpec.Unset;
    public BindingSpec PreviousWorkspace { get; init; } = BindingSpec.Unset;
    public BindingSpec NextWorkspace { get; init; } = BindingSpec.Unset;
    public BindingSpec PreviousAgent { get; init; } = BindingSpec.Unset;
    public BindingSpec NextAgent { get; init; } = BindingSpec.Unset;
    public BindingSpec FocusAgent { get; init; } = BindingSpec.Unset;
    public BindingSpec RemoteImagePaste { get; init; } = BindingSpec.Unset;
    public BindingSpec NewTab { get; init; } = BindingSpec.Unset;
    public BindingSpec RenameTab { get; init; } = BindingSpec.Unset;
    public BindingSpec PreviousTab { get; init; } = BindingSpec.Unset;
    public BindingSpec NextTab { get; init; } = BindingSpec.Unset;
    public BindingSpec MoveTabPrevious { get; init; } = BindingSpec.Unset;
    public BindingSpec MoveTabNext { get; init; } = BindingSpec.Unset;
    public BindingSpec SwitchTab { get; init; } = BindingSpec.Unset;
    public BindingSpec SwitchWorkspace { get; init; } = BindingSpec.Unset;
    public BindingSpec CloseTab { get; init; } = BindingSpec.Unset;
    public BindingSpec RenamePane { get; init; } = BindingSpec.Unset;
    public BindingSpec EditScrollback { get; init; } = BindingSpec.Unset;
    public BindingSpec AnnotateHandoff { get; init; } = BindingSpec.Unset;
    public BindingSpec CopyMode { get; init; } = BindingSpec.Unset;
    public BindingSpec FocusPaneLeft { get; init; } = BindingSpec.Unset;
    public BindingSpec FocusPaneDown { get; init; } = BindingSpec.Unset;
    public BindingSpec FocusPaneUp { get; init; } = BindingSpec.Unset;
    public BindingSpec FocusPaneRight { get; init; } = BindingSpec.Unset;
    public BindingSpec SwapPaneLeft { get; init; } = BindingSpec.Unset;
    public BindingSpec SwapPaneDown { get; init; } = BindingSpec.Unset;
    public BindingSpec SwapPaneUp { get; init; } = BindingSpec.Unset;
    public BindingSpec SwapPaneRight { get; init; } = BindingSpec.Unset;
    public BindingSpec CyclePaneNext { get; init; } = BindingSpec.Unset;
    public BindingSpec CyclePanePrevious { get; init; } = BindingSpec.Unset;
    public BindingSpec LastPane { get; init; } = BindingSpec.Unset;
    public BindingSpec SplitVertical { get; init; } = BindingSpec.Unset;
    public BindingSpec SplitHorizontal { get; init; } = BindingSpec.Unset;
    public BindingSpec ClosePane { get; init; } = BindingSpec.Unset;
    public BindingSpec Zoom { get; init; } = BindingSpec.Unset;
    public BindingSpec Fullscreen { get; init; } = BindingSpec.Unset;
    public BindingSpec ResizeMode { get; init; } = BindingSpec.Unset;
    public BindingSpec ResizePaneLeft { get; init; } = BindingSpec.Unset;
    public BindingSpec ResizePaneDown { get; init; } = BindingSpec.Unset;
    public BindingSpec ResizePaneUp { get; init; } = BindingSpec.Unset;
    public BindingSpec ResizePaneRight { get; init; } = BindingSpec.Unset;
    public BindingSpec ToggleSidebar { get; init; } = BindingSpec.Unset;
    public BindingSpec IndexedTabs { get; init; } = BindingSpec.Unset;
    public BindingSpec IndexedWorkspaces { get; init; } = BindingSpec.Unset;
    public BindingSpec IndexedAgents { get; init; } = BindingSpec.Unset;
    public IReadOnlyList<KeyCommandBinding> Commands { get; init; } = [];

    public static KeysConfig Default() => new()
    {
        Prefix = "ctrl+b",
        Help = "prefix+?",
        Settings = "prefix+s",
        NewWorkspace = "prefix+shift+n",
        NewWorktree = "prefix+shift+g",
        OpenWorktree = BindingSpec.Unset,
        RemoveWorktree = BindingSpec.Unset,
        RenameWorkspace = "prefix+shift+w",
        CloseWorkspace = "prefix+shift+d",
        WorkspacePicker = "prefix+w",
        Goto = "prefix+g",
        Navigate = "prefix+space",
        NavigateWorkspaceUp = "up",
        NavigateWorkspaceDown = "down",
        NavigatePaneLeft = "h",
        NavigatePaneDown = "j",
        NavigatePaneUp = "k",
        NavigatePaneRight = "l",
        Detach = "prefix+q",
        ReloadConfig = "prefix+shift+r",
        OpenNotificationTarget = "prefix+o",
        PreviousWorkspace = BindingSpec.Unset,
        NextWorkspace = BindingSpec.Unset,
        PreviousAgent = BindingSpec.Unset,
        NextAgent = BindingSpec.Unset,
        FocusAgent = BindingSpec.Unset,
        RemoteImagePaste = "ctrl+v",
        NewTab = "prefix+c",
        RenameTab = "prefix+shift+t",
        PreviousTab = "prefix+p",
        NextTab = "prefix+n",
        MoveTabPrevious = BindingSpec.Unset,
        MoveTabNext = BindingSpec.Unset,
        SwitchTab = "prefix+1..9",
        SwitchWorkspace = BindingSpec.Unset,
        CloseTab = "prefix+shift+x",
        RenamePane = "prefix+shift+p",
        EditScrollback = "prefix+e",
        AnnotateHandoff = BindingSpec.Unset,
        CopyMode = "prefix+[",
        FocusPaneLeft = "prefix+h",
        FocusPaneDown = "prefix+j",
        FocusPaneUp = "prefix+k",
        FocusPaneRight = "prefix+l",
        SwapPaneLeft = "prefix+shift+h",
        SwapPaneDown = "prefix+shift+j",
        SwapPaneUp = "prefix+shift+k",
        SwapPaneRight = "prefix+shift+l",
        CyclePaneNext = "prefix+tab",
        CyclePanePrevious = "prefix+shift+tab",
        LastPane = BindingSpec.Unset,
        SplitVertical = "prefix+v",
        SplitHorizontal = "prefix+minus",
        ClosePane = "prefix+x",
        Zoom = "prefix+z",
        Fullscreen = BindingSpec.Unset,
        ResizeMode = "prefix+r",
        ResizePaneLeft = BindingSpec.Unset,
        ResizePaneDown = BindingSpec.Unset,
        ResizePaneUp = BindingSpec.Unset,
        ResizePaneRight = BindingSpec.Unset,
        ToggleSidebar = "prefix+b",
        IndexedTabs = BindingSpec.Unset,
        IndexedWorkspaces = BindingSpec.Unset,
        IndexedAgents = BindingSpec.Unset,
        Commands = [],
    };

    /// <summary>
    /// Overlay the prefix-free family on this config. Does not change <see cref="Default"/>.
    /// </summary>
    public KeysConfig WithPrefixFreeFamily() => this with
    {
        FocusPaneLeft = BindingSpec.From("prefix+h", "ctrl+alt+h"),
        FocusPaneDown = BindingSpec.From("prefix+j", "ctrl+alt+j"),
        FocusPaneUp = BindingSpec.From("prefix+k", "ctrl+alt+k"),
        FocusPaneRight = BindingSpec.From("prefix+l", "ctrl+alt+l"),
        PreviousTab = BindingSpec.From("prefix+p", "ctrl+alt+["),
        NextTab = BindingSpec.From("prefix+n", "ctrl+alt+]"),
        NewTab = BindingSpec.From("prefix+c", "ctrl+alt+c"),
        SplitVertical = BindingSpec.From("prefix+v", "ctrl+alt+d"),
        SplitHorizontal = BindingSpec.From("prefix+minus", "ctrl+alt+shift+d"),
        Zoom = BindingSpec.From("prefix+z", "ctrl+alt+z"),
    };

    internal BindingSpec ResolvedZoom =>
        !Zoom.IsUnset ? Zoom : Fullscreen;

    internal IEnumerable<(KeyActionId Action, BindingSpec Spec)> EnumerateActions()
    {
        yield return (KeyActionId.Prefix, Prefix);
        yield return (KeyActionId.Help, Help);
        yield return (KeyActionId.Settings, Settings);
        yield return (KeyActionId.NewWorkspace, NewWorkspace);
        yield return (KeyActionId.NewWorktree, NewWorktree);
        yield return (KeyActionId.OpenWorktree, OpenWorktree);
        yield return (KeyActionId.RemoveWorktree, RemoveWorktree);
        yield return (KeyActionId.RenameWorkspace, RenameWorkspace);
        yield return (KeyActionId.CloseWorkspace, CloseWorkspace);
        yield return (KeyActionId.WorkspacePicker, WorkspacePicker);
        yield return (KeyActionId.Goto, Goto);
        yield return (KeyActionId.Navigate, Navigate);
        yield return (KeyActionId.NavigateWorkspaceUp, NavigateWorkspaceUp);
        yield return (KeyActionId.NavigateWorkspaceDown, NavigateWorkspaceDown);
        yield return (KeyActionId.NavigatePaneLeft, NavigatePaneLeft);
        yield return (KeyActionId.NavigatePaneDown, NavigatePaneDown);
        yield return (KeyActionId.NavigatePaneUp, NavigatePaneUp);
        yield return (KeyActionId.NavigatePaneRight, NavigatePaneRight);
        yield return (KeyActionId.Detach, Detach);
        yield return (KeyActionId.ReloadConfig, ReloadConfig);
        yield return (KeyActionId.OpenNotificationTarget, OpenNotificationTarget);
        yield return (KeyActionId.PreviousWorkspace, PreviousWorkspace);
        yield return (KeyActionId.NextWorkspace, NextWorkspace);
        yield return (KeyActionId.PreviousAgent, PreviousAgent);
        yield return (KeyActionId.NextAgent, NextAgent);
        yield return (KeyActionId.FocusAgent, FocusAgent);
        yield return (KeyActionId.RemoteImagePaste, RemoteImagePaste);
        yield return (KeyActionId.NewTab, NewTab);
        yield return (KeyActionId.RenameTab, RenameTab);
        yield return (KeyActionId.PreviousTab, PreviousTab);
        yield return (KeyActionId.NextTab, NextTab);
        yield return (KeyActionId.MoveTabPrevious, MoveTabPrevious);
        yield return (KeyActionId.MoveTabNext, MoveTabNext);
        yield return (KeyActionId.SwitchTab, SwitchTab);
        yield return (KeyActionId.SwitchWorkspace, SwitchWorkspace);
        yield return (KeyActionId.CloseTab, CloseTab);
        yield return (KeyActionId.RenamePane, RenamePane);
        yield return (KeyActionId.EditScrollback, EditScrollback);
        yield return (KeyActionId.AnnotateHandoff, AnnotateHandoff);
        yield return (KeyActionId.CopyMode, CopyMode);
        yield return (KeyActionId.FocusPaneLeft, FocusPaneLeft);
        yield return (KeyActionId.FocusPaneDown, FocusPaneDown);
        yield return (KeyActionId.FocusPaneUp, FocusPaneUp);
        yield return (KeyActionId.FocusPaneRight, FocusPaneRight);
        yield return (KeyActionId.SwapPaneLeft, SwapPaneLeft);
        yield return (KeyActionId.SwapPaneDown, SwapPaneDown);
        yield return (KeyActionId.SwapPaneUp, SwapPaneUp);
        yield return (KeyActionId.SwapPaneRight, SwapPaneRight);
        yield return (KeyActionId.CyclePaneNext, CyclePaneNext);
        yield return (KeyActionId.CyclePanePrevious, CyclePanePrevious);
        yield return (KeyActionId.LastPane, LastPane);
        yield return (KeyActionId.SplitVertical, SplitVertical);
        yield return (KeyActionId.SplitHorizontal, SplitHorizontal);
        yield return (KeyActionId.ClosePane, ClosePane);
        yield return (KeyActionId.Zoom, ResolvedZoom);
        yield return (KeyActionId.ResizeMode, ResizeMode);
        yield return (KeyActionId.ResizePaneLeft, ResizePaneLeft);
        yield return (KeyActionId.ResizePaneDown, ResizePaneDown);
        yield return (KeyActionId.ResizePaneUp, ResizePaneUp);
        yield return (KeyActionId.ResizePaneRight, ResizePaneRight);
        yield return (KeyActionId.ToggleSidebar, ToggleSidebar);
        yield return (KeyActionId.IndexedTabs, IndexedTabs);
        yield return (KeyActionId.IndexedWorkspaces, IndexedWorkspaces);
        yield return (KeyActionId.IndexedAgents, IndexedAgents);
    }
}

public sealed record KeyCommandBinding(
    BindingSpec Key,
    string Command,
    string Type = "shell",
    string? Description = null,
    AttachPopupSize? Width = null,
    AttachPopupSize? Height = null);
