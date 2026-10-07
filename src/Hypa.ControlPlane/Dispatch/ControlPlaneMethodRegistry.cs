using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane.Dispatch;

/// <summary>
/// Live method table. Keys equal <see cref="ProtocolMethods.All"/>.
/// Dispatch checks the advertised capability set before invoke.
/// </summary>
public sealed class ControlPlaneMethodRegistry
{
    private readonly Dictionary<string, IControlPlaneMethod> _methods =
        new(StringComparer.Ordinal);

    public ControlPlaneMethodRegistry(ControlPlaneService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        Add(ProtocolMethods.Ping, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandlePingAsync(p, ct));
        Add(ProtocolMethods.ServerStop, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleServerStopAsync(p, ct));
        Add(ProtocolMethods.ServerLiveHandoff, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleServerLiveHandoffAsync(p, ct));
        Add(ProtocolMethods.ServerReloadConfig, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleServerReloadConfigAsync(p, ct));
        Add(ProtocolMethods.ServerAgentManifests, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleServerAgentManifestsAsync(p, ct));
        Add(ProtocolMethods.ServerReloadAgentManifests, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleServerReloadAgentManifestsAsync(p, ct));
        Add(ProtocolMethods.SessionSnapshot, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, conn, ct) => service.HandleSessionSnapshotAsync(p, conn, ct));
        Add(ProtocolMethods.WorkspaceCreate, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorkspaceCreateParams,
            (p, conn, ct) => service.HandleWorkspaceCreateAsync(p, conn, ct));
        Add(ProtocolMethods.WorkspaceList, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleWorkspaceListAsync(p, ct));
        Add(ProtocolMethods.WorkspaceGet, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorkspaceGetParams,
            (p, _, ct) => service.HandleWorkspaceGetAsync(p, ct));
        Add(ProtocolMethods.WorkspaceFocus, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorkspaceFocusParams,
            (p, conn, ct) => service.HandleWorkspaceFocusAsync(p, conn, ct));
        Add(ProtocolMethods.WorkspaceRename, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorkspaceRenameParams,
            (p, conn, ct) => service.HandleWorkspaceRenameAsync(p, conn, ct));
        Add(ProtocolMethods.WorkspaceClose, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorkspaceCloseParams,
            (p, conn, ct) => service.HandleWorkspaceCloseAsync(p, conn, ct));
        Add(ProtocolMethods.WorkspaceMove, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorkspaceMoveParams,
            (p, conn, ct) => service.HandleWorkspaceMoveAsync(p, conn, ct));
        Add(ProtocolMethods.WorkspaceMoveBlock, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorkspaceMoveBlockParams,
            (p, conn, ct) => service.HandleWorkspaceMoveBlockAsync(p, conn, ct));
        Add(ProtocolMethods.WorkspaceReportMetadata, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorkspaceReportMetadataParams,
            (p, _, ct) => service.HandleWorkspaceReportMetadataAsync(p, ct));
        Add(ProtocolMethods.PaneCreate, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneCreateParams,
            (p, conn, ct) => service.HandlePaneCreateAsync(p, conn, ct));
        Add(ProtocolMethods.PaneList, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandlePaneListAsync(p, ct));
        Add(ProtocolMethods.PaneGet, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneGetParams,
            (p, _, ct) => service.HandlePaneGetAsync(p, ct));
        Add(ProtocolMethods.PaneSendText, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.PaneSendTextParams,
            (p, conn, ct) => service.HandlePaneSendTextAsync(p, conn, ct));
        Add(ProtocolMethods.PaneSendInput, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.PaneSendInputParams,
            (p, conn, ct) => service.HandlePaneSendInputAsync(p, conn, ct));
        Add(ProtocolMethods.PaneSendKeys, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.PaneSendKeysParams,
            (p, conn, ct) => service.HandlePaneSendKeysAsync(p, conn, ct));
        Add(ProtocolMethods.PaneResize, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.PaneResizeParams,
            (p, conn, ct) => service.HandlePaneResizeAsync(p, conn, ct));
        Add(ProtocolMethods.PaneRead, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.PaneReadParams,
            (p, _, ct) => service.HandlePaneReadAsync(p, ct));
        Add(ProtocolMethods.PaneClose, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneCloseParams,
            (p, conn, ct) => service.HandlePaneCloseAsync(p, conn, ct));
        Add(ProtocolMethods.PaneWaitForOutput, ProtocolCapabilities.AgentWait,
            ProtocolJsonContext.Default.PaneWaitForOutputParams,
            (p, _, ct) => service.HandlePaneWaitForOutputAsync(p, ct));
        Add(ProtocolMethods.AgentList, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleAgentListAsync(p, ct));
        Add(ProtocolMethods.AgentStatus, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneGetParams,
            (p, _, ct) => service.HandleAgentGetAsync(p, ct));
        Add(ProtocolMethods.AgentGet, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneGetParams,
            (p, _, ct) => service.HandleAgentGetAsync(p, ct));
        Add(ProtocolMethods.AgentWait, ProtocolCapabilities.AgentWait,
            ProtocolJsonContext.Default.AgentWaitParams,
            (p, _, ct) => service.HandleAgentWaitAsync(p, ct));
        Add(ProtocolMethods.AgentPrompt, ProtocolCapabilities.AgentWait,
            ProtocolJsonContext.Default.AgentPromptParams,
            (p, conn, ct) => service.HandleAgentPromptAsync(p, conn, ct));
        Add(ProtocolMethods.AgentRead, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneReadParams,
            (p, _, ct) => service.HandleAgentReadAsync(p, ct));
        Add(ProtocolMethods.AgentExplain, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.AgentTargetParams,
            (p, _, ct) => service.HandleAgentExplainAsync(p, ct));
        Add(ProtocolMethods.AgentRename, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.AgentRenameParams,
            (p, conn, ct) => service.HandleAgentRenameAsync(p, conn, ct));
        Add(ProtocolMethods.AgentFocus, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.AgentTargetParams,
            (p, conn, ct) => service.HandleAgentFocusAsync(p, conn, ct));
        Add(ProtocolMethods.AgentSendKeys, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.AgentSendKeysParams,
            (p, conn, ct) => service.HandleAgentSendKeysAsync(p, conn, ct));
        Add(ProtocolMethods.AgentViewSet, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.AgentViewSetParams,
            (p, _, ct) => service.HandleAgentViewSetAsync(p, ct));
        Add(ProtocolMethods.AgentViewClear, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.AgentViewClearParams,
            (p, _, ct) => service.HandleAgentViewClearAsync(p, ct));
        Add(ProtocolMethods.EventsSubscribe, ProtocolCapabilities.Events,
            ProtocolJsonContext.Default.EventsSubscribeParams,
            (p, conn, ct) => service.HandleEventsSubscribeAsync(p, conn, ct));
        Add(ProtocolMethods.EventsWait, ProtocolCapabilities.Events,
            ProtocolJsonContext.Default.EventsWaitParams,
            (p, _, ct) => service.HandleEventsWaitAsync(p, ct));
        Add(ProtocolMethods.EventsUnsubscribe, ProtocolCapabilities.Events,
            ProtocolJsonContext.Default.EventsUnsubscribeParams,
            (p, conn, ct) => service.HandleEventsUnsubscribeAsync(p, conn, ct));
        Add(ProtocolMethods.RuntimeHealth, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleRuntimeHealthAsync(p, ct));
        Add(ProtocolMethods.RuntimeLeaseClaim, ProtocolCapabilities.Leases,
            ProtocolJsonContext.Default.LeaseClaimParams,
            (p, conn, ct) => service.HandleLeaseClaimAsync(p, conn, ct));
        Add(ProtocolMethods.RuntimeLeaseRelease, ProtocolCapabilities.Leases,
            ProtocolJsonContext.Default.LeaseReleaseParams,
            (p, conn, ct) => service.HandleLeaseReleaseAsync(p, conn, ct));
        Add(ProtocolMethods.RuntimeLeaseRenew, ProtocolCapabilities.Leases,
            ProtocolJsonContext.Default.LeaseRenewParams,
            (p, conn, ct) => service.HandleLeaseRenewAsync(p, conn, ct));
        Add(ProtocolMethods.TerminalObserve, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.TerminalObserveParams,
            (p, conn, ct) => service.HandleTerminalObserveAsync(p, conn, ct));
        Add(ProtocolMethods.TerminalVisibleSet, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.TerminalVisibleSetParams,
            (p, conn, ct) => service.HandleTerminalVisibleSetAsync(p, conn, ct));
        Add(ProtocolMethods.TerminalControl, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.TerminalControlParams,
            (p, conn, ct) => service.HandleTerminalControlAsync(p, conn, ct));
        Add(ProtocolMethods.RuntimeBindingSet, ProtocolCapabilities.Binding,
            ProtocolJsonContext.Default.BindingSetParams,
            (p, _, ct) => service.HandleBindingSetAsync(p, ct));
        Add(ProtocolMethods.RuntimeBindingGet, ProtocolCapabilities.Binding,
            ProtocolJsonContext.Default.BindingGetParams,
            (p, _, ct) => service.HandleBindingGetAsync(p, ct));
        Add(ProtocolMethods.EventsExportAck, ProtocolCapabilities.Events,
            ProtocolJsonContext.Default.ExportAckParams,
            (p, _, ct) => service.HandleExportAckAsync(p, ct));
        Add(ProtocolMethods.RuntimeCheckpointPrepare, ProtocolCapabilities.Checkpoint,
            ProtocolJsonContext.Default.CheckpointPrepareParams,
            (p, _, ct) => service.HandleCheckpointPrepareAsync(p, ct));
        Add(ProtocolMethods.RuntimeCheckpointExport, ProtocolCapabilities.Checkpoint,
            ProtocolJsonContext.Default.CheckpointExportParams,
            (p, _, ct) => service.HandleCheckpointExportAsync(p, ct));
        Add(ProtocolMethods.RuntimeCheckpointAbort, ProtocolCapabilities.Checkpoint,
            ProtocolJsonContext.Default.CheckpointAbortParams,
            (p, _, ct) => service.HandleCheckpointAbortAsync(p, ct));
        Add(ProtocolMethods.RuntimeHandoffExport, ProtocolCapabilities.Handoff,
            ProtocolJsonContext.Default.HandoffExportParams,
            (p, conn, ct) => service.HandleHandoffExportAsync(p, conn, ct));
        Add(ProtocolMethods.RuntimeHandoffAdopt, ProtocolCapabilities.Handoff,
            ProtocolJsonContext.Default.HandoffAdoptParams,
            (p, conn, ct) => service.HandleHandoffAdoptAsync(p, conn, ct));
        Add(ProtocolMethods.TabCreate, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.TabCreateParams,
            (p, conn, ct) => service.HandleTabCreateAsync(p, conn, ct));
        Add(ProtocolMethods.TabList, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.TabListParams,
            (p, _, ct) => service.HandleTabListAsync(p, ct));
        Add(ProtocolMethods.TabGet, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.TabGetParams,
            (p, _, ct) => service.HandleTabGetAsync(p, ct));
        Add(ProtocolMethods.TabFocus, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.TabFocusParams,
            (p, conn, ct) => service.HandleTabFocusAsync(p, conn, ct));
        Add(ProtocolMethods.TabRename, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.TabRenameParams,
            (p, conn, ct) => service.HandleTabRenameAsync(p, conn, ct));
        Add(ProtocolMethods.TabMove, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.TabMoveParams,
            (p, conn, ct) => service.HandleTabMoveAsync(p, conn, ct));
        Add(ProtocolMethods.TabClose, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.TabCloseParams,
            (p, conn, ct) => service.HandleTabCloseAsync(p, conn, ct));
        Add(ProtocolMethods.PaneSplit, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneSplitParams,
            (p, conn, ct) => service.HandlePaneSplitAsync(p, conn, ct));
        Add(ProtocolMethods.PaneMove, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneMoveParams,
            (p, conn, ct) => service.HandlePaneMoveAsync(p, conn, ct));
        Add(ProtocolMethods.PaneZoom, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneZoomParams,
            (p, conn, ct) => service.HandlePaneZoomAsync(p, conn, ct));
        Add(ProtocolMethods.PaneFocusDirection, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneFocusDirectionParams,
            (p, conn, ct) => service.HandlePaneFocusDirectionAsync(p, conn, ct));
        Add(ProtocolMethods.PaneLayout, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneLayoutParams,
            (p, _, ct) => service.HandlePaneLayoutAsync(p, ct));
        Add(ProtocolMethods.LayoutExport, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.LayoutExportParams,
            (p, _, ct) => service.HandleLayoutExportAsync(p, ct));
        Add(ProtocolMethods.LayoutApply, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.LayoutApplyParams,
            (p, conn, ct) => service.HandleLayoutApplyAsync(p, conn, ct));
        Add(ProtocolMethods.LayoutSetSplitRatio, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.LayoutSetSplitRatioParams,
            (p, conn, ct) => service.HandleLayoutSetSplitRatioAsync(p, conn, ct));
        Add(ProtocolMethods.AgentStart, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.AgentStartParams,
            (p, conn, ct) => service.HandleAgentStartAsync(p, conn, ct));
        Add(ProtocolMethods.PaneRename, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneRenameParams,
            (p, conn, ct) => service.HandlePaneRenameAsync(p, conn, ct));
        Add(ProtocolMethods.PaneCurrent, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneCurrentParams,
            (p, _, ct) => service.HandlePaneCurrentAsync(p, ct));
        Add(ProtocolMethods.PaneFocus, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneFocusParams,
            (p, conn, ct) => service.HandlePaneFocusAsync(p, conn, ct));
        Add(ProtocolMethods.PaneNeighbor, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneNeighborParams,
            (p, _, ct) => service.HandlePaneNeighborAsync(p, ct));
        Add(ProtocolMethods.PaneEdges, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneEdgesParams,
            (p, _, ct) => service.HandlePaneEdgesAsync(p, ct));
        Add(ProtocolMethods.PaneProcessInfo, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.PaneProcessInfoParams,
            (p, _, ct) => service.HandlePaneProcessInfoAsync(p, ct));
        Add(ProtocolMethods.PaneInputSet, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneInputSetParams,
            (p, conn, ct) => service.HandlePaneInputSetAsync(p, conn, ct));
        Add(ProtocolMethods.PaneReportAgent, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneReportAgentParams,
            (p, _, ct) => service.HandlePaneReportAgentAsync(p, ct));
        Add(ProtocolMethods.PaneReportAgentSession, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneReportAgentSessionParams,
            (p, _, ct) => service.HandlePaneReportAgentSessionAsync(p, ct));
        Add(ProtocolMethods.PaneReportMetadata, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneReportMetadataParams,
            (p, _, ct) => service.HandlePaneReportMetadataAsync(p, ct));
        Add(ProtocolMethods.PaneReleaseAgent, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneReleaseAgentParams,
            (p, _, ct) => service.HandlePaneReleaseAgentAsync(p, ct));
        Add(ProtocolMethods.PaneClearAgentAuthority, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PaneClearAgentAuthorityParams,
            (p, _, ct) => service.HandlePaneClearAgentAuthorityAsync(p, ct));
        Add(ProtocolMethods.PaneSwap, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneSwapParams,
            (p, conn, ct) => service.HandlePaneSwapAsync(p, conn, ct));
        Add(ProtocolMethods.PaneShow, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneShowParams,
            (p, conn, ct) => service.HandlePaneShowAsync(p, conn, ct));
        Add(ProtocolMethods.PaneHide, ProtocolCapabilities.Layout,
            ProtocolJsonContext.Default.PaneHideParams,
            (p, conn, ct) => service.HandlePaneHideAsync(p, conn, ct));
        Add(ProtocolMethods.UiClientMode, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.UiClientModeParams,
            (p, conn, ct) => service.HandleUiClientModeAsync(p, conn, ct));
        Add(ProtocolMethods.NotificationShow, ProtocolCapabilities.Notification,
            ProtocolJsonContext.Default.NotificationShowParams,
            (p, conn, ct) => service.HandleNotificationShowAsync(p, conn, ct));
        Add(ProtocolMethods.PaneScroll, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.PaneScrollParams,
            (p, _, ct) => service.HandlePaneScrollAsync(p, ct));
        Add(ProtocolMethods.PaneLinkActivate, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.PaneLinkActivateParams,
            (p, _, ct) => service.HandlePaneLinkActivateAsync(p, ct));
        Add(ProtocolMethods.ClientWindowTitleSet, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WindowTitleSetParams,
            (p, _, ct) => service.HandleWindowTitleSetAsync(p, ct));
        Add(ProtocolMethods.ClientWindowTitleClear, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleWindowTitleClearAsync(p, ct));
        Add(ProtocolMethods.ClientHostThemeSet, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.HostThemeSetParams,
            (p, _, ct) => service.HandleClientHostThemeSetAsync(p, ct));
        Add(ProtocolMethods.PopupClose, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, conn, ct) => service.HandlePopupCloseAsync(p, conn, ct));
        Add(ProtocolMethods.PopupOpen, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PopupOpenParams,
            (p, conn, ct) => service.HandlePopupOpenAsync(p, conn, ct));
        Add(ProtocolMethods.PopupSendKeys, ProtocolCapabilities.Terminal,
            ProtocolJsonContext.Default.PopupSendKeysParams,
            (p, conn, ct) => service.HandlePopupSendKeysAsync(p, conn, ct));
        Add(ProtocolMethods.PopupResize, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PopupResizeParams,
            (p, conn, ct) => service.HandlePopupResizeAsync(p, conn, ct));
        Add(ProtocolMethods.WorktreeList, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorktreeListParams,
            (p, _, ct) => service.HandleWorktreeListAsync(p, ct));
        Add(ProtocolMethods.WorktreeCreate, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorktreeCreateParams,
            (p, conn, ct) => service.HandleWorktreeCreateAsync(p, conn, ct));
        Add(ProtocolMethods.WorktreeOpen, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorktreeOpenParams,
            (p, conn, ct) => service.HandleWorktreeOpenAsync(p, conn, ct));
        Add(ProtocolMethods.WorktreeRemove, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.WorktreeRemoveParams,
            (p, conn, ct) => service.HandleWorktreeRemoveAsync(p, conn, ct));
        Add(ProtocolMethods.CubeShareStatus, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleCubeShareStatusAsync(p, ct));
        Add(ProtocolMethods.CubeShareStart, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.CubeShareStartParams,
            (p, _, ct) => service.HandleCubeShareStartAsync(p, ct));
        Add(ProtocolMethods.CubeShareStop, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleCubeShareStopAsync(p, ct));
        Add(ProtocolMethods.IntegrationList, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.EmptyParams,
            (p, _, ct) => service.HandleIntegrationListAsync(p, ct));
        Add(ProtocolMethods.IntegrationInstall, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.IntegrationTargetParams,
            (p, _, ct) => service.HandleIntegrationInstallAsync(p, ct));
        Add(ProtocolMethods.IntegrationUninstall, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.IntegrationTargetParams,
            (p, _, ct) => service.HandleIntegrationUninstallAsync(p, ct));
        Add(ProtocolMethods.PluginLink, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginLinkParams,
            (p, _, ct) => service.HandlePluginLinkAsync(p, ct));
        Add(ProtocolMethods.PluginList, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginListParams,
            (p, _, ct) => service.HandlePluginListAsync(p, ct));
        Add(ProtocolMethods.PluginUnlink, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginUnlinkParams,
            (p, _, ct) => service.HandlePluginUnlinkAsync(p, ct));
        Add(ProtocolMethods.PluginEnable, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginSetEnabledParams,
            (p, _, ct) => service.HandlePluginEnableAsync(p, ct));
        Add(ProtocolMethods.PluginDisable, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginSetEnabledParams,
            (p, _, ct) => service.HandlePluginDisableAsync(p, ct));
        Add(ProtocolMethods.PluginActionList, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginActionListParams,
            (p, _, ct) => service.HandlePluginActionListAsync(p, ct));
        Add(ProtocolMethods.PluginActionInvoke, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginActionInvokeParams,
            (p, _, ct) => service.HandlePluginActionInvokeAsync(p, ct));
        Add(ProtocolMethods.PluginLogList, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginLogListParams,
            (p, _, ct) => service.HandlePluginLogListAsync(p, ct));
        Add(ProtocolMethods.PluginPaneOpen, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginPaneOpenParams,
            (p, conn, ct) => service.HandlePluginPaneOpenAsync(p, conn, ct));
        Add(ProtocolMethods.PluginPaneFocus, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginPaneFocusParams,
            (p, _, ct) => service.HandlePluginPaneFocusAsync(p, ct));
        Add(ProtocolMethods.PluginPaneClose, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginPaneCloseParams,
            (p, _, ct) => service.HandlePluginPaneCloseAsync(p, ct));
        Add(ProtocolMethods.PluginPaneSendText, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginPaneSendTextParams,
            (p, _, ct) => service.HandlePluginPaneSendTextAsync(p, ct));
        Add(ProtocolMethods.PluginResourceList, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginResourceListParams,
            (p, _, ct) => service.HandlePluginResourceListAsync(p, ct));
        Add(ProtocolMethods.PluginResourceGet, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginResourceGetParams,
            (p, _, ct) => service.HandlePluginResourceGetAsync(p, ct));
        Add(ProtocolMethods.PluginResourcePublish, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginResourcePublishParams,
            (p, _, ct) => service.HandlePluginResourcePublishAsync(p, ct));
        Add(ProtocolMethods.PluginResourceRemove, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginResourceRemoveParams,
            (p, _, ct) => service.HandlePluginResourceRemoveAsync(p, ct));
        Add(ProtocolMethods.PluginConfigGet, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginConfigGetParams,
            (p, _, ct) => service.HandlePluginConfigGetAsync(p, ct));
        Add(ProtocolMethods.PluginConfigSet, ProtocolCapabilities.Core,
            ProtocolJsonContext.Default.PluginConfigSetParams,
            (p, _, ct) => service.HandlePluginConfigSetAsync(p, ct));
        Add(ProtocolMethods.AttachHello, ProtocolCapabilities.AttachEndpoint,
            ProtocolJsonContext.Default.AttachEndpointHello,
            (p, conn, ct) => service.HandleAttachHelloAsync(p, conn, ct));
        Add(ProtocolMethods.AttachResize, ProtocolCapabilities.AttachEndpoint,
            ProtocolJsonContext.Default.AttachResizeRequest,
            (p, conn, ct) => service.HandleAttachResizeAsync(p, conn, ct));
        Add(ProtocolMethods.AttachSurfaceInterest, ProtocolCapabilities.AttachEndpoint,
            ProtocolJsonContext.Default.AttachSurfaceInterestRequest,
            (p, conn, ct) => service.HandleAttachSurfaceInterestAsync(p, conn, ct));
        Add(ProtocolMethods.AttachFocus, ProtocolCapabilities.AttachEndpoint,
            ProtocolJsonContext.Default.AttachFocusRequest,
            (p, conn, ct) => service.HandleAttachFocusAsync(p, conn, ct));
        Add(ProtocolMethods.AttachHealth, ProtocolCapabilities.AttachEndpoint,
            ProtocolJsonContext.Default.AttachHealthRequest,
            (p, conn, ct) => service.HandleAttachHealthAsync(p, conn, ct));
        Add(ProtocolMethods.AttachPresentationSync, ProtocolCapabilities.AttachEndpoint,
            ProtocolJsonContext.Default.AttachPresentationSyncRequest,
            (p, conn, ct) => service.HandleAttachPresentationSyncAsync(p, conn, ct));
        Add(ProtocolMethods.AttachPresentationReady, ProtocolCapabilities.AttachEndpoint,
            ProtocolJsonContext.Default.AttachPresentationSyncRequest,
            (p, conn, ct) => service.HandleAttachPresentationReadyAsync(p, conn, ct));
    }

    public IReadOnlyCollection<string> MethodNames => _methods.Keys;

    public bool TryGet(string method, out IControlPlaneMethod entry) =>
        _methods.TryGetValue(method, out entry!);

    public async Task<JsonElement> DispatchAsync(
        string method,
        JsonElement? parameters,
        IClientConnection? connection,
        IReadOnlySet<string> advertised,
        CancellationToken ct)
    {
        if (method.StartsWith("atomic.", StringComparison.Ordinal))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.MethodNotFound,
                $"Method not found: {method} (Atomic domain methods are not implemented in Hypa)");
        }

        if (!_methods.TryGetValue(method, out var entry))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.MethodNotFound,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.MethodNotFound));
        }

        if (!advertised.Contains(entry.RequiredCapability))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.MethodNotFound,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.MethodNotFound));
        }

        return await entry.InvokeAsync(parameters, connection, ct).ConfigureAwait(false);
    }

    private void Add<TParams>(
        string name,
        string capability,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> typeInfo,
        Func<TParams, IClientConnection?, CancellationToken, Task<JsonElement>> handler)
        where TParams : class
    {
        _methods[name] = new TypedControlPlaneMethod<TParams>(name, capability, typeInfo, handler);
    }
}
