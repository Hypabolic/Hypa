using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach.Keys;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>
/// Resolves <c>ui.host_cursor</c>. Auto on Unix TTY attach is native.
/// Windows ConPTY is.
/// </summary>
public static class HostCursorPolicy
{
    public static HostCursorMode Resolve(HostCursorMode configured)
    {
        if (configured is HostCursorMode.Auto)
            return HostCursorMode.Native;
        return configured;
    }

    public static bool IsDrawn(HostCursorMode configured) =>
        Resolve(configured) is HostCursorMode.Drawn;

    public static bool IsNative(HostCursorMode configured) =>
        Resolve(configured) is HostCursorMode.Native;

    /// <summary>
    /// Copy, help, menus, a pinned copy overlay, and a session-modal popup
    /// own the caret. Wheel scroll hides the caret from server offset, not
    /// Prefix, navigate, and resize still use the pane VT cursor.
    /// </summary>
    public static bool OverlayOwnsCaret(
        AttachClientMode mode,
        bool historyOverlay,
        bool popupOpen = false) =>
        KeyEngine.SuppressesLiveRemap(mode) || historyOverlay || popupOpen;
}
