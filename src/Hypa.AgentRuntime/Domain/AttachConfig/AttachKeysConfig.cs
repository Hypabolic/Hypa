namespace Hypa.AgentRuntime.Domain.AttachConfig;

/// <summary>Raw key specs from TOML. Cli maps these onto <c>KeysConfig</c>.</summary>
public sealed record AttachKeysConfig
{
    public AttachBindingSpec Prefix { get; init; } = "ctrl+b";
    public AttachBindingSpec Help { get; init; } = "prefix+?";
    public AttachBindingSpec Settings { get; init; } = "prefix+s";
    public AttachBindingSpec NewWorkspace { get; init; } = "prefix+shift+n";
    public AttachBindingSpec NewWorktree { get; init; } = "prefix+shift+g";
    public AttachBindingSpec OpenWorktree { get; init; } = "unset";
    public AttachBindingSpec RemoveWorktree { get; init; } = "unset";
    public AttachBindingSpec RenameWorkspace { get; init; } = "prefix+shift+w";
    public AttachBindingSpec CloseWorkspace { get; init; } = "prefix+shift+d";
    public AttachBindingSpec WorkspacePicker { get; init; } = "prefix+w";
    public AttachBindingSpec Goto { get; init; } = "prefix+g";
    public AttachBindingSpec Navigate { get; init; } = "prefix+space";
    public AttachBindingSpec NavigateWorkspaceUp { get; init; } = "up";
    public AttachBindingSpec NavigateWorkspaceDown { get; init; } = "down";
    public AttachBindingSpec NavigatePaneLeft { get; init; } = "h";
    public AttachBindingSpec NavigatePaneDown { get; init; } = "j";
    public AttachBindingSpec NavigatePaneUp { get; init; } = "k";
    public AttachBindingSpec NavigatePaneRight { get; init; } = "l";
    public AttachBindingSpec Detach { get; init; } = "prefix+q";
    public AttachBindingSpec ReloadConfig { get; init; } = "prefix+shift+r";
    public AttachBindingSpec OpenNotificationTarget { get; init; } = "prefix+o";
    public AttachBindingSpec PreviousWorkspace { get; init; } = "unset";
    public AttachBindingSpec NextWorkspace { get; init; } = "unset";
    public AttachBindingSpec PreviousAgent { get; init; } = "unset";
    public AttachBindingSpec NextAgent { get; init; } = "unset";
    public AttachBindingSpec FocusAgent { get; init; } = "unset";
    public AttachBindingSpec RemoteImagePaste { get; init; } = "ctrl+v";
    public AttachBindingSpec NewTab { get; init; } = "prefix+c";
    public AttachBindingSpec RenameTab { get; init; } = "prefix+shift+t";
    public AttachBindingSpec PreviousTab { get; init; } = "prefix+p";
    public AttachBindingSpec NextTab { get; init; } = "prefix+n";
    public AttachBindingSpec MoveTabPrevious { get; init; } = "unset";
    public AttachBindingSpec MoveTabNext { get; init; } = "unset";
    public AttachBindingSpec SwitchTab { get; init; } = "prefix+1..9";
    public AttachBindingSpec SwitchWorkspace { get; init; } = "unset";
    public AttachBindingSpec CloseTab { get; init; } = "prefix+shift+x";
    public AttachBindingSpec RenamePane { get; init; } = "prefix+shift+p";
    public AttachBindingSpec EditScrollback { get; init; } = "prefix+e";
    public AttachBindingSpec AnnotateHandoff { get; init; } = "unset";
    public AttachBindingSpec CopyMode { get; init; } = "prefix+[";
    public AttachBindingSpec FocusPaneLeft { get; init; } = "prefix+h";
    public AttachBindingSpec FocusPaneDown { get; init; } = "prefix+j";
    public AttachBindingSpec FocusPaneUp { get; init; } = "prefix+k";
    public AttachBindingSpec FocusPaneRight { get; init; } = "prefix+l";
    public AttachBindingSpec SwapPaneLeft { get; init; } = "prefix+shift+h";
    public AttachBindingSpec SwapPaneDown { get; init; } = "prefix+shift+j";
    public AttachBindingSpec SwapPaneUp { get; init; } = "prefix+shift+k";
    public AttachBindingSpec SwapPaneRight { get; init; } = "prefix+shift+l";
    public AttachBindingSpec CyclePaneNext { get; init; } = "prefix+tab";
    public AttachBindingSpec CyclePanePrevious { get; init; } = "prefix+shift+tab";
    public AttachBindingSpec LastPane { get; init; } = "unset";
    public AttachBindingSpec SplitVertical { get; init; } = "prefix+v";
    public AttachBindingSpec SplitHorizontal { get; init; } = "prefix+minus";
    public AttachBindingSpec ClosePane { get; init; } = "prefix+x";
    public AttachBindingSpec Zoom { get; init; } = "prefix+z";
    public AttachBindingSpec Fullscreen { get; init; } = "unset";
    public AttachBindingSpec ResizeMode { get; init; } = "prefix+r";
    public AttachBindingSpec ResizePaneLeft { get; init; } = "unset";
    public AttachBindingSpec ResizePaneDown { get; init; } = "unset";
    public AttachBindingSpec ResizePaneUp { get; init; } = "unset";
    public AttachBindingSpec ResizePaneRight { get; init; } = "unset";
    public AttachBindingSpec ToggleSidebar { get; init; } = "prefix+b";
    public AttachBindingSpec IndexedTabs { get; init; } = "unset";
    public AttachBindingSpec IndexedWorkspaces { get; init; } = "unset";
    public AttachBindingSpec IndexedAgents { get; init; } = "unset";
    public IReadOnlyList<AttachKeyCommandConfig> Commands { get; init; } = [];

    public static AttachKeysConfig Default { get; } = new();
}

public sealed record AttachKeyCommandConfig
{
    public AttachBindingSpec Key { get; init; } = "";
    public string Type { get; init; } = "shell";
    public string Command { get; init; } = "";
    public string? Description { get; init; }
    public AttachPopupSize? Width { get; init; }
    public AttachPopupSize? Height { get; init; }
}
