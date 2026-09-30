using System.Reflection;
using System.Text;

namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Embedded golden fixture inventory for P0 methods and events.
/// Atomic and tests load fixtures without server binaries.
/// </summary>
public static class FixtureCatalog
{
    private const string ResourcePrefix = "Hypa.AgentRuntime.Protocol.Fixtures.";

    /// <summary>P0 method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> P0Methods => ProtocolMethods.P0;

    /// <summary>P0 event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> P0EventTypes => ProtocolEventTypes.P0;

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H07Methods => ProtocolMethods.H07;

    /// <summary>Event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> H07EventTypes => ProtocolEventTypes.H07;

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H10Methods => ProtocolMethods.H10;

    /// <summary>Event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> H10EventTypes => ProtocolEventTypes.H10;

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H12Methods => ProtocolMethods.H12;

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H37Methods => ProtocolMethods.H37;

    /// <summary>Workspace ordering and metadata methods.</summary>
    public static IReadOnlyList<string> H54Methods => ProtocolMethods.H54;

    /// <summary>Agent authority methods.</summary>
    public static IReadOnlyList<string> H57Methods => ProtocolMethods.H57;

    /// <summary>Official agent integration methods that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> OfficialIntegrationMethods => ProtocolMethods.OfficialIntegrations;

    /// <summary>Local plugin host methods that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> PluginMethods => ProtocolMethods.Plugins;

    /// <summary>Plugin resource event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> PluginResourceEventTypes => ProtocolEventTypes.PluginResources;

    /// <summary>Plugin config event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> PluginConfigEventTypes => ProtocolEventTypes.PluginConfig;

    /// <summary>One-shot <c>events.wait</c> methods that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> EventWaitMethods => ProtocolMethods.EventWait;

    /// <summary>Ready collection get result.</summary>
    public const string PluginResourceGetReadyResponse =
        "methods/plugin.resource.get.ready.response.json";

    /// <summary>Stale collection get result.</summary>
    public const string PluginResourceGetStaleResponse =
        "methods/plugin.resource.get.stale.response.json";

    /// <summary>Unavailable collection get result.</summary>
    public const string PluginResourceGetUnavailableResponse =
        "methods/plugin.resource.get.unavailable.response.json";

    /// <summary>Malformed publish error envelope.</summary>
    public const string PluginResourcePublishMalformed =
        "errors/plugin.resource.malformed.json";

    /// <summary>Denied publish error envelope.</summary>
    public const string PluginResourcePublishDenied =
        "errors/plugin.resource.denied.json";

    /// <summary>Full publish error envelope.</summary>
    public const string PluginResourcePublishFull =
        "errors/plugin.resource.full.json";

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H53Methods => ProtocolMethods.H53;

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H33Methods => ProtocolMethods.H33;

    /// <summary>Remote server replacement method names that must have fixtures.</summary>
    public static IReadOnlyList<string> ServerLiveHandoffMethods =>
        ProtocolMethods.ServerLiveHandoffMethods;

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H36Methods => ProtocolMethods.H36;

    /// <summary>Tiled show and hide methods that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> PaneVisibilityMethods => ProtocolMethods.PaneVisibility;

    /// <summary>Tiled show and hide event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> PaneVisibilityEventTypes => ProtocolEventTypes.PaneVisibility;

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H41Methods => ProtocolMethods.H41;

    /// <summary>Attach cell-click link activate methods that must have fixtures.</summary>
    public static IReadOnlyList<string> PaneLinkMethods => ProtocolMethods.PaneLink;

    /// <summary>Event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> H41EventTypes => ProtocolEventTypes.H41;

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H44Methods => ProtocolMethods.H44;

    /// <summary>Host default-colour method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> ClientHostThemeMethods => ProtocolMethods.ClientHostTheme;

    /// <summary>Event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> H44EventTypes => ProtocolEventTypes.H44;

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H56Methods => ProtocolMethods.H56;

    /// <summary>Event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> H56EventTypes => ProtocolEventTypes.H56;

    /// <summary>Method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> H52Methods => ProtocolMethods.H52;

    /// <summary>Agent-detection manifest method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> AgentManifestsMethods => ProtocolMethods.AgentManifests;

    /// <summary>Agent explain / rename / focus / send_keys / view methods.</summary>
    public static IReadOnlyList<string> AgentExplainViewMethods => ProtocolMethods.AgentExplainView;

    /// <summary>Event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> H52EventTypes => ProtocolEventTypes.H52;

    /// <summary>Worktree method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> WorktreeMethods => ProtocolMethods.Worktrees;

    /// <summary>Worktree event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> WorktreeEventTypes => ProtocolEventTypes.Worktrees;

    /// <summary>Attach endpoint method names that must have request+response fixtures.</summary>
    public static IReadOnlyList<string> AttachEndpointMethods => ProtocolMethods.AttachEndpoint;

    /// <summary>Attach endpoint event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> AttachEndpointEventTypes => ProtocolEventTypes.AttachEndpoint;

    /// <summary>Settings and overlay stream event types that must have fixtures.</summary>
    public static IReadOnlyList<string> SettingsOverlayEventTypes => ProtocolEventTypes.SettingsOverlay;

    /// <summary>Failed <c>server.reload_config</c> success envelope. Status is failed.</summary>
    public const string ServerReloadConfigFailedResponse =
        "methods/server.reload_config.failed.response.json";

    /// <summary>Live <c>terminal.render</c> with <c>target=popup</c>. Not a P0 event type.</summary>
    public const string TerminalRenderPopupEvent = "events/terminal.render.popup.json";

    /// <summary>Event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> H12EventTypes => ProtocolEventTypes.H12;

    /// <summary>Event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> H25EventTypes => ProtocolEventTypes.H25;

    /// <summary>Live workspace metadata event types.</summary>
    public static IReadOnlyList<string> H54EventTypes => ProtocolEventTypes.H54;

    /// <summary>Live pane metadata event types.</summary>
    public static IReadOnlyList<string> H57EventTypes => ProtocolEventTypes.H57;

    /// <summary>Pane bell event types that must have event fixtures.</summary>
    public static IReadOnlyList<string> H91EventTypes => ProtocolEventTypes.H91;

    /// <summary>G1 event types. Prefer <see cref="H10EventTypes"/>.</summary>
    public static IReadOnlyList<string> G1ExcludedEventTypes => ProtocolEventTypes.G1Excluded;

    /// <summary>Relative path of the B.3 subscribe/observe transcript.</summary>
    public const string SubscribeObserveTranscript = "transcripts/subscribe_observe.ndjson";

    /// <summary>Relative path of the subscribe/observe render transcript.</summary>
    public const string SubscribeObserveRenderTranscript = "transcripts/subscribe_observe_render.ndjson";

    /// <summary>Attach snapshot <c>terminal.render</c> event fixture. Not a P0 event type.</summary>
    public const string TerminalRenderSnapshotEvent = "events/terminal.render.snapshot.json";

    /// <summary>Live cells payload. The encoder writes this JSON.</summary>
    public const string TerminalRenderCellsPayload = "render/terminal.render.cells.json";

    /// <summary>Live snapshot payload. The snapshot packer writes this JSON.</summary>
    public const string TerminalRenderSnapshotPayload = "render/terminal.render.snapshot.json";

    /// <summary>Synthetic legacy coverage. No production path writes this JSON body.</summary>
    public const string TerminalRenderBlitPayload = "render/terminal.render.blit.json";

    /// <summary>Live cursor object from the cells packer.</summary>
    public const string TerminalRenderCursorPayload = "render/terminal.render.cursor.json";

    /// <summary>Popup snapshot payload. The packer writes <c>target</c> and omits <c>pane_id</c>.</summary>
    public const string TerminalRenderPopupPayload = "render/terminal.render.popup.json";

    /// <summary>Relative path of the parse_error golden fixture.</summary>
    public const string ParseErrorFixture = "errors/parse_error.json";

    /// <summary>
    /// Live ControlPlane <c>session.snapshot</c> key set (B.5 current).
    /// <c>protocol_version</c> is a JSON number matching <see cref="ProtocolVersion.Current"/>.
    /// </summary>
    public const string SessionSnapshotLiveKeys = "compatibility/session.snapshot.live_keys.json";

    /// <summary>Live ControlPlane <c>agent.get</c> result key set (B.5 current).</summary>
    public const string AgentGetLiveKeys = "compatibility/agent.get.response.json";

    /// <summary>Live ControlPlane pane object key set (B.5 current).</summary>
    public const string PaneObjectLiveKeys = "compatibility/pane.object.json";

    /// <summary>Live ControlPlane <c>runtime.lease.claim</c> granted result keys.</summary>
    public const string LeaseClaimGrantedLiveKeys = "compatibility/runtime.lease.claim.granted.live_keys.json";

    /// <summary>Live ControlPlane <c>pane.send_keys</c> result keys.</summary>
    public const string PaneSendKeysLiveKeys = "compatibility/pane.send_keys.live_keys.json";

    /// <summary>Live ControlPlane <c>terminal.observe</c> result keys.</summary>
    public const string TerminalObserveLiveKeys = "compatibility/terminal.observe.live_keys.json";

    /// <summary>Live ControlPlane <c>terminal.control</c> result keys.</summary>
    public const string TerminalControlLiveKeys = "compatibility/terminal.control.live_keys.json";

    /// <summary>Live ControlPlane <c>tab.create</c> result keys. Includes pane when create_pane is true.</summary>
    public const string TabCreateLiveKeys = "compatibility/tab.create.live_keys.json";

    /// <summary>Live ControlPlane <c>tab.get</c> / <c>tab.focus</c> / <c>tab.list</c> item keys.</summary>
    public const string TabGetLiveKeys = "compatibility/tab.get.live_keys.json";

    /// <summary>Live ControlPlane workspace object keys.</summary>
    public const string WorkspaceObjectLiveKeys = "compatibility/workspace.object.live_keys.json";

    /// <summary>Live ControlPlane <c>layout.export</c> result keys.</summary>
    public const string LayoutExportLiveKeys = "compatibility/layout.export.live_keys.json";

    /// <summary>Live ControlPlane <c>pane.neighbor</c> result keys.</summary>
    public const string PaneNeighborLiveKeys = "compatibility/pane.neighbor.live_keys.json";

    /// <summary>Live ControlPlane <c>pane.swap</c> result keys.</summary>
    public const string PaneSwapLiveKeys = "compatibility/pane.swap.live_keys.json";

    /// <summary>Live ControlPlane <c>pane.edges</c> result keys.</summary>
    public const string PaneEdgesLiveKeys = "compatibility/pane.edges.live_keys.json";

    /// <summary>Live ControlPlane <c>pane.process_info</c> result keys.</summary>
    public const string PaneProcessInfoLiveKeys = "compatibility/pane.process_info.live_keys.json";

    /// <summary>
    /// <c>pane.process_info</c> when the occupant/runtime is absent.
    /// <c>shell_pid</c> and <c>foreground_process_group_id</c> are JSON null.
    /// </summary>
    public const string PaneProcessInfoAbsentResponse = "methods/pane.process_info.absent.response.json";

    /// <summary>Live ControlPlane <c>pane.send_input</c> result keys.</summary>
    public const string PaneSendInputLiveKeys = "compatibility/pane.send_input.live_keys.json";

    /// <summary>Combined <c>text</c> + <c>keys</c> request. Text is written first.</summary>
    public const string PaneSendInputCombinedRequest = "methods/pane.send_input.combined.request.json";

    /// <summary>Live ControlPlane <c>pane.input.set</c> result keys.</summary>
    public const string PaneInputSetLiveKeys = "compatibility/pane.input.set.live_keys.json";

    /// <summary>Restore / default <c>pane.input.set</c> request. <c>right_click</c> is <c>hypa</c>.</summary>
    public const string PaneInputSetHypaRequest = "methods/pane.input.set.hypa.request.json";

    /// <summary>Restore / default <c>pane.input.set</c> result. <c>right_click</c> is <c>hypa</c>.</summary>
    public const string PaneInputSetHypaResponse = "methods/pane.input.set.hypa.response.json";

    /// <summary>Live <c>pane.move</c> when the source tab is zoomed (reason=zoomed_tab).</summary>
    public const string PaneMoveZoomedTabLiveKeys = "compatibility/pane.move.zoomed_tab.live_keys.json";

    /// <summary>Live <c>pane.zoom</c> on a one-pane tab (reason=single_pane).</summary>
    public const string PaneZoomSinglePaneLiveKeys = "compatibility/pane.zoom.single_pane.live_keys.json";

    /// <summary>
    /// <c>pane.lifecycle</c> action=moved envelope (not the P0 state/occupant_generation shape).
    /// </summary>
    public const string PaneLifecycleMoved = "events/pane.lifecycle.moved.json";

    /// <summary>
    /// <c>workspace.lifecycle</c> action=focused (P0 golden stays state-only).
    /// </summary>
    public const string WorkspaceLifecycleFocused = "events/workspace.lifecycle.focused.json";

    /// <summary>
    /// <c>workspace.lifecycle</c> action=renamed (P0 golden stays state-only).
    /// </summary>
    public const string WorkspaceLifecycleRenamed = "events/workspace.lifecycle.renamed.json";

    /// <summary>
    /// <c>workspace.lifecycle</c> action=closed (state=closed, not ready).
    /// </summary>
    public const string WorkspaceLifecycleClosed = "events/workspace.lifecycle.closed.json";

    /// <summary><c>workspace.lifecycle</c> action=moved.</summary>
    public const string WorkspaceLifecycleMoved = "events/workspace.lifecycle.moved.json";

    /// <summary><c>workspace.lifecycle</c> action=reordered.</summary>
    public const string WorkspaceLifecycleReordered = "events/workspace.lifecycle.reordered.json";

    /// <summary>
    /// Deferred-policy sample: <c>runtime.lease.claim</c> pending_approval outcome
    /// (not emitted by F1 InMemoryLeaseRegistry auto-grant).
    /// </summary>
    public const string LeaseClaimPendingApprovalSample =
        "methods/runtime.lease.claim.response.pending_approval.json";

    /// <summary>
    /// Machine-readable <see cref="ProtocolVersion"/> consumer matrix.
    /// Live and typed DTO share JSON number major.
    /// </summary>
    public const string ProtocolVersionMatrix = "compatibility/protocol_version_matrix.json";

    public const string AttachEndpointHelloCompatible =
        "compatibility/attach.endpoint.hello.compatible.json";

    public const string AttachEndpointHelloIndependentMinor =
        "compatibility/attach.endpoint.hello.independent_minor.json";

    public const string AttachEndpointDisplayUnknown =
        "compatibility/attach.endpoint.display.unknown.json";

    public const string AttachEndpointWelcomeCompatible =
        "compatibility/attach.endpoint.welcome.compatible.json";

    public const string AttachEndpointHelloMissingCapabilities =
        "compatibility/attach.endpoint.hello.missing_capabilities.json";

    public const string AttachEndpointHelloUnknownOptional =
        "compatibility/attach.endpoint.hello.unknown_optional.json";

    public const string AttachEndpointHelloReconnectGeneration =
        "compatibility/attach.endpoint.hello.reconnect_generation.json";

    public const string AttachEndpointHelloUnknownControl =
        "compatibility/attach.endpoint.hello.unknown_control.json";

    public const string AttachEndpointHelloMissingClientId =
        "compatibility/attach.endpoint.hello.missing_required.json";

    public const string AttachEndpointIncompatibleError =
        "errors/attach.endpoint.incompatible.json";

    public const string AttachEndpointMissingCapabilitiesError =
        "errors/attach.endpoint.missing_capabilities.json";

    public const string EventsWaitUnsupportedError =
        "errors/events.wait.unsupported.json";

    public const string PaneReadRecentUnwrappedRequest =
        "methods/pane.read.request.json";

    public const string PaneReadRecentUnwrappedResponse =
        "methods/pane.read.response.json";

    /// <summary>
    /// Wire kind for <c>session.snapshot.protocol_version</c> / <c>ping.protocol</c> / DTO.
    /// </summary>
    public const string LiveProtocolVersionKind = "number";

    /// <summary>
    /// Same as live (unified number major; wave0 Codex P1).
    /// </summary>
    public const string TargetProtocolVersionKind = "number";

    /// <summary>Request fixture relative path for a P0 method.</summary>
    public static string MethodRequestPath(string method) =>
        $"methods/{method}.request.json";

    /// <summary>Response fixture relative path for a P0 method.</summary>
    public static string MethodResponsePath(string method) =>
        $"methods/{method}.response.json";

    /// <summary>Event fixture relative path for a P0 event type.</summary>
    public static string EventPath(string eventType) =>
        $"events/{eventType}.json";

    /// <summary>
    /// Load an embedded fixture by relative path under <c>Fixtures/</c>
    /// (e.g. <c>methods/runtime.health.request.json</c>).
    /// </summary>
    public static string Load(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var resourceName = ToResourceName(relativePath);
        var assembly = typeof(FixtureCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            var available = string.Join(", ", assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal));
            throw new FileNotFoundException(
                $"Embedded fixture not found: '{relativePath}' (resource '{resourceName}'). Available: {available}");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>True when the embedded resource exists.</summary>
    public static bool Exists(string relativePath)
    {
        var resourceName = ToResourceName(relativePath);
        var assembly = typeof(FixtureCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        return stream is not null;
    }

    /// <summary>
    /// Manifest resource name for a Fixtures-relative path.
    /// Matches SDK default: RootNamespace + path with / and \ → .
    /// </summary>
    public static string ToResourceName(string relativePath)
    {
        var normalized = relativePath
            .Replace('\\', '/')
            .TrimStart('/');
        if (normalized.StartsWith("Fixtures/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["Fixtures/".Length..];

        return ResourcePrefix + normalized.Replace('/', '.');
    }

    /// <summary>Enumerate all embedded resource names under Fixtures.</summary>
    public static IReadOnlyList<string> ListResourceNames()
    {
        return typeof(FixtureCatalog).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }
}
