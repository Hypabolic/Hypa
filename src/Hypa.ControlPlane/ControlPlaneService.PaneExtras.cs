using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal async Task<JsonElement> HandlePaneRenameAsync(
        PaneRenameParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneRenameAsync(p, connection, ct).ConfigureAwait(false));

    internal Task<JsonElement> HandlePaneCurrentAsync(PaneCurrentParams p, CancellationToken ct) =>
        Task.FromResult(Ok(PaneCurrent(p)));

    internal async Task<JsonElement> HandlePaneFocusAsync(
        PaneFocusParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneFocusAsync(p, connection, ct).ConfigureAwait(false));

    internal Task<JsonElement> HandlePaneNeighborAsync(PaneNeighborParams p, CancellationToken ct) =>
        Task.FromResult(Ok(PaneNeighbor(p)));

    internal Task<JsonElement> HandlePaneEdgesAsync(PaneEdgesParams p, CancellationToken ct) =>
        Task.FromResult(Ok(PaneEdges(p)));

    internal Task<JsonElement> HandlePaneProcessInfoAsync(PaneProcessInfoParams p, CancellationToken ct) =>
        Task.FromResult(Ok(PaneProcessInfo(p)));

    internal async Task<JsonElement> HandlePaneSendInputAsync(
        PaneSendInputParams p, IClientConnection? connection, CancellationToken ct) =>
        Ok(await PaneSendInputAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneInputSetAsync(
        PaneInputSetParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneInputSetAsync(p, connection, ct).ConfigureAwait(false));

    private async Task<JsonObject> PaneRenameAsync(
        PaneRenameParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.rename");
        var id = RequireField(p.PaneId, "pane_id");
        var label = string.IsNullOrWhiteSpace(p.Label)
            ? string.Empty
            : RejectUnsafeLabel(p.Label)!;
        PaneState pane;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "pane.rename");
            EnsureNotFrozenForMutation("pane.rename");
            pane = _state.RenamePane(new PaneId(id), label)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        return PaneToJson(pane);
    }

    private JsonObject PaneCurrent(PaneCurrentParams p)
    {
        var caller = EmptyToNull(p.CallerPaneId);
        if (caller is not null)
        {
            var pane = _state.GetPane(new PaneId(caller))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {caller}");
            return PaneToJson(pane);
        }

        var focused = ResolveFocusedPane();
        return PaneToJson(focused);
    }

    private async Task<JsonObject> PaneFocusAsync(
        PaneFocusParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.focus");
        var id = RequireField(p.PaneId, "pane_id");
        PaneState pane;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "pane.focus");
            EnsureNotFrozenForMutation("pane.focus");
            var existing = _state.GetPane(new PaneId(id))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
            RejectHiddenPane(existing, "pane.focus");
            pane = _state.FocusPane(existing.Id)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
            // Focus always marks seen. Done promotes to idle under the same
            // gate so PersistGraphAsync writes idle+seen, not done+seen.
            if (pane.AgentStatus == AgentStatus.Done || !pane.Seen)
            {
                pane = UpdatePaneEmittingStatus(pane.Id, current =>
                {
                    var nextStatus = HoldsSemanticAuthority(current)
                        || current.AgentStatus != AgentStatus.Done
                        ? current.AgentStatus
                        : AgentStatus.Idle;
                    return current with
                    {
                        AgentStatus = nextStatus,
                        Seen = true,
                        UpdatedAt = _time.GetUtcNow(),
                    };
                }) ?? pane;
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        RememberAttachClientSelection(
            connection,
            pane.WorkspaceId.Value,
            pane.TabId.Value,
            pane.Id.Value);
        await ClaimShellTabGeometryOnInteractAsync(connection, ct).ConfigureAwait(false);
        return PaneToJson(pane);
    }

    private JsonObject PaneNeighbor(PaneNeighborParams p)
    {
        var direction = RequireField(p.Direction, "direction");
        if (!LayoutNode.IsFocusDirection(direction))
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "direction must be right, down, left, or up");
        var pane = ResolveOptionalPane(p.PaneId);
        var tab = _state.GetTab(pane.TabId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {pane.TabId.Value}");
        var root = tab.LayoutRoot
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "tab has no layout");
        var neighbor = LayoutTreeOperations.Neighbor(root, pane.Id, direction);
        return new JsonObject
        {
            ["pane_id"] = pane.Id.Value,
            ["direction"] = direction,
            ["neighbor_pane_id"] = neighbor?.PaneId?.Value,
            ["layout"] = ExportLayout(tab.Id.Value, pane.Id.Value),
        };
    }

    private JsonObject PaneEdges(PaneEdgesParams p)
    {
        var pane = ResolveOptionalPane(p.PaneId);
        var tab = _state.GetTab(pane.TabId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {pane.TabId.Value}");
        var root = tab.LayoutRoot
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "tab has no layout");
        var (left, right, up, down) = LayoutTreeOperations.Edges(root, pane.Id);
        return new JsonObject
        {
            ["pane_id"] = pane.Id.Value,
            ["left"] = left,
            ["right"] = right,
            ["up"] = up,
            ["down"] = down,
            ["layout"] = ExportLayout(tab.Id.Value, pane.Id.Value),
        };
    }

    private JsonObject PaneProcessInfo(PaneProcessInfoParams p)
    {
        var pane = ResolveOptionalPane(p.PaneId);
        int? shellPid = null;
        if (TryGetRuntime(pane.Id.Value, out var runtime) && runtime is { IsAlive: true, Pid: int pid })
            shellPid = pid;

        PaneForegroundInfo? foreground = null;
        if (shellPid is int livePid)
            foreground = _processInfoProbe.TryGetForegroundInfo(livePid);

        var processes = new JsonArray();
        if (foreground is { Command.Length: > 0 } named)
        {
            processes.Add((JsonNode)new JsonObject
            {
                ["pid"] = named.Pid ?? named.GroupId,
                ["command"] = named.Command,
            });
        }

        return new JsonObject
        {
            ["pane_id"] = pane.Id.Value,
            ["shell_pid"] = shellPid,
            ["foreground_process_group_id"] = foreground?.GroupId,
            ["foreground_processes"] = processes,
        };
    }

    private async Task<JsonObject> PaneSendInputAsync(
        PaneSendInputParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.send_input");
        EnsureAttachPaneMutation(connection, "pane.send_input");
        var id = RequireField(p.PaneId, "pane_id");
        var keys = p.Keys ?? [];
        var text = p.Text ?? string.Empty;
        if (keys.Count == 0 && text.Length == 0)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "keys or text is required");
        }

        var leaseId = EmptyToNull(p.LeaseId);
        AuthorizeInput(id, leaseId, connection?.ConnectionId);
        EnsureWorkGenerationAllowsInput(id);

        var payload = new List<byte>();
        if (text.Length > 0)
            payload.AddRange(Encoding.UTF8.GetBytes(text));

        if (keys.Count > 0)
        {
            if (!_keyComboEncoder.TryEncode(keys, out var encoded, out _))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.InvalidParams));
            }

            payload.AddRange(encoded);
        }

        var bytes = payload.ToArray();
        if (InputBeforeBindingGateForTests is { } beforeGate)
            await beforeGate().ConfigureAwait(false);

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation("pane.send_input");
            EnsureWorkGenerationAllowsInput(id);
            var runtime = GetRuntime(id);
            await runtime.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["pane_id"] = id,
            ["accepted_bytes"] = bytes.Length,
        };
    }

    private async Task<JsonObject> PaneInputSetAsync(
        PaneInputSetParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.input.set");
        var id = RequireField(p.PaneId, "pane_id");
        var rightClick = RequireField(p.RightClick, "right_click");
        if (!PaneRightClick.IsValid(rightClick))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "right_click must be hypa or pane");
        }

        PaneState pane;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "pane.input.set");
            EnsureNotFrozenForMutation("pane.input.set");
            pane = _state.UpdatePane(new PaneId(id), current => current with { RightClick = rightClick })
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        return new JsonObject
        {
            ["ok"] = true,
            ["pane_id"] = pane.Id.Value,
            ["right_click"] = pane.RightClick,
        };
    }

    private PaneState ResolveOptionalPane(string? paneId)
    {
        var explicitId = EmptyToNull(paneId);
        if (explicitId is not null)
        {
            return _state.GetPane(new PaneId(explicitId))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {explicitId}");
        }

        return ResolveFocusedPane();
    }

    private PaneState ResolveFocusedPane()
    {
        var snap = _state.Snapshot();
        var ws = snap.FocusedWorkspaceId is { } fw && snap.Workspaces.TryGetValue(fw.Value, out var focused)
            ? focused
            : snap.Workspaces.Values.FirstOrDefault();
        if (ws is null)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "session/workspace/pane/occupant missing");
        var tabId = ws.FocusedTabId ?? ws.TabIds.FirstOrDefault();
        if (tabId.Value is null)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "session/workspace/pane/occupant missing");
        var tab = _state.GetTab(tabId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "session/workspace/pane/occupant missing");
        var paneId = tab.FocusedPaneId ?? tab.PaneIds.FirstOrDefault();
        if (paneId.Value is null)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "session/workspace/pane/occupant missing");
        return _state.GetPane(paneId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId.Value}");
    }
}
