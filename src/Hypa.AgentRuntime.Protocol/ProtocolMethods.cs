namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Stable method names for the hypa-runtime control-plane protocol.
/// P0 inventory matches design §8.3 / Appendix B.6.
/// </summary>
public static class ProtocolMethods
{
    public const string RuntimeHealth = "runtime.health";
    public const string SessionSnapshot = "session.snapshot";
    public const string EventsSubscribe = "events.subscribe";
    public const string EventsWait = "events.wait";
    public const string EventsUnsubscribe = "events.unsubscribe";
    public const string RuntimeLeaseClaim = "runtime.lease.claim";
    public const string RuntimeLeaseRelease = "runtime.lease.release";
    public const string RuntimeLeaseRenew = "runtime.lease.renew";
    public const string TerminalObserve = "terminal.observe";
    public const string TerminalControl = "terminal.control";
    public const string TerminalVisibleSet = "terminal.visible_set";
    public const string PaneSendKeys = "pane.send_keys";
    public const string RuntimeBindingSet = "runtime.binding.set";
    public const string RuntimeBindingGet = "runtime.binding.get";
    public const string AgentWait = "agent.wait";
    public const string AgentPrompt = "agent.prompt";

    /// <summary>
    /// Atomic-facing export cursor acknowledgement. Does not mutate Run/Step state.
    /// </summary>
    public const string EventsExportAck = "events.export.ack";

    /// <summary>
    /// Establish a quiescent journal barrier and freeze the session for transfer.
    /// Not used for local P0 reattach (SQLite + HYJR only).
    /// </summary>
    public const string RuntimeCheckpointPrepare = "runtime.checkpoint.prepare";

    /// <summary>
    /// Materialize a transfer manifest + hashed artifacts under the checkpoint id.
    /// Atomic pulls the manifest; Hypa does not run transfer.
    /// </summary>
    public const string RuntimeCheckpointExport = "runtime.checkpoint.export";

    /// <summary>
    /// Abort a prepared checkpoint and unfreeze the session to <c>ready</c>.
    /// Also the recovery path after <c>checkpoint_conflict</c> if auto-unfreeze was skipped.
    /// </summary>
    public const string RuntimeCheckpointAbort = "runtime.checkpoint.abort";

    public const string Ping = "ping";
    public const string WorkspaceCreate = "workspace.create";
    public const string WorkspaceList = "workspace.list";
    public const string WorkspaceGet = "workspace.get";
    public const string WorkspaceFocus = "workspace.focus";
    public const string WorkspaceRename = "workspace.rename";
    public const string WorkspaceClose = "workspace.close";
    public const string WorkspaceMove = "workspace.move";
    public const string WorkspaceMoveBlock = "workspace.move_block";
    public const string WorkspaceReportMetadata = "workspace.report_metadata";
    public const string PaneCreate = "pane.create";
    public const string PaneList = "pane.list";
    public const string PaneGet = "pane.get";
    public const string PaneSendText = "pane.send_text";
    public const string PaneSendInput = "pane.send_input";
    public const string PaneResize = "pane.resize";
    public const string PaneRead = "pane.read";
    public const string PaneClose = "pane.close";
    public const string PaneWaitForOutput = "pane.wait_for_output";
    public const string AgentList = "agent.list";
    public const string AgentStatus = "agent.status";
    public const string AgentGet = "agent.get";
    public const string AgentRead = "agent.read";
    public const string AgentExplain = "agent.explain";
    public const string AgentRename = "agent.rename";
    public const string AgentFocus = "agent.focus";
    public const string AgentSendKeys = "agent.send_keys";
    public const string AgentViewSet = "agent.view.set";
    public const string AgentViewClear = "agent.view.clear";
    public const string RuntimeHandoffExport = "runtime.handoff.export";
    public const string RuntimeHandoffAdopt = "runtime.handoff.adopt";

    public const string TabCreate = "tab.create";
    public const string TabList = "tab.list";
    public const string TabGet = "tab.get";
    public const string TabFocus = "tab.focus";
    public const string TabRename = "tab.rename";
    public const string TabMove = "tab.move";
    public const string TabClose = "tab.close";
    public const string PaneSplit = "pane.split";
    public const string PaneMove = "pane.move";
    public const string PaneZoom = "pane.zoom";
    public const string PaneFocusDirection = "pane.focus_direction";
    public const string PaneLayout = "pane.layout";
    public const string LayoutExport = "layout.export";
    public const string LayoutApply = "layout.apply";
    public const string LayoutSetSplitRatio = "layout.set_split_ratio";
    public const string AgentStart = "agent.start";

    public const string PaneRename = "pane.rename";
    public const string PaneCurrent = "pane.current";
    public const string PaneNeighbor = "pane.neighbor";
    public const string PaneEdges = "pane.edges";
    public const string PaneProcessInfo = "pane.process_info";
    public const string PaneFocus = "pane.focus";
    public const string PaneInputSet = "pane.input.set";
    public const string PaneSwap = "pane.swap";

    /// <summary>Show a hidden pane as a tiled leaf. Overlay mode is reserved.</summary>
    public const string PaneShow = "pane.show";

    /// <summary>Hide a tiled pane without killing the PTY.</summary>
    public const string PaneHide = "pane.hide";

    /// <summary>Publish the attach client's actual UI mode.</summary>
    public const string UiClientMode = "ui.client_mode";

    /// <summary>Stop the named mux session after the RPC result is written.</summary>
    public const string ServerStop = "server.stop";

    /// <summary>
    /// Replace the remote mux process on Unix. This does not move Work.
    /// Other platforms fail closed.
    /// </summary>
    public const string ServerLiveHandoff = "server.live_handoff";

    /// <summary>Reload attach TOML. Reloadable UI applies. Existing panes stay.</summary>
    public const string ServerReloadConfig = "server.reload_config";

    /// <summary>Report active agent-detection manifest sources.</summary>
    public const string ServerAgentManifests = "server.agent_manifests";

    /// <summary>Reload the agent-detection manifest cache. Panes stay.</summary>
    public const string ServerReloadAgentManifests = "server.reload_agent_manifests";

    /// <summary>Show a core toast or a plugin notice when the grant is present.</summary>
    public const string NotificationShow = "notification.show";

    /// <summary>Set the server VT scroll origin and emit <c>pane.scroll_changed</c>.</summary>
    public const string PaneScroll = "pane.scroll";

    /// <summary>
    // Attach client method. Client sends
    /// pane id, cell, and generation. Mux resolves the URL from the live cell.
    /// </summary>
    public const string PaneLinkActivate = "pane.link.activate";

    /// <summary>Report semantic agent state that owns waits, notify, and rollups.</summary>
    public const string PaneReportAgent = "pane.report_agent";

    /// <summary>Store a native agent-session reference without changing waits.</summary>
    public const string PaneReportAgentSession = "pane.report_agent_session";

    public const string PluginLink = "plugin.link";
    public const string PluginList = "plugin.list";
    public const string PluginUnlink = "plugin.unlink";
    public const string PluginEnable = "plugin.enable";
    public const string PluginDisable = "plugin.disable";
    public const string PluginActionList = "plugin.action.list";
    public const string PluginActionInvoke = "plugin.action.invoke";
    public const string PluginLogList = "plugin.log.list";
    public const string PluginPaneOpen = "plugin.pane.open";
    public const string PluginPaneFocus = "plugin.pane.focus";
    public const string PluginPaneClose = "plugin.pane.close";
    public const string PluginPaneSendText = "plugin.pane.send_text";
    public const string PluginResourceList = "plugin.resource.list";
    public const string PluginResourceGet = "plugin.resource.get";
    public const string PluginResourcePublish = "plugin.resource.publish";
    public const string PluginResourceRemove = "plugin.resource.remove";
    public const string PluginConfigGet = "plugin.config.get";
    public const string PluginConfigSet = "plugin.config.set";

    /// <summary>Install official agent hooks and extensions.</summary>
    public const string IntegrationInstall = "integration.install";

    /// <summary>Revert official agent hooks and extensions.</summary>
    public const string IntegrationUninstall = "integration.uninstall";

    /// <summary>List official integration install status and versions.</summary>
    public const string IntegrationList = "integration.list";

    /// <summary>Report pane display tokens. Same contract as workspace.report_metadata.</summary>
    public const string PaneReportMetadata = "pane.report_metadata";

    /// <summary>End matching source+agent authority and resume the detector.</summary>
    public const string PaneReleaseAgent = "pane.release_agent";

    /// <summary>Clear pane authority. A non-owner source fails closed with -32005.</summary>
    public const string PaneClearAgentAuthority = "pane.clear_agent_authority";

    /// <summary>
    /// Store host OSC 10/11 RGB, OSC 4 palette, and appearance, then apply to pane VT.
    /// </summary>
    public const string ClientHostThemeSet = "client.host_theme.set";

    /// <summary>Set an in-memory session window-title override.</summary>
    public const string ClientWindowTitleSet = "client.window_title.set";

    /// <summary>Clear the in-memory session window-title override.</summary>
    public const string ClientWindowTitleClear = "client.window_title.clear";

    // / <summary>Close the session-modal popup. Message <c>popup_not_open</c> when none exists.</summary>
    public const string PopupClose = "popup.close";

    /// <summary>
    // / Open a session-modal popup PTY.
    /// Same thin-client rationale as <c>pane.create</c>. Result has no <c>pane_id</c>.
    /// </summary>
    public const string PopupOpen = "popup.open";

    /// <summary>Write key bytes to the popup PTY. Hypa-additive. No <c>pane_id</c>.</summary>
    public const string PopupSendKeys = "popup.send_keys";

    /// <summary>Recompute clamped popup geometry from a new content area. Hypa-additive.</summary>
    public const string PopupResize = "popup.resize";

    public const string WorktreeList = "worktree.list";
    public const string WorktreeCreate = "worktree.create";
    public const string WorktreeOpen = "worktree.open";
    public const string WorktreeRemove = "worktree.remove";

    /// <summary>Read the mux share listener state.</summary>
    public const string CubeShareStatus = "cube.share.status";

    /// <summary>Enable share. The mux runs the listener until <c>cube.share.stop</c>, across mux restarts.</summary>
    public const string CubeShareStart = "cube.share.start";

    /// <summary>Disable share and stop the listener.</summary>
    public const string CubeShareStop = "cube.share.stop";

    /// <summary>
    /// Complete P0 method inventory. Every entry must have request+response fixtures.
    /// G1 checkpoint methods are intentionally absent from P0.
    /// </summary>
    public static IReadOnlyList<string> P0 { get; } =
    [
        RuntimeHealth,
        SessionSnapshot,
        EventsSubscribe,
        EventsUnsubscribe,
        RuntimeLeaseClaim,
        RuntimeLeaseRelease,
        RuntimeLeaseRenew,
        TerminalObserve,
        TerminalControl,
        PaneSendKeys,
        RuntimeBindingSet,
        RuntimeBindingGet,
        AgentWait,
        AgentPrompt,
    ];

    /// <summary>Method inventory (fixtures required; not P0 baseline).</summary>
    public static IReadOnlyList<string> H07 { get; } =
    [
        EventsExportAck,
    ];

    /// <summary>G1 checkpoint method inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> H10 { get; } =
    [
        RuntimeCheckpointPrepare,
        RuntimeCheckpointExport,
        RuntimeCheckpointAbort,
    ];

    /// <summary>Live methods that require fixtures (wait + handoff).</summary>
    public static IReadOnlyList<string> H23 { get; } =
    [
        PaneWaitForOutput,
        RuntimeHandoffExport,
        RuntimeHandoffAdopt,
    ];

    /// <summary>One-shot <c>events.wait</c> inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> EventWait { get; } =
    [
        EventsWait,
    ];

    /// <summary>Workspace focus / rename / close inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> H37 { get; } =
    [
        WorkspaceFocus,
        WorkspaceRename,
        WorkspaceClose,
    ];

    public static IReadOnlyList<string> H54 { get; } =
    [
        WorkspaceMove,
        WorkspaceMoveBlock,
        WorkspaceReportMetadata,
    ];

    /// <summary>Agent authority inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> H57 { get; } =
    [
        PaneReportAgent,
        PaneReportAgentSession,
        PaneReportMetadata,
        PaneReleaseAgent,
        PaneClearAgentAuthority,
    ];

    /// <summary>Official agent integration inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> OfficialIntegrations { get; } =
    [
        IntegrationInstall,
        IntegrationUninstall,
        IntegrationList,
    ];

    /// <summary>Local plugin host inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> Plugins { get; } =
    [
        PluginLink,
        PluginList,
        PluginUnlink,
        PluginEnable,
        PluginDisable,
        PluginActionList,
        PluginActionInvoke,
        PluginLogList,
        PluginPaneOpen,
        PluginPaneFocus,
        PluginPaneClose,
        PluginPaneSendText,
        PluginResourceList,
        PluginResourceGet,
        PluginResourcePublish,
        PluginResourceRemove,
        PluginConfigGet,
        PluginConfigSet,
    ];

    /// <summary>Pane extras inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> H53 { get; } =
    [
        PaneRename,
        PaneCurrent,
        PaneNeighbor,
        PaneEdges,
        PaneProcessInfo,
        PaneFocus,
        PaneSendInput,
        PaneInputSet,
    ];

    /// <summary>Attach / session-stop inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> H33 { get; } =
    [
        ServerStop,
    ];

    /// <summary>Remote server replacement inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> ServerLiveHandoffMethods { get; } =
    [
        ServerLiveHandoff,
    ];

    /// <summary>Live config reload inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> H52 { get; } =
    [
        ServerReloadConfig,
    ];

    /// <summary>Agent-detection manifest inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> AgentManifests { get; } =
    [
        ServerAgentManifests,
        ServerReloadAgentManifests,
    ];

    /// <summary>
    /// Agent explain / rename / focus / send_keys / view inventory (fixtures required; not P0).
    /// There is no <c>agent.attach</c> wire method.
    /// </summary>
    public static IReadOnlyList<string> AgentExplainView { get; } =
    [
        AgentExplain,
        AgentRename,
        AgentFocus,
        AgentSendKeys,
        AgentViewSet,
        AgentViewClear,
    ];

    /// <summary>Layout chrome / same-tab swap inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> H36 { get; } =
    [
        PaneSwap,
    ];

    /// <summary>Tiled show and hide inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> PaneVisibility { get; } =
    [
        PaneShow,
        PaneHide,
    ];

    /// <summary>Notification / scroll inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> H41 { get; } =
    [
        NotificationShow,
        PaneScroll,
    ];

    /// <summary>Attach cell-click link activate. Fixtures required. Not a plugin.* method.</summary>
    public static IReadOnlyList<string> PaneLink { get; } =
    [
        PaneLinkActivate,
    ];

    /// <summary>Window-title inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> H44 { get; } =
    [
        ClientWindowTitleSet,
        ClientWindowTitleClear,
    ];

    /// <summary>Host default-colour inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> ClientHostTheme { get; } =
    [
        ClientHostThemeSet,
    ];

    // / <summary>Popup inventory (fixtures required; not P0).
    public static IReadOnlyList<string> H56 { get; } =
    [
        PopupClose,
        PopupOpen,
        PopupSendKeys,
        PopupResize,
    ];

    /// <summary>Worktree method inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> Worktrees { get; } =
    [
        WorktreeList,
        WorktreeCreate,
        WorktreeOpen,
        WorktreeRemove,
    ];

    /// <summary>Mux share listener inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> CubeShare { get; } =
    [
        CubeShareStatus,
        CubeShareStart,
        CubeShareStop,
    ];

    /// <summary>Tab / layout / occupant-start inventory (fixtures required; not P0).</summary>
    public static IReadOnlyList<string> H12 { get; } =
    [
        TabCreate,
        TabList,
        TabGet,
        TabFocus,
        TabRename,
        TabMove,
        TabClose,
        PaneSplit,
        PaneMove,
        PaneZoom,
        PaneFocusDirection,
        PaneLayout,
        LayoutExport,
        LayoutApply,
        LayoutSetSplitRatio,
        AgentStart,
    ];

    public const string AttachHello = AttachEndpointProtocol.Hello;
    public const string AttachResize = AttachEndpointProtocol.Resize;
    public const string AttachSurfaceInterest = AttachEndpointProtocol.SurfaceInterest;
    public const string AttachFocus = AttachEndpointProtocol.Focus;
    public const string AttachHealth = AttachEndpointProtocol.Health;
    public const string AttachPresentationSync = AttachEndpointProtocol.PresentationSyncEvent;
    public const string AttachPresentationReady = AttachEndpointProtocol.PresentationReadyEvent;

    /// <summary>Attach endpoint method inventory (fixtures required).</summary>
    public static IReadOnlyList<string> AttachEndpoint { get; } =
    [
        AttachHello,
        AttachResize,
        AttachSurfaceInterest,
        AttachFocus,
        AttachHealth,
        AttachPresentationSync,
        AttachPresentationReady,
    ];

    /// <summary>
    /// Union of every live control-plane method. Registry keys equal this list.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Ping,
        SessionSnapshot,
        WorkspaceCreate,
        WorkspaceList,
        WorkspaceGet,
        WorkspaceFocus,
        WorkspaceRename,
        WorkspaceClose,
        WorkspaceMove,
        WorkspaceMoveBlock,
        WorkspaceReportMetadata,
        PaneCreate,
        PaneList,
        PaneGet,
        PaneSendText,
        PaneSendInput,
        PaneSendKeys,
        PaneResize,
        PaneRead,
        PaneClose,
        PaneWaitForOutput,
        AgentList,
        AgentStatus,
        AgentGet,
        AgentWait,
        AgentPrompt,
        AgentRead,
        AgentExplain,
        AgentRename,
        AgentFocus,
        AgentSendKeys,
        AgentViewSet,
        AgentViewClear,
        EventsSubscribe,
        EventsWait,
        EventsUnsubscribe,
        RuntimeHealth,
        RuntimeLeaseClaim,
        RuntimeLeaseRelease,
        RuntimeLeaseRenew,
        TerminalObserve,
        TerminalControl,
        TerminalVisibleSet,
        RuntimeBindingSet,
        RuntimeBindingGet,
        EventsExportAck,
        RuntimeCheckpointPrepare,
        RuntimeCheckpointExport,
        RuntimeCheckpointAbort,
        RuntimeHandoffExport,
        RuntimeHandoffAdopt,
        TabCreate,
        TabList,
        TabGet,
        TabFocus,
        TabRename,
        TabMove,
        TabClose,
        PaneSplit,
        PaneMove,
        PaneZoom,
        PaneFocusDirection,
        PaneLayout,
        LayoutExport,
        LayoutApply,
        LayoutSetSplitRatio,
        AgentStart,
        PaneRename,
        PaneCurrent,
        PaneNeighbor,
        PaneEdges,
        PaneProcessInfo,
        PaneFocus,
        PaneInputSet,
        PaneReportAgent,
        PaneReportAgentSession,
        PaneReportMetadata,
        PaneReleaseAgent,
        PaneClearAgentAuthority,
        PaneSwap,
        ServerStop,
        ServerLiveHandoff,
        ServerReloadConfig,
        ServerAgentManifests,
        ServerReloadAgentManifests,
        NotificationShow,
        PaneScroll,
        PaneLinkActivate,
        ClientWindowTitleSet,
        ClientWindowTitleClear,
        ClientHostThemeSet,
        PopupClose,
        PopupOpen,
        PopupSendKeys,
        PopupResize,
        WorktreeList,
        WorktreeCreate,
        WorktreeOpen,
        WorktreeRemove,
        CubeShareStatus,
        CubeShareStart,
        CubeShareStop,
        PaneShow,
        PaneHide,
        UiClientMode,
        IntegrationInstall,
        IntegrationUninstall,
        IntegrationList,
        PluginLink,
        PluginList,
        PluginUnlink,
        PluginEnable,
        PluginDisable,
        PluginActionList,
        PluginActionInvoke,
        PluginLogList,
        PluginPaneOpen,
        PluginPaneFocus,
        PluginPaneClose,
        PluginPaneSendText,
        PluginResourceList,
        PluginResourceGet,
        PluginResourcePublish,
        PluginResourceRemove,
        PluginConfigGet,
        PluginConfigSet,
        AttachHello,
        AttachResize,
        AttachSurfaceInterest,
        AttachFocus,
        AttachHealth,
    ];
}
