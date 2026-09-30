using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    /// <summary>
    /// Client does not send a URL. Mux reads the live packed cell.
    /// </summary>
    internal Task<JsonElement> HandlePaneLinkActivateAsync(PaneLinkActivateParams p, CancellationToken ct)
    {
        _ = ct;
        return Task.FromResult(PaneLinkActivate(p));
    }

    private JsonElement PaneLinkActivate(PaneLinkActivateParams p)
    {
        var paneId = RequireField(p.PaneId, "pane_id");
        if (p.ViewportRow is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "viewport_row is required");
        if (p.Col is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "col is required");

        var pane = _state.GetPane(new PaneId(paneId));
        if (pane is null)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
        if (!PaneVisibleOnActiveSurface(pane))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane is no longer visible");
        }

        if (!TryGetRuntime(paneId, out var runtime) || runtime is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.NotFound,
                "pane runtime not found");
        }

        var currentOffset = TryCurrentOffset(runtime);
        if (p.OffsetFromBottom is { } expectedOffsetBefore && currentOffset != expectedOffsetBefore)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane viewport changed before link activation");
        }

        var occupantGeneration = SnapshotOccupantGeneration(pane.Id);
        var contentRevision = CurrentAttachGeneration(paneId);
        if (p.Generation is { } expectedGeneration && expectedGeneration != contentRevision)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane content changed before link activation");
        }

        if (runtime is not IPaneVtSnapshot snap)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane feed unavailable");
        }

        if (!TryCaptureLinkActivationFrame(snap, out var frame, out var capturedFeedGeneration)
            || frame is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane feed unavailable");
        }

        if (frame.Modes.SynchronizedOutput)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane content changed before link activation");
        }

        if (_paneSnapshotEpoch.TryGetValue(paneId, out var epoch)
            && capturedFeedGeneration != epoch)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane content changed before link activation");
        }

        var url = EmptyToNull(frame.CellAt(p.ViewportRow.Value, p.Col.Value).Hyperlink);

        if (SnapshotOccupantGeneration(pane.Id) != occupantGeneration)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane content changed during link activation");
        }

        if (p.Generation is { } generationAfter && CurrentAttachGeneration(paneId) != generationAfter)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane content changed during link activation");
        }

        if (snap.FeedGeneration != capturedFeedGeneration)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane content changed during link activation");
        }

        if (p.OffsetFromBottom is { } expectedOffsetAfter && TryCurrentOffset(runtime) != expectedOffsetAfter)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane content or viewport changed during link activation");
        }

        if (CurrentAttachGeneration(paneId) != contentRevision
            || TryCurrentOffset(runtime) != currentOffset)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "pane content or viewport changed during link activation");
        }

        var handled = false;
        if (!string.IsNullOrEmpty(url))
        {
            var context = PluginContextForPane(pane, "link_click") with
            {
                InvocationSource = "link_click",
            };
            var started = _plugins.ActivateLink(url, context);
            if (!started.IsOk)
            {
                _logger.LogWarning(
                    "failed to invoke plugin link handler for {Url}: {Code}",
                    url,
                    started.Error.Code);
            }
            else
            {
                handled = started.Value;
            }
        }

        return OkTyped(
            new PaneLinkActivateResult { Url = url, Handled = handled },
            ProtocolJsonContext.Default.PaneLinkActivateResult);
    }

    /// <summary>
    /// </summary>
    private bool PaneVisibleOnActiveSurface(PaneState pane)
    {
        if (pane.Placement == PanePlacement.Hidden)
            return false;
        var snap = _state.Snapshot();
        if (snap.FocusedWorkspaceId is not { } focusedWorkspace
            || pane.WorkspaceId != focusedWorkspace)
        {
            return false;
        }

        if (!snap.Workspaces.TryGetValue(pane.WorkspaceId.Value, out var ws)
            || ws.FocusedTabId is not { } focusedTab
            || pane.TabId != focusedTab)
        {
            return false;
        }

        if (!snap.Tabs.TryGetValue(pane.TabId.Value, out var tab))
            return false;
        if (tab.Zoomed)
            return tab.FocusedPaneId?.Value == pane.Id.Value;
        foreach (var id in tab.PaneIds)
        {
            if (id.Value == pane.Id.Value)
                return true;
        }

        return false;
    }

    private long CurrentAttachGeneration(string paneId) =>
        _paneAttachSnapshotGeneration.TryGetValue(paneId, out var generation) ? generation : 0;

    private static long? TryCurrentOffset(IPaneRuntime runtime)
    {
        if (runtime is not IPaneVtSnapshot snap || !snap.TryGetScrollMetrics(out var offset, out _))
            return null;
        return offset;
    }

    /// <summary>
    /// Prefer attach frame, then live paint. Same order as prior link activation.
    /// </summary>
    private static bool TryCaptureLinkActivationFrame(
        IPaneVtSnapshot snap,
        out VtFrame? frame,
        out long feedGeneration)
    {
        if (snap.TryCaptureAttachFrame(out frame, out feedGeneration) && frame is not null)
            return true;
        if (snap.TryCaptureLivePaintFrame(out frame, out feedGeneration, out _) && frame is not null)
            return true;
        frame = null;
        feedGeneration = 0;
        return false;
    }

    internal PluginInvocationContext PluginContextForPane(PaneState pane, string correlationId)
    {
        var ws = _state.GetWorkspace(pane.WorkspaceId);
        var tab = _state.GetTab(pane.TabId);
        return new PluginInvocationContext
        {
            WorkspaceId = ws?.Id.Value ?? pane.WorkspaceId.Value,
            WorkspaceLabel = ws?.Label,
            WorkspaceCwd = ws?.Cwd ?? pane.Cwd,
            Worktree = PluginInvocationContextWorktree.FromMembership(ws?.Worktree),
            TabId = tab?.Id.Value ?? pane.TabId.Value,
            TabLabel = tab?.Label,
            FocusedPaneId = pane.Id.Value,
            FocusedPaneCwd = pane.Cwd,
            FocusedPaneAgent = pane.AgentKind ?? pane.AgentName,
            FocusedPaneStatus = pane.AgentStatus.ToString().ToLowerInvariant(),
            AgentSession = PluginInvocationContextSession.FromPane(pane),
            ProgramName = string.IsNullOrEmpty(pane.Command) ? null : pane.Command,
            SelectedText = null,
            InvocationSource = "api",
            CorrelationId = correlationId,
        };
    }
}
