using System.Net.Sockets;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>
/// The stall path of an attach attempt: the stall record in the attach log,
/// and the reconnect window that keeps the detach key and the action socket.
/// </summary>
public sealed partial class AttachSession
{
    internal const string StallLogStage = "attach_stall";

    /// <summary>The loop name of <paramref name="finished"/> for the attach log.</summary>
    internal static string AttachLoopName(
        Task finished,
        Task renderTask,
        Task inputTask,
        Task renewTask,
        Task beatTask,
        Task tabBarTask) =>
        ReferenceEquals(finished, renderTask) ? "render"
        : ReferenceEquals(finished, inputTask) ? "input"
        : ReferenceEquals(finished, renewTask) ? "renew"
        : ReferenceEquals(finished, beatTask) ? "heartbeat"
        : ReferenceEquals(finished, tabBarTask) ? "tab_bar"
        : "unknown";

    /// <summary>
    /// Writes one record when an attempt ends in a stall: the reason, the
    /// loop that ended the attempt, and its fault when it has one.
    /// </summary>
    internal static void RecordAttachStall(
        AttachLiveState live,
        string? reason,
        Task finished,
        Task renderTask,
        Task inputTask,
        Task renewTask,
        Task beatTask,
        Task tabBarTask)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(finished);
        var loop = AttachLoopName(finished, renderTask, inputTask, renewTask, beatTask, tabBarTask);
        var fault = finished.Exception?.GetBaseException();
        var status = finished.IsFaulted ? "faulted" : finished.IsCanceled ? "canceled" : "completed";
        var err = $"{reason ?? "stall"}; loop={loop} {status}";
        if (fault is not null)
            err += $"; {fault.GetType().Name}: {fault.Message}";
        AttachProcessLog.Failed(live.ProcessLog, StallLogStage, live.SessionName, live.AttachClientId, err);
    }

    /// <summary>
    /// Runs the stall reconnect inside <see cref="StallReconnectWindow"/>.
    /// The window reads stdin for the detach key and answers on the action
    /// socket until the reconnect ends.
    /// </summary>
    private async Task<StallWindowResult> ReconnectInStallWindowAsync(
        MuxReadyInfo ready,
        ControlPlaneAttachCommandPort commandPort,
        AttachLiveState live,
        SnapshotAssembler assembler,
        UnixRawTerminal tty,
        SemaphoreSlim controlGate,
        LeaseRenewLoop renew,
        CancellationToken ct) =>
        await StallReconnectWindow.RunAsync(
                token => ReconnectAfterStallAsync(
                    ready, commandPort, live, assembler, tty, controlGate, renew, token),
                token => AttachStdinProducer.Start(0, token, clock: live.Latency.Now),
                live.Table,
                token => TryStartReconnectingActionHost(SemanticActionStateDirectory(live), live, token),
                DiscardLateReconnect,
                ct,
                // The user left. A reconnect that still runs must not paint.
                // CloseHostOutput takes the TTY write lock, so a write in
                // progress ends first and no later write reaches the host.
                onAbandon: () =>
                {
                    live.ChromeEnabled = false;
                    tty.CloseHostOutput();
                })
            .ConfigureAwait(false);

    /// <summary>
    /// The action socket while the attach reconnects. It takes no control
    /// gate. <c>latency.report</c> and <c>latency.reset</c> apply. Every
    /// other action gets the outcome <c>reconnecting</c>.
    /// </summary>
    internal static AttachSemanticActionHost? TryStartReconnectingActionHost(
        string stateDirectory,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        try
        {
            return AttachSemanticActionHost.Start(
                stateDirectory,
                live,
                controlGate: null,
                (request, _) => Task.FromResult(
                    Reply(request, live, AttachSemanticOutcomes.Reconnecting, "the attach is reconnecting")),
                ct);
        }
        catch (Exception ex) when (
            ex is IOException
                or InvalidOperationException
                or SocketException
                or PlatformNotSupportedException
                or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A reconnect that ended after the user left. Close what it opened.</summary>
    private static void DiscardLateReconnect(StallReconnectResult late)
    {
        var render = late.Render;
        var control = late.Control;
        if (render is not null)
            _ = render.DisposeAsync().AsTask();
        if (control is not null && !ReferenceEquals(control, render))
            _ = control.DisposeAsync().AsTask();
    }
}
