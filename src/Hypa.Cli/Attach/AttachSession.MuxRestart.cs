using System.Text.Json;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach.Keys;

namespace Hypa.Cli.Attach;

/// <summary>
/// Sidebar update row and the restart it runs. Attach only detaches here.
/// <see cref="Mux.MuxAttachService"/> stops the mux and starts the
/// installed Hypa once the TTY is restored.
/// </summary>
public sealed partial class AttachSession
{
    /// <summary>
    /// Re-evaluates the update row from a heartbeat <c>ping</c> and repaints
    /// when it changes. A remote, cube or placement attach never shows it:
    /// the restart would act on the local mux, not the one on screen.
    /// </summary>
    internal static void RefreshUpdateNotice(AttachLiveState live, UnixRawTerminal? tty, JsonElement pong)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.UpdateNotices is not { } source)
            return;
        var notice = ShowsLocalMux(live) ? source.Evaluate(pong.GetRawText()) : null;
        if (!ApplyUpdateNotice(live, notice))
            return;
        if (tty is not null && live.ChromeEnabled)
            PaintChrome(tty, live);
    }

    /// <summary>True when the row changed.</summary>
    internal static bool ApplyUpdateNotice(AttachLiveState live, SidebarUpdateNotice? notice)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (Equals(live.UpdateNotice, notice))
            return false;
        live.WithPaint(() =>
        {
            live.UpdateNotice = notice;
            RecomposeLiveSidebar(live);
            RecomputeLiveChromeFromSidebar(live);
            live.InvalidateChrome();
        });
        return true;
    }

    internal static bool ShowsLocalMux(AttachLiveState live) =>
        !live.RemoteAttach
        && !live.RemoteDestination
        && string.IsNullOrEmpty(live.ConnectedPlacementId)
        && !string.IsNullOrWhiteSpace(live.SourceMuxSocketPath)
        && (string.IsNullOrEmpty(live.ActiveMuxSocketPath)
            || string.Equals(live.ActiveMuxSocketPath, live.SourceMuxSocketPath, StringComparison.Ordinal));

    /// <summary>Shows the y/n restart prompt for the sidebar row.</summary>
    internal static async Task PromptMuxRestartAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.UpdateNotice is not { } notice || !ShowsLocalMux(live))
            return;
        live.PendingMuxRestart = true;
        await ApplyModeEventsAsync(
                live.Engine.EnterConfirmMoveWork(notice.Prompt),
                live,
                control,
                tty,
                linked: null,
                ct)
            .ConfigureAwait(false);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    /// <summary>
    /// The restart prompt shares the Move Work y/n mode. True when the
    /// prompt that just closed was the restart prompt.
    /// </summary>
    internal static bool TryCompletePendingMuxRestart(AttachLiveState live)
    {
        if (!live.PendingMuxRestart)
            return false;
        live.PendingMuxRestart = false;
        if (live.Engine.TakeConfirmMoveWorkAccept())
        {
            live.MuxRestartRequested = true;
            live.DetachRequested = true;
        }

        return true;
    }
}
