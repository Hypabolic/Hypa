namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// P0 runtime event type tokens carried in <c>runtime.event</c> envelopes
/// under <c>params.type</c>. G1 checkpoint/export types are not in P0.
/// </summary>
public static class ProtocolEventTypes
{
    /// <summary>Outer event envelope name for all P0 runtime events.</summary>
    public const string RuntimeEvent = "runtime.event";

    public const string SessionLifecycle = "session.lifecycle";
    public const string WorkspaceLifecycle = "workspace.lifecycle";

    /// <summary>
    /// Live-only workspace display-token map. Never journaled.
    /// Class is Lifecycle. Reliability is Reliable.
    /// </summary>
    public const string WorkspaceMetadataUpdated = "workspace.metadata_updated";

    /// <summary>
    /// Live-only pane display-token map. Never journaled.
    /// Class is Lifecycle. Reliability is Reliable.
    /// </summary>
    public const string PaneMetadataUpdated = "pane.metadata_updated";
    public const string ResourceChanged = "resource.changed";
    public const string ConfigChanged = "config.changed";
    public const string PaneLifecycle = "pane.lifecycle";
    public const string OccupantLifecycle = "occupant.lifecycle";
    public const string LeaseChanged = "lease.changed";
    public const string BindingChanged = "binding.changed";
    public const string TerminalOutput = "terminal.output";

    /// <summary>Reliable pane resource event for Ground-state BEL bytes.</summary>
    public const string PaneBell = "pane.bell";

    /// <summary>Live-only human attach stream. Never journaled.</summary>
    public const string TerminalRender = "terminal.render";

    /// <summary>Export cursor ack.</summary>
    public const string ExportAcked = "export.acked";

    /// <summary>Checkpoint prepare/export lifecycle.</summary>
    public const string CheckpointLifecycle = "checkpoint.lifecycle";

    /// <summary>Tab create/focus/rename/move/close.</summary>
    public const string TabLifecycle = "tab.lifecycle";

    /// <summary>Committed occupant status/kind/message change.</summary>
    public const string PaneAgentStatusChanged = "pane.agent_status_changed";

    /// <summary>Server VT scroll origin or length change.</summary>
    public const string PaneScrollChanged = "pane.scroll_changed";

    /// <summary>Layout snapshot after split/swap/zoom/move/ratio/apply.</summary>
    public const string LayoutUpdated = "layout.updated";

    /// <summary>Pane moved between tiled and hidden placement.</summary>
    public const string PanePlacementChanged = "pane.placement_changed";

    /// <summary>Successful <c>pane.wait_for_output</c> match.</summary>
    public const string PaneOutputMatched = "pane.output_matched";

    /// <summary>Fanout of a shown <c>notification.show</c>.</summary>
    public const string NotificationShown = "notification.shown";

    /// <summary>In-memory window-title override changed.</summary>
    public const string ClientWindowTitleChanged = "client.window_title.changed";

    /// <summary>Session-modal popup opened or closed. Never carries <c>pane_id</c>.</summary>
    public const string PopupLifecycle = "popup.lifecycle";

    /// <summary>
    /// Live-only config reload result. Never journaled.
    /// Class is Control. Reliability is Reliable.
    /// </summary>
    public const string ConfigReloaded = "config.reloaded";

    public const string WorktreeCreated = "worktree.created";
    public const string WorktreeOpened = "worktree.opened";
    public const string WorktreeRemoved = "worktree.removed";

    /// <summary>
    /// Live-only input admission fault. Never journaled.
    /// Class is Control. Reliability is Reliable.
    /// </summary>
    public const string PaneInputRejected = "pane.input_rejected";

    /// <summary>
    /// Committed settings change. Lifecycle. Replays. Not
    /// <see cref="ConfigReloaded"/>.
    /// </summary>
    public const string SettingsChanged = "settings.changed";

    /// <summary>
    /// Overlay open or close. Lifecycle. Replays. Never carries
    /// <c>pane_id</c>.
    /// </summary>
    public const string OverlayLifecycle = "overlay.lifecycle";

    /// <summary>Live <c>terminal.render</c> payload target for the popup overlay.</summary>
    public const string TerminalRenderTargetPopup = "popup";

    /// <summary>
    /// Complete P0 event-type inventory. Every entry must have a golden fixture.
    /// </summary>
    public static IReadOnlyList<string> P0 { get; } =
    [
        SessionLifecycle,
        WorkspaceLifecycle,
        PaneLifecycle,
        OccupantLifecycle,
        LeaseChanged,
        BindingChanged,
        TerminalOutput,
    ];

    /// <summary>Event types. Fixtures required; not part of baseline.</summary>
    public static IReadOnlyList<string> H07 { get; } =
    [
        ExportAcked,
    ];

    /// <summary>G1 event types. Fixtures required; not part of baseline.</summary>
    public static IReadOnlyList<string> H10 { get; } =
    [
        CheckpointLifecycle,
    ];

    /// <summary>Tab event types. Fixtures required; not part of baseline.</summary>
    public static IReadOnlyList<string> H12 { get; } =
    [
        TabLifecycle,
    ];

    /// <summary>Live metadata event. Fixtures required; not P0. Never journaled.</summary>
    public static IReadOnlyList<string> H54 { get; } =
    [
        WorkspaceMetadataUpdated,
    ];

    /// <summary>Live pane metadata event. Fixtures required; not P0. Never journaled.</summary>
    public static IReadOnlyList<string> H57 { get; } =
    [
        PaneMetadataUpdated,
    ];

    /// <summary>Live plugin resource event. Fixtures required; not P0. Never journaled.</summary>
    public static IReadOnlyList<string> PluginResources { get; } =
    [
        ResourceChanged,
    ];

    /// <summary>Live plugin config event. Fixtures required; not P0. Never journaled.</summary>
    public static IReadOnlyList<string> PluginConfig { get; } =
    [
        ConfigChanged,
    ];

    /// <summary>Live render types. Fixtures required; do not change the P0 count of 7.</summary>
    public static IReadOnlyList<string> H25 { get; } =
    [
        TerminalRender,
    ];

    /// <summary>Notify / layout / scroll types. Fixtures required; not P0.</summary>
    public static IReadOnlyList<string> H41 { get; } =
    [
        PaneAgentStatusChanged,
        PaneScrollChanged,
        LayoutUpdated,
        PaneOutputMatched,
        NotificationShown,
    ];

    /// <summary>Tiled show and hide event types. Fixtures required; not P0.</summary>
    public static IReadOnlyList<string> PaneVisibility { get; } =
    [
        PanePlacementChanged,
    ];

    /// <summary>Window-title types. Fixtures required; not P0.</summary>
    public static IReadOnlyList<string> H44 { get; } =
    [
        ClientWindowTitleChanged,
    ];

    /// <summary>Pane bell event. Fixtures required; not P0.</summary>
    public static IReadOnlyList<string> H91 { get; } =
    [
        PaneBell,
    ];

    /// <summary>Popup lifecycle. Fixtures required; not P0.</summary>
    public static IReadOnlyList<string> H56 { get; } =
    [
        PopupLifecycle,
    ];

    /// <summary>Live config reload event. Fixtures required; not P0. Never journaled.</summary>
    public static IReadOnlyList<string> H52 { get; } =
    [
        ConfigReloaded,
    ];

    /// <summary>Worktree lifecycle events. Fixtures required; not P0.</summary>
    public static IReadOnlyList<string> Worktrees { get; } =
    [
        WorktreeCreated,
        WorktreeOpened,
        WorktreeRemoved,
    ];

    /// <summary>Attach endpoint event inventory (fixtures required).</summary>
    public static IReadOnlyList<string> AttachEndpoint { get; } = AttachEndpointProtocol.Events;

    /// <summary>Committed settings and overlay stream types. Fixtures required.</summary>
    public static IReadOnlyList<string> SettingsOverlay { get; } =
    [
        SettingsChanged,
        OverlayLifecycle,
    ];

    /// <summary>
    /// Complete named inventory used for fail-closed class mapping.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        .. P0,
        WorkspaceMetadataUpdated,
        PaneMetadataUpdated,
        ResourceChanged,
        ConfigChanged,
        PaneBell,
        TerminalRender,
        .. H07,
        .. H10,
        .. H12,
        .. H41,
        .. PaneVisibility,
        .. H44,
        .. H91,
        .. H56,
        .. H52,
        PaneInputRejected,
        .. Worktrees,
        .. AttachEndpoint,
        .. SettingsOverlay,
    ];

    /// <summary>
    /// Additive named subscribe tokens. Class tokens still match every event
    /// of that class. Unknown tokens fail closed.
    /// </summary>
    public static bool IsNamedSubscribeToken(string token) =>
        token is PaneAgentStatusChanged
            or PaneScrollChanged
            or LayoutUpdated
            or PanePlacementChanged
            or PaneOutputMatched
            or NotificationShown
            or PaneBell
            or ClientWindowTitleChanged
            or PopupLifecycle
            or ConfigReloaded
            or PaneInputRejected
            or WorkspaceMetadataUpdated
            or PaneMetadataUpdated
            or ResourceChanged
            or ConfigChanged
            or WorktreeCreated
            or WorktreeOpened
            or WorktreeRemoved
            or TabLifecycle
            or WorkspaceLifecycle
            or SettingsChanged
            or OverlayLifecycle;

    /// <summary>
    /// Legacy alias for inventory tests that still enumerate G1-only event names.
    /// Prefer <see cref="H10"/>.
    /// </summary>
    public static IReadOnlyList<string> G1Excluded => H10;
}
