using Hypa.AgentRuntime.Protocol;

namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Attach and UI process-log records.
/// for tab focus and rename. Overlay, sidebar, and Cubes are Hypa-native.
/// </summary>
public static class AttachProcessLog
{
    public static void Connect(IProcessLogSink sink, string? sessionId) =>
        sink.Write(Base(ProcessLogEvents.AttachConnect, ProcessLogEvents.SubsystemAttach,
            ProcessLogEvents.OutcomeStarted, ProcessLogLevel.Information, sessionId, null));

    public static void Snapshot(IProcessLogSink sink, string? sessionId) =>
        sink.Write(Base(ProcessLogEvents.AttachSnapshot, ProcessLogEvents.SubsystemAttach,
            ProcessLogEvents.OutcomeOk, ProcessLogLevel.Information, sessionId, null));

    public static void Lease(IProcessLogSink sink, string? sessionId, string? attachClientId) =>
        sink.Write(Base(ProcessLogEvents.AttachLease, ProcessLogEvents.SubsystemAttach,
            ProcessLogEvents.OutcomeOk, ProcessLogLevel.Information, sessionId, attachClientId));

    public static void Subscribe(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string? subscriptionId) =>
        sink.Write(Base(ProcessLogEvents.AttachSubscribe, ProcessLogEvents.SubsystemAttach,
            ProcessLogEvents.OutcomeOk, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            SubscriptionId = subscriptionId,
        });

    public static void Failed(
        IProcessLogSink sink,
        string stage,
        string? sessionId,
        string? attachClientId,
        string err)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Write(Base(StageEvent(stage), ProcessLogEvents.SubsystemAttach,
            ProcessLogEvents.OutcomeError, ProcessLogLevel.Warning, sessionId, attachClientId) with
        {
            Action = stage,
            Err = err,
        });
    }

    public static void Detach(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string outcome = ProcessLogEvents.OutcomeOk) =>
        sink.Write(Base(ProcessLogEvents.AttachDetach, ProcessLogEvents.SubsystemAttach,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId));

    public static void Disconnect(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string? reason,
        string outcome = ProcessLogEvents.OutcomeCompleted) =>
        sink.Write(Base(ProcessLogEvents.AttachDisconnect, ProcessLogEvents.SubsystemAttach,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            DisconnectReason = reason,
        });

    /// <summary>
    /// One visible-pane observe decision: issued, skipped, or failed.
    /// </summary>
    public static void Observe(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string? paneId,
        string outcome,
        string? reason) =>
        Observe(sink, sessionId, attachClientId, paneId, outcome, reason, dropCount: null);

    /// <summary>
    /// One observe decision with <paramref name="dropCount"/>: the number of
    /// frames of the pane that the client dropped in a row for this reason.
    /// </summary>
    public static void Observe(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string? paneId,
        string outcome,
        string? reason,
        long? dropCount)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!sink.IsEnabled(ProcessLogLevel.Information))
            return;
        sink.Write(Base(ProcessLogEvents.AttachObserve, ProcessLogEvents.SubsystemAttach,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            PaneId = paneId,
            Reason = reason,
            DropCount = dropCount,
        });
    }

    public static void LoopFault(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string loop,
        Exception error) =>
        sink.Write(Base(ProcessLogEvents.AttachLoopFault, ProcessLogEvents.SubsystemAttach,
            ProcessLogEvents.OutcomeError, ProcessLogLevel.Warning, sessionId, attachClientId) with
        {
            Action = loop,
            Reason = error.GetType().Name + ": " + error.Message,
        });

    public static void TabRequested(
        IProcessLogSink sink,
        string action,
        string? sessionId,
        string? attachClientId,
        string? tabId,
        string? workspaceId,
        string? previousTabId,
        string? currentTabId,
        string? requestId = null,
        string? source = null) =>
        sink.Write(Base(ProcessLogEvents.TabRequested, ProcessLogEvents.SubsystemTab,
            ProcessLogEvents.OutcomeStarted, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            Action = action,
            TabId = tabId,
            WorkspaceId = workspaceId,
            PreviousTabId = previousTabId,
            CurrentTabId = currentTabId,
            RequestId = requestId,
            Source = source,
        });

    public static void TabOutcome(
        IProcessLogSink sink,
        string action,
        string outcome,
        string? sessionId,
        string? attachClientId,
        string? tabId,
        string? workspaceId,
        string? previousTabId,
        string? currentTabId,
        string? requestId = null,
        string? source = null) =>
        sink.Write(Base(ProcessLogEvents.TabOutcome, ProcessLogEvents.SubsystemTab,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            Action = action,
            TabId = tabId,
            WorkspaceId = workspaceId,
            PreviousTabId = previousTabId,
            CurrentTabId = currentTabId,
            RequestId = requestId,
            Source = source,
        });

    public static void OverlayRequested(
        IProcessLogSink sink,
        string surface,
        string? sessionId,
        string? attachClientId) =>
        sink.Write(Base(ProcessLogEvents.OverlayRequested, ProcessLogEvents.SubsystemOverlay,
            ProcessLogEvents.OutcomeStarted, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            Surface = surface,
        });

    public static void OverlayOutcome(
        IProcessLogSink sink,
        string surface,
        string outcome,
        string? sessionId,
        string? attachClientId) =>
        sink.Write(Base(ProcessLogEvents.OverlayOutcome, ProcessLogEvents.SubsystemOverlay,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            Surface = surface,
        });

    public static void SidebarRequested(
        IProcessLogSink sink,
        string surface,
        string? sessionId,
        string? attachClientId) =>
        sink.Write(Base(ProcessLogEvents.SidebarRequested, ProcessLogEvents.SubsystemSidebar,
            ProcessLogEvents.OutcomeStarted, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            Surface = surface,
        });

    public static void SidebarOutcome(
        IProcessLogSink sink,
        string surface,
        string outcome,
        string? sessionId,
        string? attachClientId) =>
        sink.Write(Base(ProcessLogEvents.SidebarOutcome, ProcessLogEvents.SubsystemSidebar,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            Surface = surface,
        });

    public static void CubesRequested(
        IProcessLogSink sink,
        string action,
        string? sessionId,
        string? attachClientId) =>
        sink.Write(Base(ProcessLogEvents.CubesConnectRequested, ProcessLogEvents.SubsystemCubes,
            ProcessLogEvents.OutcomeStarted, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            Action = action,
        });

    public static void CubesStage(
        IProcessLogSink sink,
        string stage,
        long elapsedMs,
        long stageMs,
        string? endpointId,
        string? transport,
        string? sessionId,
        string? attachClientId,
        string? failureReason = null)
    {
        var failed = failureReason is not null;
        sink.Write(Base(ProcessLogEvents.CubesConnectStage, ProcessLogEvents.SubsystemCubes,
            failed ? ProcessLogEvents.OutcomeError : ProcessLogEvents.OutcomeOk,
            failed ? ProcessLogLevel.Warning : ProcessLogLevel.Information,
            sessionId, attachClientId) with
        {
            Stage = stage,
            ElapsedMs = elapsedMs,
            StageMs = stageMs,
            EndpointId = endpointId,
            Transport = transport,
            Reason = failureReason,
        });
    }

    public static void CubesOutcome(
        IProcessLogSink sink,
        string action,
        string outcome,
        string? sessionId,
        string? attachClientId,
        string? reason = null,
        string? detail = null) =>
        sink.Write(Base(ProcessLogEvents.CubesConnectOutcome, ProcessLogEvents.SubsystemCubes,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            Action = action,
            Reason = reason,
            Err = detail,
        });

    public static void SettingsRequested(
        IProcessLogSink sink,
        string key,
        string? sessionId,
        string? attachClientId,
        string? value,
        string? valueKind,
        string? digest) =>
        sink.Write(Base(ProcessLogEvents.SettingsRequested, ProcessLogEvents.SubsystemSettings,
            ProcessLogEvents.OutcomeStarted, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            SettingKey = key,
            SettingValue = value,
            ValueKind = valueKind,
            Digest = digest,
        });

    public static void SettingsOutcome(
        IProcessLogSink sink,
        string key,
        string outcome,
        string? sessionId,
        string? attachClientId,
        string? value,
        string? valueKind,
        string? digest) =>
        sink.Write(Base(ProcessLogEvents.SettingsOutcome, ProcessLogEvents.SubsystemSettings,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            SettingKey = key,
            SettingValue = value,
            ValueKind = valueKind,
            Digest = digest,
        });

    public static string TabAction(string method) =>
        method switch
        {
            ProtocolMethods.TabCreate => "create",
            ProtocolMethods.TabRename => "rename",
            ProtocolMethods.TabFocus => "focus",
            ProtocolMethods.TabMove => "move",
            ProtocolMethods.TabClose => "close",
            _ => "",
        };

    /// <summary>
    /// The level of the per-event attach trace (<c>attach.event.received</c>
    /// and <c>attach.event.outcome</c>). Both lines of one event use the same
    /// level. Information is only for an event that is rare or that changes
    /// the UI: a lifecycle change of a session, workspace, tab, pane, occupant,
    /// popup or overlay, a layout or placement change, an agent or program
    /// status change, program attention, a bell, a notification, a window
    /// title, a lease change, a rejected input, a settings or config change,
    /// a worktree change, a checkpoint, and the attach endpoint handshake.
    /// Each other event is Debug. This includes <c>pane.scroll_changed</c>,
    /// which a busy pane sends up to 10 times each second, the metadata
    /// updates, <c>pane.output_matched</c>, <c>resource.changed</c>,
    /// <c>binding.changed</c>, <c>export.acked</c>, and a type that is not
    /// in the list.
    /// </summary>
    public static ProcessLogLevel EventTraceLevel(string? eventType) =>
        eventType switch
        {
            ProtocolEventTypes.SessionLifecycle
                or ProtocolEventTypes.WorkspaceLifecycle
                or ProtocolEventTypes.TabLifecycle
                or ProtocolEventTypes.PaneLifecycle
                or ProtocolEventTypes.OccupantLifecycle
                or ProtocolEventTypes.PopupLifecycle
                or ProtocolEventTypes.OverlayLifecycle
                or ProtocolEventTypes.LayoutUpdated
                or ProtocolEventTypes.PanePlacementChanged
                or ProtocolEventTypes.PaneAgentStatusChanged
                or ProtocolEventTypes.PaneProgramStatusChanged
                or ProtocolEventTypes.PaneProgramAttention
                or ProtocolEventTypes.PaneBell
                or ProtocolEventTypes.NotificationShown
                or ProtocolEventTypes.ClientWindowTitleChanged
                or ProtocolEventTypes.LeaseChanged
                or ProtocolEventTypes.PaneInputRejected
                or ProtocolEventTypes.SettingsChanged
                or ProtocolEventTypes.ConfigChanged
                or ProtocolEventTypes.ConfigReloaded
                or ProtocolEventTypes.WorktreeCreated
                or ProtocolEventTypes.WorktreeOpened
                or ProtocolEventTypes.WorktreeRemoved
                or ProtocolEventTypes.CheckpointLifecycle
                or AttachEndpointProtocol.WelcomeEvent
                or AttachEndpointProtocol.ProjectionSnapshotEvent
                or AttachEndpointProtocol.PresentationSyncEvent
                or AttachEndpointProtocol.PresentationReadyEvent => ProcessLogLevel.Information,
            _ => ProcessLogLevel.Debug,
        };

    public static void EventReceived(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string eventType,
        string? paneId,
        string? tabId)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var level = EventTraceLevel(eventType);
        if (!sink.IsEnabled(level))
            return;
        sink.Write(Base(ProcessLogEvents.AttachEventReceived, ProcessLogEvents.SubsystemAttach,
            ProcessLogEvents.OutcomeOk, level, sessionId, attachClientId) with
        {
            EventType = eventType,
            PaneId = paneId,
            TabId = tabId,
        });
    }

    public static void EventOutcome(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string eventType,
        string? paneId,
        string? tabId,
        string outcome,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var level = EventTraceLevel(eventType);
        if (!sink.IsEnabled(level))
            return;
        sink.Write(Base(ProcessLogEvents.AttachEventOutcome, ProcessLogEvents.SubsystemAttach,
            outcome, level, sessionId, attachClientId) with
        {
            EventType = eventType,
            PaneId = paneId,
            TabId = tabId,
            Reason = reason,
        });
    }

    public static void AgentStatusEmitted(
        IProcessLogSink sink,
        string? sessionId,
        string paneId,
        string? agent,
        string agentStatus,
        string previousStatus) =>
        sink.Write(Base(ProcessLogEvents.MuxAgentStatusChanged, ProcessLogEvents.SubsystemMux,
            ProcessLogEvents.OutcomeOk, ProcessLogLevel.Information, sessionId, null) with
        {
            PaneId = paneId,
            Agent = agent,
            AgentSpecified = true,
            AgentStatus = agentStatus,
            PreviousStatus = previousStatus,
        });

    public static string StageEvent(string stage) =>
        stage switch
        {
            "connect" => ProcessLogEvents.AttachConnect,
            "snapshot" => ProcessLogEvents.AttachSnapshot,
            "lease" => ProcessLogEvents.AttachLease,
            "subscribe" => ProcessLogEvents.AttachSubscribe,
            _ => ProcessLogEvents.AttachFail,
        };

    private static ProcessLogRecord Base(
        string eventName,
        string subsystem,
        string outcome,
        ProcessLogLevel level,
        string? sessionId,
        string? attachClientId) =>
        new()
        {
            Event = eventName,
            Subsystem = subsystem,
            Outcome = outcome,
            Ts = DateTimeOffset.UtcNow,
            Pid = Environment.ProcessId,
            Level = level,
            SessionId = sessionId,
            AttachClientId = attachClientId,
        };
}
