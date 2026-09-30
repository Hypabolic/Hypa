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
        string? reason)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!sink.IsEnabled(ProcessLogLevel.Information))
            return;
        sink.Write(Base(ProcessLogEvents.AttachObserve, ProcessLogEvents.SubsystemAttach,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            PaneId = paneId,
            Reason = reason,
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
        string? attachClientId) =>
        sink.Write(Base(ProcessLogEvents.CubesConnectStage, ProcessLogEvents.SubsystemCubes,
            ProcessLogEvents.OutcomeOk, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            Stage = stage,
            ElapsedMs = elapsedMs,
            StageMs = stageMs,
            EndpointId = endpointId,
            Transport = transport,
        });

    public static void CubesOutcome(
        IProcessLogSink sink,
        string action,
        string outcome,
        string? sessionId,
        string? attachClientId) =>
        sink.Write(Base(ProcessLogEvents.CubesConnectOutcome, ProcessLogEvents.SubsystemCubes,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            Action = action,
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

    public static void EventReceived(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string eventType,
        string? paneId,
        string? tabId) =>
        sink.Write(Base(ProcessLogEvents.AttachEventReceived, ProcessLogEvents.SubsystemAttach,
            ProcessLogEvents.OutcomeOk, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            EventType = eventType,
            PaneId = paneId,
            TabId = tabId,
        });

    public static void EventOutcome(
        IProcessLogSink sink,
        string? sessionId,
        string? attachClientId,
        string eventType,
        string? paneId,
        string? tabId,
        string outcome,
        string? reason = null) =>
        sink.Write(Base(ProcessLogEvents.AttachEventOutcome, ProcessLogEvents.SubsystemAttach,
            outcome, ProcessLogLevel.Information, sessionId, attachClientId) with
        {
            EventType = eventType,
            PaneId = paneId,
            TabId = tabId,
            Reason = reason,
        });

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
