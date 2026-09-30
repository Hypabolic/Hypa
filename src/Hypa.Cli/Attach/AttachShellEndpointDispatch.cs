using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>
/// A <c>ClientShellAction::Endpoint</c> is enqueued on the active endpoint when that
/// surface is available. It is not a PTY write and it does not kill the pane process.
/// active surface is unavailable. The caller cancels ids <c>send_next</c> returns.
/// The active endpoint is <c>AttachLiveState.ActiveProjectionEndpointId</c>
/// </summary>
internal static class AttachShellEndpointDispatch
{
    internal static Task<ControlPlaneCallResult> Route(
        AttachLiveState live,
        ControlPlaneClient client,
        string method,
        JsonObject? parameters,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(client);
        return ExecuteAsync(live, client, method, parameters, ct);
    }

    /// <summary>
    /// endpoint when its surface is available. Completion of an in-flight command
    /// </summary>
    internal static void DispatchSendNext(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var activeId = live.ActiveProjectionEndpointId;
        if (string.IsNullOrEmpty(activeId) || !live.GetEndpointSurfaceActive(activeId))
            return;

        var cancelled = live.EndpointCommands.SendNext(activeId, Accepts, Encode, TrySend);
        foreach (var cancelledId in cancelled)
            live.EndpointCommands.CancelEndpointRequest(activeId, cancelledId);
        return;

        bool Accepts(QueuedShellCommand queued) => GenerationAccepts(live, queued);
    }

    internal static bool SurfaceReady(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var endpointId = live.ActiveProjectionEndpointId;
        if (string.IsNullOrEmpty(endpointId))
            endpointId = live.ConnectedPlacementId ?? "local";
        return live.GetEndpointSurfaceActive(endpointId)
            && string.Equals(live.ActiveProjectionEndpointId, endpointId, StringComparison.Ordinal)
            && GenerationFor(live, endpointId) > 0
            && BootFor(live, endpointId).Length > 0;
    }

    internal static ulong GenerationFor(AttachLiveState live, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.Equals(endpointId, live.ConnectedPlacementId, StringComparison.Ordinal)
            || string.Equals(endpointId, "local", StringComparison.Ordinal))
            return live.SourceTransportEnvelope.Generation;
        return live.TransportEnvelope.Generation;
    }

    internal static string BootFor(AttachLiveState live, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.Equals(endpointId, live.ConnectedPlacementId, StringComparison.Ordinal)
            || string.Equals(endpointId, "local", StringComparison.Ordinal))
        {
            return live.SourceEndpointBootId is { Length: > 0 } source
                ? source
                : live.EndpointCommands.SnapshotBootId ?? string.Empty;
        }

        return live.ConnectedBootId is { Length: > 0 } connected
            ? connected
            : live.EndpointCommands.SnapshotBootId ?? string.Empty;
    }

    private static async Task<ControlPlaneCallResult> ExecuteAsync(
        AttachLiveState live,
        ControlPlaneClient client,
        string method,
        JsonObject? parameters,
        CancellationToken ct)
    {
        var wait = new TaskCompletionSource<ControlPlaneCallResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var endpointId = live.ActiveProjectionEndpointId;
        if (string.IsNullOrEmpty(endpointId))
            endpointId = live.ConnectedPlacementId ?? "local";
        var generation = GenerationFor(live, endpointId);
        var boot = BootFor(live, endpointId);
        var requestId = client.AllocateRpcId();
        var command = new QueuedShellCommand(
            endpointId,
            generation,
            boot,
            requestId,
            method,
            parameters?.DeepClone().AsObject(),
            client);
        live.EndpointCommands.BeginWait(
            command.EndpointId,
            command.Generation,
            command.BootId,
            command.RequestId,
            command.MethodName,
            client,
            wait);

        var activeSurface = SurfaceReady(live)
            && string.Equals(live.ActiveProjectionEndpointId, endpointId, StringComparison.Ordinal);
        if (activeSurface)
        {
            live.EndpointCommands.Enqueue(command);
            DispatchSendNext(live);
        }
        else
        {
            live.EndpointCommands.CancelEndpointRequest(endpointId, requestId);
        }

        return await wait.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private static bool GenerationAccepts(AttachLiveState live, QueuedShellCommand queued) =>
        GenerationFor(live, queued.EndpointId) == queued.Generation && queued.Generation > 0;

    private static bool Encode(QueuedShellCommand queued)
    {
        try
        {
            _ = ControlPlaneClient.FormatRpcRequest(queued.RequestId, queued.MethodName, queued.Parameters);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TrySend(QueuedShellCommand queued) =>
        queued.Client.TryWriteRpcRequest(queued.RequestId, queued.MethodName, queued.Parameters);
}

/// <summary>
/// <c>layout.set_split_ratio</c> is the pane-split drag
/// <c>pane.input.set</c> is the right-click target
/// <c>server.reload_config</c> is the reload key and the settings save
/// <c>src/client/shell/settings.rs:173-179</c>).
/// <c>integration.list</c> and <c>integration.install</c> are the settings
/// and <c>269-274</c>; <c>src/client/shell/worktrees.rs:553-555</c>).
/// Focus-revoke, surface interest, resize, and focus baseline stay on
/// Raw pane input stays <c>ClientMessage::Input</c>
/// </summary>
internal static class AttachShellEndpointMethods
{
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        ProtocolMethods.WorkspaceCreate,
        ProtocolMethods.WorkspaceFocus,
        ProtocolMethods.WorkspaceRename,
        ProtocolMethods.WorkspaceClose,
        ProtocolMethods.WorkspaceMove,
        ProtocolMethods.WorkspaceMoveBlock,
        ProtocolMethods.TabCreate,
        ProtocolMethods.TabFocus,
        ProtocolMethods.TabRename,
        ProtocolMethods.TabMove,
        ProtocolMethods.TabClose,
        ProtocolMethods.PaneCreate,
        ProtocolMethods.PaneClose,
        ProtocolMethods.PaneSplit,
        ProtocolMethods.PaneMove,
        ProtocolMethods.PaneZoom,
        ProtocolMethods.PaneFocus,
        ProtocolMethods.PaneFocusDirection,
        ProtocolMethods.PaneSwap,
        ProtocolMethods.PaneRename,
        ProtocolMethods.PaneInputSet,
        ProtocolMethods.PaneShow,
        ProtocolMethods.PaneHide,
        ProtocolMethods.PaneScroll,
        ProtocolMethods.PaneLinkActivate,
        ProtocolMethods.PaneLayout,
        ProtocolMethods.LayoutApply,
        ProtocolMethods.LayoutSetSplitRatio,
        ProtocolMethods.WorktreeCreate,
        ProtocolMethods.WorktreeOpen,
        ProtocolMethods.WorktreeRemove,
        ProtocolMethods.PluginActionInvoke,
        ProtocolMethods.ServerReloadConfig,
        ProtocolMethods.IntegrationList,
        ProtocolMethods.IntegrationInstall,
    };

    internal static bool IsEndpointAction(string method) => Actions.Contains(method);
}
