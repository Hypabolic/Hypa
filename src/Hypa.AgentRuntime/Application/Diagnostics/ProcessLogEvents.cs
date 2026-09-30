namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Stable process-log event names.
/// <c>src/logging.rs:41-128</c> and <c>:257-289</c>. Hypa-native names only.
/// </summary>
public static class ProcessLogEvents
{
    public const string AppStartup = "app.startup";
    public const string AppShutdown = "app.shutdown";
    public const string ApiRequestStart = "api.request.start";
    public const string ApiRequestComplete = "api.request.complete";
    public const string ApiRequestFail = "api.request.fail";
    public const string TabRequested = "tab.requested";
    public const string TabOutcome = "tab.outcome";
    public const string OverlayRequested = "overlay.requested";
    public const string OverlayOutcome = "overlay.outcome";
    public const string SidebarRequested = "sidebar.requested";
    public const string SidebarOutcome = "sidebar.outcome";
    public const string CubesConnectRequested = "cubes.connect.requested";
    public const string CubesConnectStage = "cubes.connect.stage";
    public const string CubesConnectOutcome = "cubes.connect.outcome";
    public const string SettingsRequested = "settings.requested";
    public const string SettingsOutcome = "settings.outcome";
    public const string AttachConnect = "attach.connect";
    public const string AttachSnapshot = "attach.snapshot";
    public const string AttachLease = "attach.lease";
    public const string AttachSubscribe = "attach.subscribe";
    public const string AttachFail = "attach.fail";
    public const string AttachDetach = "attach.detach";
    public const string AttachDisconnect = "attach.disconnect";
    public const string AttachLoopFault = "attach.loop.fault";
    public const string AttachObserve = "attach.observe";
    public const string AttachEventReceived = "attach.event.received";
    public const string AttachEventOutcome = "attach.event.outcome";
    public const string MuxAgentStatusChanged = "pane.agent_status_changed";
    public const string JournalRefused = "journal.refused";
    public const string EventsQueue = "events.queue";
    public const string LogSinkDisabled = "log.sink.disabled";

    public const string SubsystemMux = "mux";
    public const string SubsystemApi = "api";
    public const string SubsystemAttach = "attach";
    public const string SubsystemTab = "tab";
    public const string SubsystemOverlay = "overlay";
    public const string SubsystemSidebar = "sidebar";
    public const string SubsystemCubes = "cubes";
    public const string SubsystemSettings = "settings";
    public const string SubsystemEvents = "events";
    public const string SubsystemLog = "log";

    public const string OutcomeStarted = "started";
    public const string OutcomeCompleted = "completed";
    public const string OutcomeOk = "ok";
    public const string OutcomeError = "error";
    public const string OutcomeRejected = "rejected";
    public const string OutcomeDisabled = "disabled";
    public const string OutcomeApplied = "applied";
    public const string OutcomeRecomposed = "recomposed";
    public const string OutcomePainted = "painted";
    public const string OutcomeCoalesced = "coalesced";
    public const string OutcomeDropped = "dropped";
    public const string OutcomeOpened = "opened";
    public const string OutcomeClosed = "closed";
}
