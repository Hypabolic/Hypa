using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Worktrees;

namespace Hypa.Cli.Attach.Input;

/// <summary>
// / Modes that need ASCII prefix input.
/// <c>src/client/shell/input_source.rs:3-23</c> <c>wants_ascii_input</c>.
/// </summary>
public static class PrefixAsciiNeed
{
    public static bool WantsAscii(AttachClientMode mode, WorktreeDialogKind worktree)
    {
        if (worktree is WorktreeDialogKind.Remove)
            return true;
        return mode is AttachClientMode.ConfirmClose
            or AttachClientMode.KeybindHelp
            or AttachClientMode.Navigator
            or AttachClientMode.WorkspacePicker
            or AttachClientMode.TransferPicker
            or AttachClientMode.ContextMenu
            or AttachClientMode.GlobalMenu
            or AttachClientMode.Prefix
            or AttachClientMode.Navigate
            or AttachClientMode.Resize
            or AttachClientMode.Copy;
    }
}
