using Hypa.Cli.Attach;

namespace Hypa.Cli.Attach.Keys;

/// <summary>
/// Published attach <c>client_mode</c>. Worktree dialogs sit outside
/// and remove as <c>ClientShellOverlay</c> variants.
/// other input when <c>overlay.is_some()</c>. Hidden list and context
/// tokens stay on <see cref="KeyEngine.ClientModeToken"/> so a later
/// hidden follow-up can add them without dropping worktree busy.
/// </summary>
internal static class AttachClientModePublication
{
    public const string WorktreeToken = "worktree";

    public const string CubesPairingToken = "cubes_pairing";

    public static string PublishedToken(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Worktrees.IsOpen)
            return WorktreeToken;
        if (live.CubesPairing.IsOpen)
            return CubesPairingToken;
        return live.Engine.ClientModeToken;
    }

    /// <summary>
    /// Active pane overlay, session popup, or command overlay occupies
    /// the same exclusive surface as a worktree dialog.
    /// </summary>
    public static bool ConflictsWithWorktreeOpen(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Engine.PopupOpen || live.PopupOpen)
            return true;
        if (live.Engine.OverlayOpen || live.Overlay.OwnsModal)
            return true;
        if (!string.IsNullOrWhiteSpace(live.Dispatcher.CommandOverlayPaneId))
            return true;
        if (!string.IsNullOrWhiteSpace(live.OverlayPaneId))
            return true;
        return false;
    }

    /// <summary>
    /// Human modal already occupies the exclusive surface. A delayed
    /// overlay show must lose admission. Do not count pane overlay.
    /// </summary>
    public static bool HumanExclusiveSurfaceOpen(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Worktrees.IsOpen)
            return true;
        if (live.CubesPairing.IsOpen)
            return true;
        if (live.Engine.PopupOpen || live.PopupOpen)
            return true;
        if (!string.IsNullOrWhiteSpace(live.Dispatcher.CommandOverlayPaneId))
            return true;
        if (live.MouseMenu is not null)
            return true;
        if (live.Engine.ExclusiveSurfaceOpen
            && !live.Engine.OverlayOpen
            && !live.Overlay.OwnsModal)
        {
            return true;
        }

        return live.Engine.Mode is not (AttachClientMode.Terminal or AttachClientMode.Prefix);
    }

    /// <summary>
    /// External modal start. Worktree open is busy even when
    /// <see cref="KeyEngine.Mode"/> is still Terminal.
    /// </summary>
    public static bool TryBeginExternalModal(AttachLiveState live, out string? busyMessage)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Worktrees.IsOpen || live.CubesPairing.IsOpen)
        {
            busyMessage = "ui_busy";
            return false;
        }

        return live.Engine.TryBeginPopup(out busyMessage);
    }
}
