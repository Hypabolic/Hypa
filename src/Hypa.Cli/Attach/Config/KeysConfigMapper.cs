using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach.Keys;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Cli.Attach.Config;

public static class KeysConfigMapper
{
    public static KeysConfig Map(AttachKeysConfig keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new KeysConfig
        {
            Prefix = Bind(keys.Prefix),
            Help = Bind(keys.Help),
            Settings = Bind(keys.Settings),
            NewWorkspace = Bind(keys.NewWorkspace),
            NewWorktree = Bind(keys.NewWorktree),
            OpenWorktree = Bind(keys.OpenWorktree),
            RemoveWorktree = Bind(keys.RemoveWorktree),
            RenameWorkspace = Bind(keys.RenameWorkspace),
            CloseWorkspace = Bind(keys.CloseWorkspace),
            WorkspacePicker = Bind(keys.WorkspacePicker),
            Goto = Bind(keys.Goto),
            Navigate = Bind(keys.Navigate),
            NavigateWorkspaceUp = Bind(keys.NavigateWorkspaceUp),
            NavigateWorkspaceDown = Bind(keys.NavigateWorkspaceDown),
            NavigatePaneLeft = Bind(keys.NavigatePaneLeft),
            NavigatePaneDown = Bind(keys.NavigatePaneDown),
            NavigatePaneUp = Bind(keys.NavigatePaneUp),
            NavigatePaneRight = Bind(keys.NavigatePaneRight),
            Detach = Bind(keys.Detach),
            ReloadConfig = Bind(keys.ReloadConfig),
            OpenNotificationTarget = Bind(keys.OpenNotificationTarget),
            PreviousWorkspace = Bind(keys.PreviousWorkspace),
            NextWorkspace = Bind(keys.NextWorkspace),
            PreviousAgent = Bind(keys.PreviousAgent),
            NextAgent = Bind(keys.NextAgent),
            FocusAgent = Bind(keys.FocusAgent),
            RemoteImagePaste = Bind(keys.RemoteImagePaste),
            NewTab = Bind(keys.NewTab),
            RenameTab = Bind(keys.RenameTab),
            PreviousTab = Bind(keys.PreviousTab),
            NextTab = Bind(keys.NextTab),
            MoveTabPrevious = Bind(keys.MoveTabPrevious),
            MoveTabNext = Bind(keys.MoveTabNext),
            SwitchTab = Bind(keys.SwitchTab),
            SwitchWorkspace = Bind(keys.SwitchWorkspace),
            CloseTab = Bind(keys.CloseTab),
            RenamePane = Bind(keys.RenamePane),
            EditScrollback = Bind(keys.EditScrollback),
            AnnotateHandoff = Bind(keys.AnnotateHandoff),
            CopyMode = Bind(keys.CopyMode),
            FocusPaneLeft = Bind(keys.FocusPaneLeft),
            FocusPaneDown = Bind(keys.FocusPaneDown),
            FocusPaneUp = Bind(keys.FocusPaneUp),
            FocusPaneRight = Bind(keys.FocusPaneRight),
            SwapPaneLeft = Bind(keys.SwapPaneLeft),
            SwapPaneDown = Bind(keys.SwapPaneDown),
            SwapPaneUp = Bind(keys.SwapPaneUp),
            SwapPaneRight = Bind(keys.SwapPaneRight),
            CyclePaneNext = Bind(keys.CyclePaneNext),
            CyclePanePrevious = Bind(keys.CyclePanePrevious),
            LastPane = Bind(keys.LastPane),
            SplitVertical = Bind(keys.SplitVertical),
            SplitHorizontal = Bind(keys.SplitHorizontal),
            ClosePane = Bind(keys.ClosePane),
            Zoom = Bind(keys.Zoom),
            Fullscreen = Bind(keys.Fullscreen),
            ResizeMode = Bind(keys.ResizeMode),
            ResizePaneLeft = Bind(keys.ResizePaneLeft),
            ResizePaneDown = Bind(keys.ResizePaneDown),
            ResizePaneUp = Bind(keys.ResizePaneUp),
            ResizePaneRight = Bind(keys.ResizePaneRight),
            ToggleSidebar = Bind(keys.ToggleSidebar),
            IndexedTabs = Bind(keys.IndexedTabs),
            IndexedWorkspaces = Bind(keys.IndexedWorkspaces),
            IndexedAgents = Bind(keys.IndexedAgents),
            Commands = MapCommands(keys.Commands),
        };
    }

    public static Result<KeyBindingTable, KeyCompileError> Compile(AttachKeysConfig keys) =>
        KeyBindingTable.Compile(Map(keys));

    public static bool TryCompile(AttachKeysConfig keys, TextWriter errors, out KeyBindingTable? table)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(errors);
        var compiled = Compile(keys);
        if (compiled.IsOk)
        {
            table = compiled.Value;
            return true;
        }

        errors.WriteLine(AttachConfigErrors.IssuesFound);
        errors.WriteLine($"config [keys]: {compiled.Error}");
        table = null;
        return false;
    }

    private static BindingSpec Bind(AttachBindingSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.IsUnset)
            return BindingSpec.Unset;

        // Each TOML string is one chord. ',' is a key name, not a delimiter.
        return BindingSpec.From(spec.Specs);
    }

    private static IReadOnlyList<KeyCommandBinding> MapCommands(IReadOnlyList<AttachKeyCommandConfig> commands)
    {
        if (commands.Count == 0)
            return [];
        var list = new List<KeyCommandBinding>(commands.Count);
        foreach (var command in commands)
        {
            list.Add(new KeyCommandBinding(
                Bind(command.Key),
                command.Command,
                command.Type,
                command.Description,
                command.Width,
                command.Height));
        }

        return list;
    }
}
