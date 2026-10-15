using System.Diagnostics;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Cubes.Connections;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Connections;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.AgentRuntime.Application.Attach.Status;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.Cli.Attach;

public sealed partial class AttachSession
{
    private static EndpointConnectionSupervisor? StartEndpointConnectionSupervisor(
        AttachLiveState live,
        ControlPlaneClient? control,
        UnixRawTerminal tty,
        SemaphoreSlim controlGate,
        CancellationToken attemptCt)
    {
        if (control is null)
            return null;

        var redial = new EndpointRedialPort(live, control, tty);
        return EndpointConnectionSupervisor.Start(
            () => live.Connections.Armed is not null,
            controlGate,
            token => EndpointConnectionSupervisor.StepAsync(live, redial, tty, token),
            attemptCt,
            live.Time);
    }

    /// <summary>
    /// The redial port. It starts a redial through the dest connect prep that
    /// a cube selection uses. It sends no mouse result and takes no click path.
    /// </summary>
    private sealed class EndpointRedialPort : IEndpointRedialPort
    {
        private readonly AttachLiveState _live;
        private readonly ControlPlaneClient _control;
        private readonly UnixRawTerminal _tty;

        public EndpointRedialPort(AttachLiveState live, ControlPlaneClient control, UnixRawTerminal tty)
        {
            _live = live;
            _control = control;
            _tty = tty;
        }

        public Task RedialAsync(string endpointId, CancellationToken ct)
        {
            // The same port a click and hypa client action use.
            var port = LiveControlPort(
                _live,
                new ControlPlaneAttachCommandPort(LiveControlClient(_live, _control) ?? _control));
            if (port is not LoggingAttachCommandPort)
                port = new LoggingAttachCommandPort(port, _live);
            return StartRedialAsync(_live, endpointId, port, _tty, ct);
        }
    }

    /// <summary>
    /// Starts one redial of a lost cube. It runs the guards that the cube
    /// selection runs for a redial, then the same dest connect prep.
    /// </summary>
    private static Task StartRedialAsync(
        AttachLiveState live,
        string endpointId,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        var cube = live.Cubes.FirstOrDefault(
            item => string.Equals(item.Id, endpointId, StringComparison.Ordinal));
        if (cube is null || cube.Kind == SidebarCubeKind.Local || ct.IsCancellationRequested)
            return Task.CompletedTask;
        if (live.CubesConnect is null)
        {
            RecordCubesConnectOutcome(live, ProcessLogEvents.OutcomeRejected);
            return Task.CompletedTask;
        }

        var stageWatch = Stopwatch.StartNew();
        AttachProcessLog.CubesRequested(live.ProcessLog, "connect", live.SessionName, live.AttachClientId);
        live.CubesConnectOutcomeRecorded = false;
        var intent = new EndpointActivationIntent
        {
            EndpointId = endpointId,
            StageClock = new CubesConnectStageClock(
                stageWatch,
                live.ProcessLog,
                endpointId,
                live.SessionName,
                live.AttachClientId),
        };
        ScheduleDestConnectPrep(live, cube, intent, LiveControlPort(live, control), tty, ct);
        if (tty is not null && live.ChromeEnabled)
            PaintChrome(tty, live);
        return Task.CompletedTask;
    }

    /// <summary>
    /// True when the in-flight dest prep or activation targets this endpoint.
    /// </summary>
    internal static bool AttemptTargetsCube(AttachLiveState live, string endpointId) =>
        string.Equals(live.DestConnectAttempt?.EndpointId, endpointId, StringComparison.Ordinal)
        || string.Equals(live.PendingActivation?.Target.EndpointId, endpointId, StringComparison.Ordinal);

    /// <summary>
    /// The active remote cube was lost outside an activation. The connection
    /// is lost, and the view freezes on the last frame. Chrome input stays
    /// live. Pane keys stay blocked by the frozen presentation. The
    /// supervisor dials the same cube again. Caller holds
    /// <see cref="AttachLiveState.ActivationGate"/>. Returns false when the
    /// loss is not a remote cube with no activation pending. The caller then
    /// uses the normal handoff error.
    /// </summary>
    internal static bool ArmEndpointLossLocked(AttachLiveState live, string endpointId)
    {
        if (live.PendingActivation is not null)
            return false;
        var cube = live.Cubes.FirstOrDefault(
            item => string.Equals(item.Id, endpointId, StringComparison.Ordinal));
        if (cube is null || cube.Kind == SidebarCubeKind.Local)
            return false;

        var connection = live.Connections.Entity(cube.Id);
        if (connection.State is EndpointConnectionState.Connecting or EndpointConnectionState.Failed)
        {
            // The attach never committed this connection through the registry.
            // It was live when it dropped, so record it as live first.
            _ = connection.Live(LiveConnectionFor(live, cube.Id), LiveBootIdFor(live, cube.Id));
        }

        var lost = connection.Lose("connection was lost");
        if (lost.IsOk)
        {
            AttachProcessLog.Failed(
                live.ProcessLog,
                "reconnect",
                live.SessionName,
                live.AttachClientId,
                $"{cube.Id} lost");
        }

        live.PresentationFrozen = true;
        live.InputSender?.DiscardQueued();
        if (!live.PlacementOwnerUnavailable)
            live.InputFrozen = false;
        live.FrozenChromePaintArmed = true;
        live.HostEncoder?.RequestRepaint();
        return true;
    }

    /// <summary>
    /// A person's selection of a cube. A click on the lost cube is one
    /// immediate redial, and the reconnect stays armed. Any other selection,
    /// including Local, cancels the reconnect.
    /// </summary>
    internal static void ApplyPersonSelectionToConnection(AttachLiveState live, SidebarCubeItem cube)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(cube);
        if (live.Connections.Armed is not { } armed)
            return;

        if (cube.Kind != SidebarCubeKind.Local
            && string.Equals(cube.Id, armed.EndpointId, StringComparison.Ordinal))
        {
            _ = armed.BeginRedial(immediate: true);
            return;
        }

        CancelReconnect(live, armed);
    }

    /// <summary>
    /// Ends the reconnect of an endpoint, and clears the reconnect copies
    /// that it set. An unrelated error stays.
    /// </summary>
    internal static void CancelReconnect(AttachLiveState live, EndpointConnection connection)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Cancel().IsOk)
            ClearReconnectStatus(live);
    }

    /// <summary>
    /// The commit point. The activation accepted the connection (Complete).
    /// The endpoint status reads Online while the activation runs. The
    /// reconnect stays armed. A rolled back activation leaves it armed.
    /// </summary>
    internal static void BeginEndpointCommit(AttachLiveState live, ConnectionId connection, string bootId)
    {
        ArgumentNullException.ThrowIfNull(live);
        _ = live.Connections.Entity(connection.EndpointId).BeginCommit(connection, bootId);
    }

    /// <summary>
    /// Ends the commit window of an endpoint. The window opens at Complete. Only
    /// Activated closes it through <see cref="CompleteEndpointCommit"/>. Every
    /// other end of an activation closes it here.
    /// </summary>
    internal static void AbandonEndpointCommit(AttachLiveState live, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Connections.TryGetEntity(endpointId, out var entity) && entity.IsCommitting)
            entity.AbandonCommit();
    }

    /// <summary>
    /// Clears the pending activation when it ends without Activated. It also
    /// closes the commit window of its source and its target. Call it wherever
    /// <see cref="AttachLiveState.PendingActivation"/> is cleared without Activated.
    /// </summary>
    internal static void ClearPendingActivationWithoutActivated(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var pending = live.PendingActivation;
        live.PendingActivation = null;
        if (pending is null)
            return;
        AbandonEndpointCommit(live, pending.Source.EndpointId);
        AbandonEndpointCommit(live, pending.Target.EndpointId);
    }

    /// <summary>
    /// The activation completed (Activated). A restored connection clears the
    /// reconnect copies and logs the restore once. Returns true when a lost or
    /// redialing connection was restored.
    /// </summary>
    internal static bool CompleteEndpointCommit(AttachLiveState live, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Connections.TryGetEntity(endpointId, out var entity) || !entity.IsCommitting)
            return false;

        var restored = entity.CompleteCommit();
        if (!restored.IsOk || restored.Value is not { } restoredEvent)
            return false;

        AttachProcessLog.CubesOutcome(
            live.ProcessLog,
            "reconnect",
            ProcessLogEvents.OutcomeOk,
            live.SessionName,
            live.AttachClientId);
        ClearReconnectStatus(live);
        return true;
    }

    /// <summary>Begins and completes the commit at once. Used when no activation runs.</summary>
    internal static void MarkEndpointLive(AttachLiveState live, ConnectionId connection, string bootId)
    {
        BeginEndpointCommit(live, connection, bootId);
        _ = CompleteEndpointCommit(live, connection.EndpointId);
    }

    /// <summary>The reason code of a redial whose activation failed.</summary>
    internal const string ActivationFailedReason = "activation_failed";

    /// <summary>
    /// Records a failed redial on the armed connection. The connection stays
    /// armed and keeps its schedule and its loss time.
    /// </summary>
    internal static void RecordRedialFailure(AttachLiveState live, string reasonCode, string? detail)
    {
        if (live.Connections.Armed is not { } armed)
            return;
        armed.AbandonCommit();
        _ = armed.RedialFailed(new ConnectError(reasonCode, detail));
    }

    /// <summary>The boot id that the bound client of an endpoint reported. Empty when unknown.</summary>
    internal static string LiveBootIdFor(AttachLiveState live, string endpointId) =>
        ClientForEndpoint(live, endpointId) is { } client
            && live.Connections.TryGetBootId(client, out var bootId)
            ? bootId
            : string.Empty;

    /// <summary>
    /// A connect failure while a reconnect is armed. Inside the grace window
    /// the failure stays quiet. After the window the status names the cube as
    /// unreachable. With no reconnect armed the failure is shown as before.
    /// </summary>
    internal static void PresentReconnectAwareFailure(
        AttachLiveState live,
        string? copy,
        UnixRawTerminal? tty,
        string reasonCode = CubesConnectReasons.DestConnectFailed)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Connections.Armed is not { } connection)
        {
            PresentConnectFailure(live, copy, tty);
            return;
        }

        AttachProcessLog.Failed(
            live.ProcessLog,
            "reconnect",
            live.SessionName,
            live.AttachClientId,
            copy ?? connection.EndpointId);
        _ = connection.RedialFailed(new ConnectError(reasonCode, copy));
        live.PresentationFrozen = true;
        live.InputSender?.DiscardQueued();
        ApplyReconnectStatus(live, connection);
        live.CubesConnectAction = CubesConnectActions.Noop;
        live.FrozenChromePaintArmed = true;
        live.HostEncoder?.RequestRepaint();
        if (tty is not null && live.ChromeEnabled)
            PaintChrome(tty, live, requestRepaint: true);
    }

    /// <summary>
    /// A handoff unavailable notice during an activation. A reconnect armed
    /// for an endpoint keeps the quiet status. Otherwise it is the normal
    /// handoff error.
    /// </summary>
    internal static void PresentActivationUnavailable(AttachLiveState live, string message)
    {
        if (live.Connections.Armed is not { } connection)
        {
            PresentHandoffUnavailable(live, message);
            return;
        }

        RecordRedialFailure(live, ActivationFailedReason, message);
        live.PresentationFrozen = true;
        live.InputSender?.DiscardQueued();
        ApplyReconnectStatus(live, connection);
        live.FrozenChromePaintArmed = true;
        live.HostEncoder?.RequestRepaint();
    }

    /// <summary>
    /// Applies the reconnect status for the current time. Before the quiet
    /// wait, the reconnect entry is cleared. From the quiet wait to the grace
    /// window, it is a notice. After the grace window, it is the error copy.
    /// Only the reconnect owner writes this entry. Returns true when the entry
    /// changed.
    /// </summary>
    internal static bool ApplyReconnectStatus(AttachLiveState live, EndpointConnection connection)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(connection);
        var (statusCopy, unreachableCopy) = ReconnectCopies(live, connection.EndpointId);
        var previous = live.Status.Entry(StatusOwner.Reconnect);
        var (severity, text) = EndpointReconnectPolicy.StatusFor(connection) switch
        {
            ReconnectStatusLevel.Unreachable => (StatusSeverity.Error, unreachableCopy),
            ReconnectStatusLevel.Notice => (StatusSeverity.Notice, statusCopy),
            _ => (StatusSeverity.Notice, (string?)null),
        };

        if (text is null)
        {
            live.Status.Clear(StatusOwner.Reconnect);
            return previous is not null;
        }

        // Set again only on a change. A repeat would raise the sequence and
        // put the reconnect entry above a newer error of another owner.
        if (previous is not null
            && previous.Severity == severity
            && string.Equals(previous.Text, text, StringComparison.Ordinal))
            return false;

        live.Status.Set(StatusOwner.Reconnect, severity, text);
        return true;
    }

    /// <summary>
    /// Clears the reconnect entry. Other owners keep their entries.
    /// </summary>
    internal static void ClearReconnectStatus(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.Status.Clear(StatusOwner.Reconnect);
    }

    private static (string StatusCopy, string UnreachableCopy) ReconnectCopies(
        AttachLiveState live,
        string endpointId)
    {
        var label = EndpointLabel(live, endpointId);
        return ("Reconnecting to " + label + "…", label + " is unreachable. Retrying…");
    }

    internal static bool ShowReconnectStatus(
        AttachLiveState live,
        EndpointConnection connection,
        UnixRawTerminal? tty)
    {
        if (!ApplyReconnectStatus(live, connection))
            return false;
        PaintReconnectStatus(live, tty);
        return true;
    }

    internal static void PaintReconnectStatus(AttachLiveState live, UnixRawTerminal? tty)
    {
        live.FrozenChromePaintArmed = true;
        live.HostEncoder?.RequestRepaint();
        PaintStatus(tty, live);
    }
}
