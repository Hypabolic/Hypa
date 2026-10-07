using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Metadata;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal async Task<JsonElement> HandleTabCreateAsync(
        TabCreateParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await TabCreateAsync(p, connection, ct).ConfigureAwait(false));

    internal Task<JsonElement> HandleTabListAsync(TabListParams p, CancellationToken ct) =>
        Task.FromResult(Ok(TabList(p)));

    internal Task<JsonElement> HandleTabGetAsync(TabGetParams p, CancellationToken ct) =>
        Task.FromResult(Ok(TabGet(p)));

    internal async Task<JsonElement> HandleTabFocusAsync(
        TabFocusParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await TabFocusAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleTabRenameAsync(
        TabRenameParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await TabRenameAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleTabMoveAsync(
        TabMoveParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await TabMoveAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleTabCloseAsync(
        TabCloseParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await TabCloseAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneSplitAsync(
        PaneSplitParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneSplitAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneMoveAsync(
        PaneMoveParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneMoveAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneZoomAsync(
        PaneZoomParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneZoomAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneFocusDirectionAsync(
        PaneFocusDirectionParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneFocusDirectionAsync(p, connection, ct).ConfigureAwait(false));

    internal Task<JsonElement> HandlePaneLayoutAsync(PaneLayoutParams p, CancellationToken ct) =>
        Task.FromResult(Ok(ExportLayout(p.TabId, p.PaneId)));

    internal Task<JsonElement> HandleLayoutExportAsync(LayoutExportParams p, CancellationToken ct) =>
        Task.FromResult(Ok(ExportLayout(p.TabId, p.PaneId)));

    internal async Task<JsonElement> HandleLayoutApplyAsync(
        LayoutApplyParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await LayoutApplyAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleLayoutSetSplitRatioAsync(
        LayoutSetSplitRatioParams p, IClientConnection? connection, CancellationToken ct) =>
        Ok(await LayoutSetSplitRatioAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneSwapAsync(
        PaneSwapParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneSwapAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleAgentStartAsync(
        AgentStartParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        OkTyped(await AgentStartAsync(p, connection, ct).ConfigureAwait(false), ProtocolJsonContext.Default.AgentStartResult);

    private async Task<JsonObject> TabCreateAsync(
        TabCreateParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("tab.create");
        var workspaceId = RequireField(p.WorkspaceId, "workspace_id");
        var createPane = p.CreatePane ?? true;
        var focus = p.Focus ?? true;

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        TabState tab;
        WorkspaceState ws;
        TabId? priorFocusedTabId;
        try
        {
            EnsureAttachPaneMutation(connection, "tab.create");
            EnsureNotFrozenForMutation("tab.create");
            ws = _state.GetWorkspace(new WorkspaceId(workspaceId))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {workspaceId}");
            priorFocusedTabId = ws.FocusedTabId;
            tab = _state.CreateTab(ws.Id, RejectUnsafeLabel(EmptyToNull(p.Label)), focus);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        JsonObject? paneJson = null;
        if (createPane)
        {
            try
            {
                paneJson = await SpawnPaneAsync(
                        ws, p.Command ?? string.Empty, p.Args ?? [], p.Label, null, ct, tab.Id)
                    .ConfigureAwait(false);
                tab = _state.GetTab(tab.Id) ?? tab;
            }
            catch
            {
                // FailSpawnCleanupAsync already dropped the pane row and persisted the
                // empty tab. Close it (when it is not last) so a failed create cannot
                // steal focus or leave a durable extra tab. CloseTab would otherwise
                // restore TabIds[0]; put the pre-create focus back when it still exists.
                if (!SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
                    await RollbackAppliedTabAsync(tab.Id, ct, priorFocusedTabId).ConfigureAwait(false);
                throw;
            }
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        await EmitTabLifecycleAsync(tab.Id.Value, "created", tab.WorkspaceId.Value, ct).ConfigureAwait(false);
        if (focus)
            await EmitTabLifecycleAsync(tab.Id.Value, "focused", tab.WorkspaceId.Value, ct).ConfigureAwait(false);

        var result = TabToJson(tab);
        if (paneJson is not null)
            result["pane"] = paneJson;
        return result;
    }

    private JsonArray TabList(TabListParams p)
    {
        WorkspaceId? filter = string.IsNullOrWhiteSpace(p.WorkspaceId)
            ? null
            : new WorkspaceId(p.WorkspaceId);
        var arr = new JsonArray();
        foreach (var tab in _state.ListTabs(filter))
            arr.Add((JsonNode)TabToJson(tab));
        return arr;
    }

    private JsonObject TabGet(TabGetParams p)
    {
        var id = RequireField(p.TabId, "tab_id");
        var tab = _state.GetTab(new TabId(id))
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {id}");
        return TabToJson(tab);
    }

    private async Task<JsonObject> TabFocusAsync(
        TabFocusParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("tab.focus");
        var id = RequireField(p.TabId, "tab_id");
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        TabState tab;
        try
        {
            EnsureAttachPaneMutation(connection, "tab.focus");
            EnsureNotFrozenForMutation("tab.focus");
            tab = _state.FocusTab(new TabId(id))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {id}");
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        RememberAttachClientSelection(
            connection,
            tab.WorkspaceId.Value,
            tab.Id.Value,
            tab.FocusedPaneId?.Value);
        await EmitTabLifecycleAsync(tab.Id.Value, "focused", tab.WorkspaceId.Value, ct, connection).ConfigureAwait(false);
        return TabToJson(tab);
    }

    private async Task<JsonObject> TabRenameAsync(
        TabRenameParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("tab.rename");
        var id = RequireField(p.TabId, "tab_id");
        var label = RejectUnsafeLabel(RequireField(p.Label, "label"))!;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        TabState tab;
        try
        {
            EnsureAttachPaneMutation(connection, "tab.rename");
            EnsureNotFrozenForMutation("tab.rename");
            tab = _state.RenameTab(new TabId(id), label)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {id}");
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        await EmitTabLifecycleAsync(tab.Id.Value, "renamed", tab.WorkspaceId.Value, ct).ConfigureAwait(false);
        return TabToJson(tab);
    }

    private async Task<JsonObject> TabMoveAsync(
        TabMoveParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("tab.move");
        var id = RequireField(p.TabId, "tab_id");
        if (p.Index is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "index is required");
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        TabState tab;
        try
        {
            EnsureAttachPaneMutation(connection, "tab.move");
            EnsureNotFrozenForMutation("tab.move");
            try
            {
                tab = _state.MoveTab(new TabId(id), p.Index.Value)
                    ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {id}");
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "index is out of range");
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        await EmitTabLifecycleAsync(tab.Id.Value, "moved", tab.WorkspaceId.Value, ct).ConfigureAwait(false);
        return TabToJson(tab);
    }

    private async Task<JsonObject> TabCloseAsync(
        TabCloseParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("tab.close");
        var id = RequireField(p.TabId, "tab_id");
        var tabId = new TabId(id);
        var ifEmpty = p.IfEmpty is true;

        IReadOnlyList<(string Id, int OccupantGeneration)> panes = [];
        string workspaceId;
        JsonObject? refused = null;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "tab.close");
            EnsureNotFrozenForMutation("tab.close");
            var tab = _state.GetTab(tabId)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {id}");
            if (_state.IsLastTabInWorkspace(tabId))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "Cannot close the last tab");
            workspaceId = tab.WorkspaceId.Value;
            // OccupantPaneIds unions layout leaves and HiddenPaneIds. Check
            // that set here, under the same gate as CloseTab. Do not use
            // TabToJson: it omits hidden occupants.
            var occupants = AppState.OccupantPaneIds(tab);
            if (ifEmpty && occupants.Count > 0)
            {
                refused = new JsonObject
                {
                    ["ok"] = true,
                    ["closed"] = false,
                    ["reason"] = "not_empty",
                    ["tab_id"] = id,
                };
            }
            else
            {
                var captured = new List<(string Id, int OccupantGeneration)>(occupants.Count);
                foreach (var paneId in occupants)
                {
                    captured.Add((paneId.Value, SnapshotOccupantGeneration(paneId)));
                    _metadata.Drop("pane", paneId.Value);
                }

                panes = captured;
                var outcome = _state.CloseTab(tabId);
                if (outcome == CloseTabOutcome.NotFound)
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {id}");
                if (outcome == CloseTabOutcome.LastTab)
                    throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "Cannot close the last tab");
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        if (refused is not null)
            return refused;

        foreach (var pane in panes)
            await TeardownRemovedPaneRuntimeAsync(pane.Id, pane.OccupantGeneration, ct).ConfigureAwait(false);

        await PersistGraphAsync(requireDurable: true, removeMissingPanes: true).ConfigureAwait(false);
        ReconcileAttachClientViews();
        await EmitTabLifecycleAsync(id, "closed", workspaceId, ct).ConfigureAwait(false);
        return ifEmpty
            ? new JsonObject { ["ok"] = true, ["closed"] = true, ["tab_id"] = id }
            : new JsonObject { ["ok"] = true, ["tab_id"] = id };
    }

    private async Task<JsonObject> PaneSplitAsync(
        PaneSplitParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.split");
        var paneId = RequireField(p.PaneId, "pane_id");
        var direction = RequireField(p.Direction, "direction");
        if (!LayoutNode.IsSplitDirection(direction))
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "direction must be right or down");
        var ratio = p.Ratio ?? 0.5;
        if (!LayoutNode.IsValidRatio(ratio))
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "ratio must be in (0,1)");

        var source = _state.GetPane(new PaneId(paneId))
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
        RejectHiddenPane(source, "pane.split");
        var ws = _state.GetWorkspace(source.WorkspaceId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Workspace not found");
        var tab = _state.GetTab(source.TabId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Tab not found");
        var restorePaneId = source.Id.Value;
        var restoreZoomed = tab.Zoomed;
        var restoreZoomedPaneId = tab.ZoomedPaneId?.Value;

        var closeOnExit = p.CloseOnExit is true;
        Func<PaneId, CancellationToken, ValueTask>? beforeStart = null;
        if (closeOnExit)
        {
            beforeStart = (id, _) =>
            {
                RegisterCommandOverlay(new CustomCommandOverlayState
                {
                    OverlayPaneId = id.Value,
                    RestorePaneId = restorePaneId,
                    TabId = tab.Id.Value,
                    RestoreZoomed = restoreZoomed,
                    RestoreZoomedPaneId = restoreZoomedPaneId,
                });
                return ValueTask.CompletedTask;
            };
        }

        JsonObject? created = null;
        PaneState? newPane = null;
        try
        {
            // TabCreate releases _bindingMutationGate before SpawnPaneAsync.
            // SemaphoreSlim(1,1) is not recursive, so split must match that.
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureAttachPaneMutation(connection, "pane.split");
                EnsureNotFrozenForMutation("pane.split");
                if (_state.GetPane(source.Id) is null)
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
                tab = _state.GetTab(source.TabId)
                    ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Tab not found");
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            created = await SpawnPaneAsync(
                    ws,
                    p.Command ?? string.Empty,
                    p.Args ?? [],
                    EmptyToNull(p.Label),
                    source.Binding,
                    ct,
                    tab.Id,
                    persistAfterStart: false,
                    env: p.Env,
                    cwd: EmptyToNull(p.Cwd),
                    beforeStart: beforeStart)
                .ConfigureAwait(false);
            var newPaneId = created["pane_id"]?.GetValue<string>()
                ?? throw new ControlPlaneException(ProtocolErrorCodes.PaneStartFailed, "split spawn missing pane_id");
            newPane = _state.GetPane(new PaneId(newPaneId))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Split pane missing after spawn");

            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureNotFrozenForMutation("pane.split");
                tab = _state.GetTab(source.TabId)
                    ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Tab not found");
                var root = tab.LayoutRoot;
                if (root is not null && LayoutTreeOperations.ContainsPane(root, newPane.Id))
                    (root, _) = LayoutTreeOperations.RemovePane(root, newPane.Id);
                root ??= LayoutTreeOperations.FromPane(source);
                LayoutNode split;
                try
                {
                    split = LayoutTreeOperations.Split(
                        root,
                        source.Id,
                        LayoutTreeOperations.FromPane(newPane),
                        direction,
                        ratio);
                }
                catch (InvalidOperationException)
                {
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found in layout: {paneId}");
                }
                catch (ArgumentOutOfRangeException)
                {
                    throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "direction or ratio is invalid");
                }

                var previousFocus = tab.FocusedPaneId ?? source.Id;
                var focusNewPane = p.Focus ?? true;
                _state.SetTabLayout(
                    tab.Id,
                    split,
                    zoomed: closeOnExit ? true : null,
                    zoomedPaneId: closeOnExit ? newPane.Id : null,
                    focusedPaneId: focusNewPane ? newPane.Id : previousFocus);
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
            if (closeOnExit)
            {
                ArmCommandOverlay(newPane.Id.Value);
                if (await TryCloseCommandOverlayIfExitedAsync(newPane.Id.Value).ConfigureAwait(false))
                    return created;
            }

            await EmitLayoutUpdatedAsync(tab.Id.Value, newPane.Id.Value, ct).ConfigureAwait(false);
            return created;
        }
        catch
        {
            if (newPane is not null)
            {
                _ = TakeCommandOverlay(newPane.Id.Value);
                if (!SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
                    await RollbackSpawnedPaneAsync(newPane.Id, ct).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<JsonObject> PaneMoveAsync(
        PaneMoveParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.move");
        var paneId = RequireField(p.PaneId, "pane_id");
        var destination = RequireField(p.Destination, "destination");
        if (destination is not ("tab" or "new_tab"))
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "destination must be tab or new_tab");

        TabId? createdDest = null;
        TabId destTabId = default;
        TabState? sourceBefore = null;
        TabState? destBefore = null;
        var splitAfterMove = false;
        string? splitDirection = null;
        var splitRatio = 0.5;
        PaneId splitTarget = default;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "pane.move");
            EnsureNotFrozenForMutation("pane.move");
            var pane = _state.GetPane(new PaneId(paneId))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
            RejectHiddenPane(pane, "pane.move");
            var sourceTab = _state.GetTab(pane.TabId)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Tab not found");
            if (sourceTab.Zoomed)
            {
                return new JsonObject
                {
                    ["changed"] = false,
                    ["reason"] = "zoomed_tab",
                    ["pane_id"] = paneId,
                    ["tab_id"] = sourceTab.Id.Value,
                };
            }

            sourceBefore = sourceTab;
            if (destination == "new_tab")
            {
                var workspaceId = EmptyToNull(p.WorkspaceId) ?? pane.WorkspaceId.Value;
                var ws = _state.GetWorkspace(new WorkspaceId(workspaceId))
                    ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {workspaceId}");
                var created = _state.CreateTab(ws.Id, label: pane.Label, focus: false);
                createdDest = created.Id;
                destTabId = created.Id;
                destBefore = created;
            }
            else
            {
                destTabId = new TabId(RequireField(p.TabId, "tab_id"));
                var destTab = _state.GetTab(destTabId)
                    ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {destTabId}");
                if (destTab.Id.Value == pane.TabId.Value)
                    throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "cannot move a pane onto its own tab");
                destBefore = destTab;
                if (destTab.PaneIds.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(p.Split) || !LayoutNode.IsSplitDirection(p.Split))
                        throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "split must be right or down when the destination tab already has a pane");
                    var ratio = p.Ratio ?? 0.5;
                    if (!LayoutNode.IsValidRatio(ratio))
                        throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "ratio must be in (0,1)");
                    var targetId = EmptyToNull(p.TargetPaneId);
                    var target = targetId is null
                        ? destTab.PaneIds.FirstOrDefault()
                        : new PaneId(targetId);
                    if (target.Value is null
                        || destTab.LayoutRoot is null
                        || !LayoutTreeOperations.ContainsPane(destTab.LayoutRoot, target))
                    {
                        throw new ControlPlaneException(
                            ProtocolErrorCodes.NotFound,
                            $"Pane not found in layout: {targetId ?? target.Value}");
                    }

                    splitAfterMove = true;
                    splitDirection = p.Split;
                    splitRatio = ratio;
                    splitTarget = target;
                }
            }

            var moved = _state.MovePaneToTab(pane.Id, destTabId)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
            if (splitAfterMove)
            {
                var dest = _state.GetTab(destTabId)!;
                try
                {
                    var (without, _) = LayoutTreeOperations.RemovePane(dest.LayoutRoot, pane.Id);
                    var root = without ?? dest.LayoutRoot
                        ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "tab has no layout");
                    var split = LayoutTreeOperations.Split(
                        root,
                        splitTarget,
                        LayoutTreeOperations.FromPane(moved),
                        splitDirection!,
                        splitRatio);
                    _state.SetTabLayout(dest.Id, split, focusedPaneId: pane.Id);
                }
                catch (InvalidOperationException)
                {
                    RestoreMovedPane(pane.Id, sourceBefore!, destBefore!);
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found in layout: {splitTarget.Value}");
                }
                catch (ArgumentOutOfRangeException)
                {
                    RestoreMovedPane(pane.Id, sourceBefore!, destBefore!);
                    throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "split or ratio is invalid");
                }
                catch (ControlPlaneException)
                {
                    RestoreMovedPane(pane.Id, sourceBefore!, destBefore!);
                    throw;
                }
            }
        }
        catch
        {
            if (createdDest is { } unused
                && !SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState)
                && !_state.IsLastTabInWorkspace(unused))
            {
                _ = _state.CloseTab(unused);
            }

            throw;
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        await EmitPaneMovedAsync(paneId, destTabId.Value, ct).ConfigureAwait(false);
        await EmitLayoutUpdatedAsync(destTabId.Value, paneId, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["changed"] = true,
            ["pane_id"] = paneId,
            ["tab_id"] = destTabId.Value,
        };
    }

    private async Task<JsonObject> PaneZoomAsync(
        PaneZoomParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.zoom");
        RejectHiddenPaneId(p.PaneId, "pane.zoom");
        var tab = ResolveTabFromPaneOrFocus(p.PaneId);
        var paneId = EmptyToNull(p.PaneId) ?? tab.FocusedPaneId?.Value
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "pane_id is required");
        RejectHiddenPaneId(paneId, "pane.zoom");
        var mode = (p.Mode ?? "toggle").ToLowerInvariant();
        if (mode is not ("on" or "off" or "toggle"))
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "mode must be toggle, on, or off");

        var next = false;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "pane.zoom");
            EnsureNotFrozenForMutation("pane.zoom");
            var current = _state.GetTab(tab.Id)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {tab.Id.Value}");
            if (LayoutTreeOperations.LeafCount(current.LayoutRoot) <= 1 && current.PaneIds.Count <= 1)
            {
                return new JsonObject
                {
                    ["changed"] = false,
                    ["reason"] = "single_pane",
                    ["zoomed"] = false,
                    ["pane_id"] = paneId,
                };
            }

            if (mode == "on"
                && current.Zoomed
                && current.ZoomedPaneId?.Value == paneId)
            {
                return new JsonObject
                {
                    ["changed"] = false,
                    ["zoomed"] = true,
                    ["pane_id"] = paneId,
                };
            }

            next = mode switch
            {
                "on" => true,
                "off" => false,
                _ => !current.Zoomed,
            };
            _state.UpdateTab(current.Id, t => t with
            {
                Zoomed = next,
                ZoomedPaneId = next ? new PaneId(paneId) : null,
                FocusedPaneId = new PaneId(paneId),
            });
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        await EmitLayoutUpdatedAsync(tab.Id.Value, paneId, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["changed"] = true,
            ["zoomed"] = next,
            ["pane_id"] = paneId,
        };
    }

    private async Task<JsonObject> PaneFocusDirectionAsync(
        PaneFocusDirectionParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.focus_direction");
        var direction = RequireField(p.Direction, "direction");
        if (!LayoutNode.IsFocusDirection(direction))
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "direction must be right, down, left, or up");
        RejectHiddenPaneId(p.PaneId, "pane.focus_direction");
        var tab = ResolveTabFromPaneOrFocus(p.PaneId);
        var from = EmptyToNull(p.PaneId) ?? tab.FocusedPaneId?.Value
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "pane_id is required");
        RejectHiddenPaneId(from, "pane.focus_direction");

        PaneId next = default;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "pane.focus_direction");
            EnsureNotFrozenForMutation("pane.focus_direction");
            var current = _state.GetTab(tab.Id)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {tab.Id.Value}");
            var root = current.LayoutRoot
                ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "tab has no layout");
            var neighbor = LayoutTreeOperations.FocusDirection(root, new PaneId(from), direction);
            if (neighbor?.PaneId is not { } found)
                return new JsonObject { ["changed"] = false, ["pane_id"] = from, ["tab_id"] = current.Id.Value };

            next = found;
            _state.UpdateTab(current.Id, t => t with { FocusedPaneId = next });
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        return new JsonObject { ["changed"] = true, ["pane_id"] = next.Value, ["tab_id"] = tab.Id.Value };
    }

    private JsonObject ExportLayout(string? tabId, string? paneId)
    {
        var tab = ResolveTabFromPaneOrFocus(paneId, tabId);
        return new JsonObject
        {
            ["workspace_id"] = tab.WorkspaceId.Value,
            ["tab_id"] = tab.Id.Value,
            ["zoomed"] = tab.Zoomed,
            ["focused_pane_id"] = tab.FocusedPaneId?.Value,
            ["zoomed_pane_id"] = tab.ZoomedPaneId?.Value,
            ["root"] = tab.LayoutRoot?.ToJsonObject(includePaneId: true),
        };
    }

    private async Task<JsonObject> LayoutApplyAsync(
        LayoutApplyParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("layout.apply");
        var workspaceId = RequireField(p.WorkspaceId, "workspace_id");
        if (p.Root is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "root is required");
        var applied = LayoutTreeOperations.FromDto(p.Root, keepPaneId: false)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "root is invalid");
        var ws = _state.GetWorkspace(new WorkspaceId(workspaceId))
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {workspaceId}");

        var replaceId = EmptyToNull(p.TabId);
        if (replaceId is not null)
        {
            var replaceTabId = new TabId(replaceId);
            var old = _state.GetTab(replaceTabId)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {replaceId}");
            if (_state.IsLastTabInWorkspace(replaceTabId))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "Cannot replace the last tab");
        }

        TabState? tab = null;
        var oldTabClosed = false;
        TabId? priorFocusedTabId = null;
        try
        {
            LayoutNode rewritten;
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureAttachPaneMutation(connection, "layout.apply");
                EnsureNotFrozenForMutation("layout.apply");
                var liveWs = _state.GetWorkspace(ws.Id)
                    ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {workspaceId}");
                priorFocusedTabId = liveWs.FocusedTabId;
                tab = _state.CreateTab(liveWs.Id, EmptyToNull(p.TabLabel) ?? "layout", p.Focus ?? true);
                var idMap = new Dictionary<string, PaneId>(StringComparer.Ordinal);
                rewritten = await MaterializeLayoutLeavesAsync(applied, ws, tab.Id, idMap, ct)
                    .ConfigureAwait(false);
                EnsureAttachPaneMutation(connection, "layout.apply");
                EnsureNotFrozenForMutation("layout.apply");
                _state.SetTabLayout(tab.Id, rewritten);
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            if (replaceId is not null)
            {
                var replaceTabId = new TabId(replaceId);
                IReadOnlyList<(string Id, int OccupantGeneration)> oldPanes = [];
                await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    EnsureAttachPaneMutation(connection, "layout.apply");
                    EnsureNotFrozenForMutation("layout.apply");
                    if (_state.IsLastTabInWorkspace(replaceTabId))
                        throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "Cannot replace the last tab");
                    var old = _state.GetTab(replaceTabId);
                    if (old is not null)
                    {
                        _state.RehomeHiddenPanes(old.Id, tab.Id);
                        old = _state.GetTab(replaceTabId) ?? old;
                        var captured = new List<(string Id, int OccupantGeneration)>(old.PaneIds.Count);
                        foreach (var paneId in old.PaneIds)
                        {
                            captured.Add((paneId.Value, SnapshotOccupantGeneration(paneId)));
                            _metadata.Drop("pane", paneId.Value);
                        }
                        oldPanes = captured;
                        var outcome = _state.CloseTab(old.Id);
                        if (outcome != CloseTabOutcome.Closed)
                        {
                            throw new ControlPlaneException(
                                outcome == CloseTabOutcome.LastTab
                                    ? ProtocolErrorCodes.InvalidState
                                    : ProtocolErrorCodes.NotFound,
                                outcome == CloseTabOutcome.LastTab
                                    ? "Cannot replace the last tab"
                                    : $"Tab not found: {replaceId}");
                        }

                        oldTabClosed = true;
                    }
                }
                finally
                {
                    _bindingMutationGate.Release();
                }

                foreach (var pane in oldPanes)
                    await TeardownRemovedPaneRuntimeAsync(pane.Id, pane.OccupantGeneration, ct).ConfigureAwait(false);
            }

            // Replace already CloseTab'd the target; persist must delete those rows or
            // restart/reattach resurrects the old layout beside the new one.
            await PersistGraphAsync(requireDurable: true, removeMissingPanes: oldTabClosed)
                .ConfigureAwait(false);
            await EmitTabLifecycleAsync(tab.Id.Value, "created", tab.WorkspaceId.Value, ct).ConfigureAwait(false);
            await EmitLayoutUpdatedAsync(tab.Id.Value, null, ct).ConfigureAwait(false);
            return TabToJson(_state.GetTab(tab.Id) ?? tab);
        }
        catch
        {
            if (tab is not null
                && !oldTabClosed
                && !SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
                await RollbackAppliedTabAsync(tab.Id, ct, priorFocusedTabId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<LayoutNode> MaterializeLayoutLeavesAsync(
        LayoutNode node,
        WorkspaceState ws,
        TabId tabId,
        Dictionary<string, PaneId> idMap,
        CancellationToken ct)
    {
        if (node is LayoutPaneNode leaf)
        {
            var command = leaf.Command.Count > 0 ? leaf.Command[0] : string.Empty;
            var args = leaf.Command.Count > 1 ? leaf.Command.Skip(1).ToArray() : [];
            var created = await SpawnPaneAsync(
                    ws, command, args, EmptyToNull(leaf.Label), null, ct, tabId, persistAfterStart: false)
                .ConfigureAwait(false);
            var newId = created["pane_id"]?.GetValue<string>()
                ?? throw new ControlPlaneException(ProtocolErrorCodes.PaneStartFailed, "apply spawn missing pane_id");
            var pane = _state.GetPane(new PaneId(newId))!;
            if (leaf.PaneId is { } old)
                idMap[old.Value] = pane.Id;
            return LayoutTreeOperations.FromPane(pane) with
            {
                Label = string.IsNullOrEmpty(leaf.Label) ? pane.Label : leaf.Label,
                Cwd = string.IsNullOrEmpty(leaf.Cwd) ? pane.Cwd : leaf.Cwd,
                Command = leaf.Command,
            };
        }

        if (node is LayoutSplitNode split)
        {
            var first = await MaterializeLayoutLeavesAsync(split.First, ws, tabId, idMap, ct).ConfigureAwait(false);
            var second = await MaterializeLayoutLeavesAsync(split.Second, ws, tabId, idMap, ct).ConfigureAwait(false);
            return split with { First = first, Second = second };
        }

        throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "root is invalid");
    }

    private async Task<JsonObject> PaneSwapAsync(
        PaneSwapParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.swap");
        var hasDirection = !string.IsNullOrWhiteSpace(p.Direction);
        var hasTarget = !string.IsNullOrWhiteSpace(p.TargetPaneId);
        if (!hasDirection && !hasTarget)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "direction or target_pane_id is required");
        }

        if (hasDirection && !LayoutNode.IsFocusDirection(p.Direction))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "direction must be right, down, left, or up");
        }

        var sourcePane = ResolveOptionalPane(p.PaneId);
        RejectHiddenPane(sourcePane, "pane.swap");
        var sourceTab = _state.GetTab(sourcePane.TabId)
            ?? throw new ControlPlaneException(
                ProtocolErrorCodes.NotFound, $"Tab not found: {sourcePane.TabId.Value}");
        var root = sourceTab.LayoutRoot
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "tab has no layout");
        if (!LayoutTreeOperations.ContainsPane(root, sourcePane.Id))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.NotFound,
                $"Pane not found in layout: {sourcePane.Id.Value}");
        }

        string targetId;
        if (hasTarget)
        {
            targetId = p.TargetPaneId!;
        }
        else
        {
            var neighbor = LayoutTreeOperations.Neighbor(root, sourcePane.Id, p.Direction!);
            if (neighbor?.PaneId is not { } found)
            {
                return new JsonObject
                {
                    ["changed"] = false,
                    ["reason"] = "no_neighbor",
                    ["pane_id"] = sourcePane.Id.Value,
                    ["tab_id"] = sourceTab.Id.Value,
                };
            }

            targetId = found.Value;
        }

        if (targetId == sourcePane.Id.Value)
        {
            return new JsonObject
            {
                ["changed"] = false,
                ["reason"] = "same_pane",
                ["pane_id"] = sourcePane.Id.Value,
                ["target_pane_id"] = targetId,
                ["tab_id"] = sourceTab.Id.Value,
            };
        }

        var targetPane = _state.GetPane(new PaneId(targetId))
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {targetId}");
        RejectHiddenPane(targetPane, "pane.swap");
        if (targetPane.TabId.Value != sourcePane.TabId.Value)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "different_tab");
        }

        if (!LayoutTreeOperations.ContainsPane(root, targetPane.Id))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.NotFound,
                $"Pane not found in layout: {targetId}");
        }

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "pane.swap");
            EnsureNotFrozenForMutation("pane.swap");
            var current = _state.GetTab(sourceTab.Id)
                ?? throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound, $"Tab not found: {sourceTab.Id.Value}");
            var liveRoot = current.LayoutRoot
                ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "tab has no layout");
            if (!LayoutTreeOperations.ContainsPane(liveRoot, sourcePane.Id)
                || !LayoutTreeOperations.ContainsPane(liveRoot, targetPane.Id))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    $"Pane not found in layout: {targetId}");
            }

            LayoutNode swapped;
            try
            {
                swapped = LayoutTreeOperations.SwapLeaves(liveRoot, sourcePane.Id, targetPane.Id);
            }
            catch (InvalidOperationException)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    $"Pane not found in layout: {targetId}");
            }

            _state.UpdateTab(current.Id, t => t with { LayoutRoot = swapped });
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        await EmitLayoutUpdatedAsync(sourceTab.Id.Value, sourcePane.Id.Value, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["changed"] = true,
            ["pane_id"] = sourcePane.Id.Value,
            ["target_pane_id"] = targetId,
            ["tab_id"] = sourceTab.Id.Value,
        };
    }

    private async Task<JsonObject> LayoutSetSplitRatioAsync(
        LayoutSetSplitRatioParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("layout.set_split_ratio");
        var tabId = RequireField(p.TabId, "tab_id");
        var leaseId = RequireField(p.LeaseId, "lease_id");
        if (p.Path is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "path is required");
        var ratio = p.Ratio ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "ratio is required");
        if (!LayoutNode.IsValidRatio(ratio))
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "ratio must be in (0,1)");

        var tab = _state.GetTab(new TabId(tabId))
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {tabId}");
        var leasePane = tab.FocusedPaneId?.Value
            ?? tab.PaneIds.FirstOrDefault().Value
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "tab has no pane");
        AuthorizeResize(leasePane, leaseId, connection?.ConnectionId);

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "layout.set_split_ratio");
            EnsureNotFrozenForMutation("layout.set_split_ratio");
            tab = _state.GetTab(new TabId(tabId))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {tabId}");
            if (tab.LayoutRoot is null)
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "tab has no layout");
            try
            {
                var next = LayoutTreeOperations.SetSplitRatio(tab.LayoutRoot, p.Path, ratio);
                _state.SetTabLayout(tab.Id, next);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "ratio must be in (0,1)");
            }
            catch (InvalidOperationException)
            {
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "path does not address a split");
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        await EmitLayoutUpdatedAsync(tabId, null, ct).ConfigureAwait(false);
        var pathNodes = new JsonNode[p.Path.Count];
        for (var i = 0; i < p.Path.Count; i++)
            pathNodes[i] = JsonValue.Create(p.Path[i]);
        return new JsonObject
        {
            ["tab_id"] = tabId,
            ["ratio"] = LayoutNode.RatioNode(ratio),
            ["path"] = new JsonArray(pathNodes),
        };
    }

    /// <summary>
    /// Occupant start/replace on an existing pane. Must not create workspace, tab, split, or apply.
    /// Holds the per-pane admit lock for the whole replace. The previous IPaneRuntime stays
    /// registered until StartAsync commits so a failed start keeps a live occupant.
    /// </summary>
    private async Task<AgentStartResult> AgentStartAsync(
        AgentStartParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.PaneId))
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "pane_id is required");
        EnsureNotFrozenForMutation("agent.start");

        var paneId = new PaneId(p.PaneId);
        if (_state.GetPane(paneId) is null)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {p.PaneId}");

        var workId = EmptyToNull(p.WorkId);
        var workGeneration = p.Generation;
        if (workId is null && workGeneration is not null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "work_id is required when generation is set");
        }

        if (workId is not null && workGeneration is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "generation is required when work_id is set");
        }

        if (workGeneration is < 1)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "generation must be at least 1");
        }

        EnsureWorkGenerationAllowsStart(workId, workGeneration);

        // Empty or
        // whitespace is not omitted; omitted/null keeps occupant/command/restart.
        var hasKind = p.Kind is not null;
        var hasOccupant = !string.IsNullOrWhiteSpace(p.Occupant);
        var hasCommand = !string.IsNullOrWhiteSpace(p.Command);
        if (hasKind && hasOccupant)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "kind and occupant are mutually exclusive");
        }

        if (hasKind && hasCommand)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "kind and command are mutually exclusive");
        }

        if (hasOccupant && hasCommand)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "occupant and command are mutually exclusive");
        }

        var occupantId = p.Occupant;
        if (hasKind)
        {
            if (!AgentKindCatalog.TryResolve(p.Kind, out var canonical))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    AgentKindCatalog.UnsupportedKindMessage(p.Kind!));
            }

            occupantId = canonical;
            hasOccupant = true;
        }

        OccupantManifest? resolvedOccupant = null;
        string? transcriptRoot = null;
        string? spawnCwd = null;
        IReadOnlyDictionary<string, string>? spawnEnv = null;
        string? occupantHome = null;
        string? command;
        IReadOnlyList<string> args;

        if (hasOccupant)
        {
            if (!_occupants.TryGet(occupantId!, out var manifest) || manifest is null)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    $"Occupant not found: {occupantId}");
            }

            var existingForCwd = _state.GetPane(paneId)!;
            occupantHome = !string.IsNullOrWhiteSpace(p.CubeHome)
                ? Path.GetFullPath(p.CubeHome.Trim())
                : _cubeHome;
            var cubeHome = occupantHome!;
            var workspaceForExpand = !string.IsNullOrWhiteSpace(p.Cwd)
                ? Path.GetFullPath(p.Cwd.Trim())
                : existingForCwd.Cwd;
            var expanded = OccupantTemplateExpander.Expand(
                manifest,
                new OccupantTemplateContext
                {
                    Workspace = workspaceForExpand,
                    CubeHome = cubeHome,
                });
            if (expanded.Command.Count == 0 || string.IsNullOrWhiteSpace(expanded.Command[0]))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    "occupant command is empty");
            }

            command = expanded.Command[0];
            // Extra params.args append after the manifest argv (Work handoff resume:
            // pi --session <path>). Command remains mutually exclusive with occupant.
            var manifestTail = expanded.Command.Count > 1
                ? expanded.Command.Skip(1)
                : Array.Empty<string>();
            args = p.Args is { Count: > 0 }
                ? manifestTail.Concat(p.Args).ToArray()
                : manifestTail.ToArray();
            spawnCwd = !string.IsNullOrWhiteSpace(p.Cwd)
                ? Path.GetFullPath(p.Cwd.Trim())
                : expanded.Cwd;
            if (p.Env is { Count: > 0 })
            {
                var merged = new Dictionary<string, string>(
                    expanded.Env ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal);
                foreach (var kv in p.Env)
                    merged[kv.Key] = kv.Value;
                spawnEnv = merged;
            }
            else
            {
                spawnEnv = expanded.Env;
            }

            transcriptRoot = expanded.TranscriptRoot;
            resolvedOccupant = expanded;
        }
        else
        {
            var existing = _state.GetPane(paneId)!;
            command = p.Command ?? existing.Command;
            args = p.Args ?? existing.Args;
        }

        // Lock order: pane admit → bindingMutationGate (same as agent.prompt).
        var admit = await AcquirePaneAdmitAsync(paneId.Value, ct).ConfigureAwait(false);
        try
        {
            EnsureWorkGenerationAllowsStart(workId, workGeneration);
            var existing = _state.GetPane(paneId)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {p.PaneId}");
            var restoreCommand = existing.Command;
            var restoreArgs = existing.Args.ToArray();
            var restoreLifecycle = existing.LifecycleState;
            var restoreAgentStatus = existing.AgentStatus;
            var restoreAgentKind = existing.AgentKind;
            var restoreAgentMessage = existing.AgentMessage;
            var restoreAgentAuthority = existing.AgentAuthority;
            var restoreAgentSession = existing.AgentSession;
            var restoreAgentAuthoritySequences = existing.AgentAuthoritySequences;
            var restoreIsAlive = existing.IsAlive;
            var restoreExitCode = existing.ExitCode;
            var restorePid = existing.Pid;

            IPaneRuntime? previous = null;
            IPaneRuntime? runtime = null;
            var occupantCommitted = false;
            var generation = 0;
            try
            {
                if (AgentStartBeforeBindingGateForTests is { } beforeGate)
                    await beforeGate().ConfigureAwait(false);

                await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    EnsureAttachPaneMutation(connection, "agent.start");
                    EnsureNotFrozenForMutation("agent.start");
                    if (hasOccupant && occupantHome is not null && transcriptRoot is not null)
                    {
                        Directory.CreateDirectory(occupantHome);
                        Directory.CreateDirectory(transcriptRoot);
                        if (resolvedOccupant is not null
                            && string.Equals(resolvedOccupant.Id, "pi", StringComparison.Ordinal))
                        {
                            spawnEnv = EnsureLivePiResumeEnv(occupantHome, spawnEnv);
                            try
                            {
                                OccupantResumeReporter.Write(occupantHome);
                                OccupantResumeReporter.WritePiHomeExtension(occupantHome);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                throw new ControlPlaneException(
                                    ProtocolErrorCodes.PaneStartFailed,
                                    "live Pi resume reporter write failed: " + ex.Message);
                            }

                            args = EnsureLivePiResumeReporterArg(args, occupantHome);
                        }
                    }

                    var live = _state.GetPane(paneId)
                        ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {p.PaneId}");
                    // Snapshot the restore tuple under the binding gate immediately
                    // before MarkReplacementInFlight/bump so a report_agent that won
                    // the pre-gate window is the tuple rollback restores.
                    restoreCommand = live.Command;
                    restoreArgs = live.Args.ToArray();
                    restoreLifecycle = live.LifecycleState;
                    restoreAgentStatus = live.AgentStatus;
                    restoreAgentKind = live.AgentKind;
                    restoreAgentMessage = live.AgentMessage;
                    restoreAgentAuthority = live.AgentAuthority;
                    restoreAgentSession = live.AgentSession;
                    restoreAgentAuthoritySequences = live.AgentAuthoritySequences.Count == 0
                        ? EmptyAuthoritySequences()
                        : new Dictionary<string, long>(live.AgentAuthoritySequences, StringComparer.Ordinal);
                    restoreIsAlive = live.IsAlive;
                    restoreExitCode = live.ExitCode;
                    restorePid = live.Pid;
                    // execve does not search PATH. Resolve after the restore
                    // snapshot and before the generation bump so a miss leaves
                    // the previous occupant and does not bump generation.
                    if (OccupantCommandResolver.RequiresExecvePathSearch(_ptyProvider))
                    {
                        var resolveCwd = spawnCwd ?? live.Cwd;
                        if (!OccupantCommandResolver.TryResolve(
                                command,
                                resolveCwd,
                                spawnEnv,
                                Environment.GetEnvironmentVariable("PATH"),
                                out var resolvedCommand))
                        {
                            throw new ControlPlaneException(
                                ProtocolErrorCodes.PaneStartFailed,
                                OccupantCommandResolver.UnresolvableMessage(command));
                        }

                        command = resolvedCommand;
                    }

                    lock (_gate)
                    {
                        _runtimes.TryGetValue(paneId.Value, out previous);
                        MarkReplacementInFlightUnlocked(paneId.Value);
                    }
                    generation = await BumpOccupantGenerationUnderAdmitAsync(paneId.Value, ct)
                        .ConfigureAwait(false);
                    // Command/args and occupant liveness stay on the previous occupant
                    // until StartAsync commits. Do not publish IsAlive=false while that
                    // runtime is still registered.
                    UpdatePaneEmittingStatus(paneId, pane => pane with
                    {
                        LifecycleState = PaneLifecycle.Starting,
                        AgentStatus = AgentStatus.Working,
                        OccupantGeneration = generation,
                    });
                }
                finally
                {
                    _bindingMutationGate.Release();
                }

                var occupantToken = _placement.IssueOccupant(paneId, generation);
                var options = new PaneSpawnOptions
                {
                    Id = paneId,
                    Cwd = spawnCwd ?? existing.Cwd,
                    Command = command,
                    Args = args,
                    Env = WithOccupantIdentity(WithManagedPaneId(spawnEnv, paneId), paneId, occupantToken),
                    Cols = existing.Cols,
                    Rows = existing.Rows,
                    ScrollbackLimitBytes = AttachConfig.Advanced.ScrollbackLimitBytes,
                    Terminal = AttachConfig.Terminal,
                    HostTheme = SnapshotHostTheme(),
                };

                runtime = _paneFactory.Create(options);
                TrackPendingReplacementOrThrow(paneId, runtime);
                runtime.OutputReceived += OnRuntimeOutput;
                runtime.BellReceived += OnRuntimeBell;
                runtime.Exited += OnRuntimeExited;
                ApplyThemeBeforeStart(runtime);
                await runtime.StartAsync(ct).ConfigureAwait(false);

                IPaneRuntime? toDispose = null;
                await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    EnsureNotFrozenForMutation("agent.start");
                    if (IsShuttingDown)
                    {
                        throw new ControlPlaneException(
                            ProtocolErrorCodes.ServerShuttingDown,
                            ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));
                    }

                    if (_state.GetPane(paneId) is null)
                    {
                        throw new ControlPlaneException(
                            ProtocolErrorCodes.NotFound,
                            $"Pane not found: {paneId.Value}");
                    }

                    IPaneRuntime? current;
                    lock (_gate)
                        _runtimes.TryGetValue(paneId.Value, out current);
                    if (current is null && previous is not null)
                    {
                        throw new ControlPlaneException(
                            ProtocolErrorCodes.NotFound,
                            $"Pane not found: {paneId.Value}");
                    }

                    var alive = runtime.IsAlive;
                    NativeAgentSessionRef? agentSession = null;
                    if (resolvedOccupant is not null && transcriptRoot is not null)
                    {
                        agentSession = new NativeAgentSessionRef
                        {
                            Kind = NativeAgentSessionRef.KindPath,
                            Value = transcriptRoot,
                            Source = resolvedOccupant.TranscriptSource,
                            Agent = resolvedOccupant.Id,
                            SessionStartSource = "startup",
                        };
                    }

                    _metadata.Drop("pane", paneId.Value);
                    StampLiveResumeTokens(paneId.Value, spawnEnv, "agent.start");
                    EnsureWorkGenerationAllowsStart(workId, workGeneration);
                    RememberWorkGeneration(workId, workGeneration);
                    UpdatePaneEmittingStatus(paneId, pane => pane with
                    {
                        IsAlive = alive,
                        ExitCode = runtime.ExitCode,
                        Pid = alive ? runtime.Pid : null,
                        LifecycleState = alive ? PaneLifecycle.Running : PaneLifecycle.Exited,
                        AgentStatus = alive ? AgentStatus.Working : AgentStatus.Done,
                        Command = command,
                        Args = args,
                        Cwd = spawnCwd ?? pane.Cwd,
                        OccupantGeneration = generation,
                        WorkId = workId,
                        WorkGeneration = workGeneration ?? 0,
                        Home = occupantHome,
                        AgentKind = resolvedOccupant?.Id,
                        AgentMessage = null,
                        AgentAuthority = null,
                        AgentSession = agentSession,
                        AgentAuthoritySequences = EmptyAuthoritySequences(),
                    });
                    await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
                    toDispose = SwapCommittedRuntimeOrThrow(paneId, runtime, previous);
                    occupantCommitted = true;
                    // Binding → emit. Drop the previous occupant's attach epoch
                    // and repaint observers from the new grid.
                    await RepaintAfterOccupantSwapAsync(paneId.Value, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _bindingMutationGate.Release();
                }

                if (toDispose is not null && !ReferenceEquals(toDispose, runtime))
                    await DisposeRuntimeQuietlyAsync(toDispose, "Dispose previous occupant", paneId)
                        .ConfigureAwait(false);
            }
            catch (ControlPlaneException)
            {
                await FailReplacementCleanupAsync(paneId, runtime).ConfigureAwait(false);
                await RollbackFailedAgentStartAsync(
                        paneId,
                        previous,
                        occupantCommitted,
                        restoreCommand,
                        restoreArgs,
                        restoreLifecycle,
                        restoreAgentStatus,
                        restoreAgentKind,
                        restoreAgentMessage,
                        restoreAgentAuthority,
                        restoreAgentSession,
                        restoreAgentAuthoritySequences,
                        restoreIsAlive,
                        restoreExitCode,
                        restorePid)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "agent.start failed for {PaneId}", paneId);
                await FailReplacementCleanupAsync(paneId, runtime).ConfigureAwait(false);
                await RollbackFailedAgentStartAsync(
                        paneId,
                        previous,
                        occupantCommitted,
                        restoreCommand,
                        restoreArgs,
                        restoreLifecycle,
                        restoreAgentStatus,
                        restoreAgentKind,
                        restoreAgentMessage,
                        restoreAgentAuthority,
                        restoreAgentSession,
                        restoreAgentAuthoritySequences,
                        restoreIsAlive,
                        restoreExitCode,
                        restorePid)
                    .ConfigureAwait(false);
                // Close during StartAsync surfaces ObjectDisposedException. A missing
                // pane must be NotFound so clients do not retry agent.start.
                if (_state.GetPane(paneId) is null)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.NotFound,
                        $"Pane not found: {paneId.Value}");
                }

                throw PaneStartFailure();
            }

            var started = _state.GetPane(paneId) ?? existing;
            return new AgentStartResult
            {
                PaneId = paneId.Value,
                OccupantGeneration = started.OccupantGeneration,
                Command = started.Command,
                Alive = started.IsAlive,
                Occupant = resolvedOccupant?.Id,
                TranscriptRoot = transcriptRoot,
                CubeHome = occupantHome,
                WorkId = started.WorkId,
                Generation = string.IsNullOrWhiteSpace(started.WorkId) ? null : started.WorkGeneration,
            };
        }
        finally
        {
            SafeReleaseAdmit(admit);
        }
    }

    /// <summary>
    /// Copy live Pi resume env onto pane tokens so adopt can wait on the
    /// matching report. Pane create and occupant start each mint a unique
    /// attempt before spawn. Report paths longer than the token cap stay
    /// off the wire. The probe rebuilds the default report path under HOME.
    /// </summary>
    private void StampLiveResumeTokens(
        string paneId,
        IReadOnlyDictionary<string, string>? spawnEnv,
        string source)
    {
        if (string.IsNullOrWhiteSpace(paneId) || spawnEnv is null || spawnEnv.Count == 0)
            return;

        var tokens = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (spawnEnv.TryGetValue(OccupantResumeReporter.AttemptIdEnv, out var attempt)
            && OccupantResumeReporter.IsAttemptId(attempt)
            && MetadataTokenNormalizer.TryNormalizeKey(OccupantResumeReporter.AttemptIdEnv, out var attemptKey))
        {
            var attemptValue = MetadataTokenNormalizer.NormalizeValue(attempt);
            if (attemptValue.Length > 0 && attemptValue.Length <= MetadataTokenLimits.MaxValueLength)
                tokens[attemptKey] = attemptValue;
        }

        foreach (var pair in spawnEnv)
        {
            if (!pair.Key.StartsWith("HYPA_RESUME_", StringComparison.Ordinal))
                continue;
            if (string.Equals(pair.Key, OccupantResumeReporter.AttemptIdEnv, StringComparison.Ordinal))
                continue;
            if (string.Equals(pair.Key, OccupantResumeReporter.ReportPathEnv, StringComparison.Ordinal))
                continue;
            if (pair.Value.Length > MetadataTokenLimits.MaxValueLength)
                continue;
            if (!MetadataTokenNormalizer.TryNormalizeKey(pair.Key, out var key))
                continue;
            var value = MetadataTokenNormalizer.NormalizeValue(pair.Value);
            if (value.Length == 0 || value.Length > MetadataTokenLimits.MaxValueLength)
                continue;
            tokens[key] = value;
            if (tokens.Count >= MetadataTokenLimits.MaxKeysPerReport)
                break;
        }

        if (tokens.Count == 0)
            return;

        _metadata.ApplyPatch("pane", paneId, source, tokens);
    }

    private static IReadOnlyDictionary<string, string> EnsureLivePiResumeEnv(
        string cubeHome,
        IReadOnlyDictionary<string, string>? spawnEnv)
    {
        var env = new Dictionary<string, string>(
            spawnEnv ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var home = Path.GetFullPath(cubeHome.Trim());
        if (!env.TryGetValue(OccupantResumeReporter.AttemptIdEnv, out var attempt)
            || !OccupantResumeReporter.IsAttemptId(attempt))
        {
            attempt = OccupantResumeReporter.NewAttemptId();
            env[OccupantResumeReporter.AttemptIdEnv] = attempt;
        }

        if (!env.TryGetValue(OccupantResumeReporter.ReportPathEnv, out var report)
            || string.IsNullOrWhiteSpace(report)
            || !Path.IsPathRooted(report.Trim()))
        {
            env[OccupantResumeReporter.ReportPathEnv] = OccupantResumeReporter.ReportFile(home, attempt);
        }

        env.TryAdd("HOME", home);
        env.TryAdd("HYPA_CUBE_HOME", home);
        return env;
    }

    private static IReadOnlyList<string> EnsureLivePiResumeReporterArg(
        IReadOnlyList<string> args,
        string cubeHome)
    {
        var reporter = OccupantResumeReporter.PathFor(cubeHome);
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "-e", StringComparison.Ordinal)
                && string.Equals(args[i + 1], reporter, StringComparison.Ordinal))
            {
                return args;
            }
        }

        var next = new string[args.Count + 2];
        for (var i = 0; i < args.Count; i++)
            next[i] = args[i];
        next[args.Count] = "-e";
        next[args.Count + 1] = reporter;
        return next;
    }

    /// <summary>
    /// Drop a replacement that did not commit. The previous occupant stays in
    /// <c>_runtimes</c> unless the swap already won.
    /// </summary>
    private async Task FailReplacementCleanupAsync(
        PaneId paneId,
        IPaneRuntime? runtime)
    {
        if (runtime is not null)
        {
            runtime.OutputReceived -= OnRuntimeOutput;
            runtime.BellReceived -= OnRuntimeBell;
            runtime.Exited -= OnRuntimeExited;
        }

        lock (_gate)
        {
            // In-flight stays set until RollbackFailedAgentStartAsync restores.
            if (runtime is not null)
            {
                ClearPendingReplacementUnlocked(paneId, runtime);
                if (_runtimes.TryGetValue(paneId.Value, out var current)
                    && ReferenceEquals(current, runtime))
                    _runtimes.Remove(paneId.Value);
            }
        }

        if (runtime is null)
            return;

        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort dispose of the failed occupant.
        }
    }

    /// <summary>
    /// Restore the pane row after a failed occupant replace. Keep the generation bump:
    /// subscribers already saw occupant/pane lifecycle and agent.wait pins on that identity.
    /// Re-register the previous runtime when it still exists. If the occupant is gone,
    /// persist an honest dead row (same fields as <see cref="FailSpawnCleanupAsync"/>).
    /// Waits on <c>_persistGate</c> so a concurrent best-effort persist cannot skip restore.
    /// </summary>
    private async Task RollbackFailedAgentStartAsync(
        PaneId paneId,
        IPaneRuntime? previous,
        bool occupantCommitted,
        string command,
        IReadOnlyList<string> args,
        string lifecycleState,
        AgentStatus agentStatus,
        string? agentKind,
        string? agentMessage,
        PaneAgentAuthority? agentAuthority,
        NativeAgentSessionRef? agentSession,
        IReadOnlyDictionary<string, long> agentAuthoritySequences,
        bool isAlive,
        int? exitCode,
        int? pid)
    {
        try
        {
            await _bindingMutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                try
                {
                    if (_state.GetPane(paneId) is null)
                        return;

                    var frozen = SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState);
                    var occupantStillMapped = previous is not null && !occupantCommitted;
                    if (occupantStillMapped)
                        RestoreRuntimeIfAbsent(paneId, previous);

                    IPaneRuntime? registered = null;
                    lock (_gate)
                        _runtimes.TryGetValue(paneId.Value, out registered);
                    var hasRuntime = registered is not null;

                    if (!hasRuntime)
                    {
                        UpdatePaneEmittingStatus(paneId, pane =>
                        {
                            var next = pane with
                            {
                                Command = frozen ? pane.Command : command,
                                Args = frozen ? pane.Args : args,
                                AgentKind = agentKind,
                                AgentMessage = agentMessage,
                                LifecycleState = PaneLifecycle.Exited,
                                IsAlive = false,
                                ExitCode = pane.ExitCode ?? exitCode ?? -1,
                                Pid = null,
                                AgentAuthority = occupantCommitted ? pane.AgentAuthority : agentAuthority,
                                AgentSession = occupantCommitted ? pane.AgentSession : agentSession,
                                AgentAuthoritySequences = occupantCommitted
                                    ? pane.AgentAuthoritySequences
                                    : agentAuthoritySequences,
                            };
                            return next with { AgentStatus = StatusAfterProcessDeath(next) };
                        });
                        if (frozen)
                            return;
                    }
                    else
                    {
                        var live = registered!.IsAlive;
                        UpdatePaneEmittingStatus(paneId, pane =>
                        {
                            var next = pane with
                            {
                                Command = frozen ? pane.Command : command,
                                Args = frozen ? pane.Args : args,
                                AgentKind = agentKind,
                                AgentMessage = agentMessage,
                                LifecycleState = live ? lifecycleState : PaneLifecycle.Exited,
                                IsAlive = live,
                                ExitCode = live ? exitCode : registered.ExitCode ?? exitCode ?? pane.ExitCode ?? -1,
                                Pid = live ? registered.Pid ?? pid : null,
                                AgentAuthority = occupantCommitted ? pane.AgentAuthority : agentAuthority,
                                AgentSession = occupantCommitted ? pane.AgentSession : agentSession,
                                AgentAuthoritySequences = occupantCommitted
                                    ? pane.AgentAuthoritySequences
                                    : agentAuthoritySequences,
                            };
                            return next with
                            {
                                AgentStatus = live ? agentStatus : StatusAfterProcessDeath(next),
                            };
                        });
                        if (frozen)
                            return;
                    }

                    await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                        ClearReplacementInFlightUnlocked(paneId.Value);
                }
            }
            finally
            {
                _bindingMutationGate.Release();
            }
        }
        catch (ControlPlaneException)
        {
            // Best-effort rollback after a failed agent.start.
        }
    }

    private void RestoreMovedPane(PaneId paneId, TabState sourceBefore, TabState destBefore)
    {
        if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
            return;
        _ = _state.MovePaneToTab(paneId, sourceBefore.Id);
        _state.SetTabLayout(
            sourceBefore.Id,
            sourceBefore.LayoutRoot,
            zoomed: sourceBefore.Zoomed,
            zoomedPaneId: sourceBefore.ZoomedPaneId,
            focusedPaneId: sourceBefore.FocusedPaneId);
        _state.SetTabLayout(
            destBefore.Id,
            destBefore.LayoutRoot,
            zoomed: destBefore.Zoomed,
            zoomedPaneId: destBefore.ZoomedPaneId,
            focusedPaneId: destBefore.FocusedPaneId);
    }

    private async Task RollbackSpawnedPaneAsync(PaneId paneId, CancellationToken ct)
    {
        try
        {
            await PaneCloseAsync(new PaneCloseParams { PaneId = paneId.Value }, null, ct).ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
            // Best-effort rollback after a failed split/apply.
        }
    }

    private async Task RollbackAppliedTabAsync(TabId tabId, CancellationToken ct, TabId? restoreFocusTabId = null)
    {
        var tab = _state.GetTab(tabId);
        if (tab is null)
            return;
        foreach (var paneId in tab.PaneIds.ToList())
        {
            try
            {
                await PaneCloseAsync(new PaneCloseParams { PaneId = paneId.Value }, null, ct).ConfigureAwait(false);
            }
            catch (ControlPlaneException)
            {
                // Best-effort rollback after a failed apply.
            }
        }

        if (_state.IsLastTabInWorkspace(tabId))
            return;
        try
        {
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
                {
                    _ = _state.CloseTab(tabId);
                    if (restoreFocusTabId is { } restore && _state.GetTab(restore) is not null)
                        _ = _state.FocusTab(restore);
                }
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            await PersistGraphAsync(requireDurable: false, removeMissingPanes: true).ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
            // Best-effort rollback after a failed apply.
        }
    }

    private TabState ResolveTabFromPaneOrFocus(string? paneId, string? tabId = null)
    {
        if (!string.IsNullOrWhiteSpace(tabId))
        {
            return _state.GetTab(new TabId(tabId))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Tab not found: {tabId}");
        }

        if (!string.IsNullOrWhiteSpace(paneId))
        {
            var pane = _state.GetPane(new PaneId(paneId))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
            return _state.GetTab(pane.TabId)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Tab not found");
        }

        var snap = _state.Snapshot();
        var ws = snap.FocusedWorkspaceId is { } fw && snap.Workspaces.TryGetValue(fw.Value, out var focused)
            ? focused
            : snap.Workspaces.Values.FirstOrDefault();
        if (ws is null)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Workspace not found");
        var focusedTab = ws.FocusedTabId ?? ws.TabIds.FirstOrDefault();
        if (focusedTab.Value is null)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Tab not found");
        return _state.GetTab(focusedTab)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Tab not found");
    }

    private async Task EmitTabLifecycleAsync(
        string tabId,
        string action,
        string workspaceId,
        CancellationToken ct,
        IClientConnection? connection = null)
    {
        if (_journal is null)
            return;
        var requestId = connection is ClientConnection cc ? cc.ActiveRequestId : null;
        var payload = RuntimeEventPayloadJson.WriteTabLifecycle(tabId, action, workspaceId, requestId);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.TabLifecycle, payload);
        await PublishReliableAsync(
            EventClass.Lifecycle,
            ProtocolEventTypes.TabLifecycle,
            payload,
            ct,
            notifyPlugin: true,
            sequenceConnection: connection).ConfigureAwait(false);
    }

    internal async Task EmitSettingsChangedAfterReloadAsync(
        AttachClientConfig previous,
        AttachClientConfig current,
        CancellationToken ct)
    {
        await EmitSettingIfChangedAsync(
                SettingsValuePolicy.ThemeNameKey,
                previous.Theme.Name,
                current.Theme.Name,
                ct)
            .ConfigureAwait(false);
        await EmitSettingIfChangedAsync(
                "ui.status_indicators",
                previous.Ui.StatusIndicators.ToString(),
                current.Ui.StatusIndicators.ToString(),
                ct)
            .ConfigureAwait(false);
        await EmitSettingIfChangedAsync(
                "experimental.pane_history",
                previous.Experimental.PaneHistory ? "true" : "false",
                current.Experimental.PaneHistory ? "true" : "false",
                ct)
            .ConfigureAwait(false);
    }

    private async Task EmitSettingIfChangedAsync(
        string key,
        string? previous,
        string? current,
        CancellationToken ct)
    {
        if (string.Equals(previous, current, StringComparison.Ordinal))
            return;
        var projected = SettingsValuePolicy.Project(key, current);
        var payload = RuntimeEventPayloadJson.WriteSettingsChanged(
            key, projected.Value, projected.ValueKind, projected.Digest);
        _ = await TryEmitMappedAsync(ProtocolEventTypes.SettingsChanged, payload, ct)
            .ConfigureAwait(false);
    }

    internal async Task EmitOverlayLifecycleIfChangedAsync(
        string? previous,
        string next,
        CancellationToken ct)
    {
        var prevToken = NormalizeClientMode(previous);
        var nextToken = NormalizeClientMode(next);
        var prevOverlay = IsOverlayClientMode(prevToken);
        var nextOverlay = IsOverlayClientMode(nextToken);
        if (prevOverlay && (!nextOverlay || !string.Equals(prevToken, nextToken, StringComparison.Ordinal)))
        {
            var payload = RuntimeEventPayloadJson.WriteOverlayLifecycle(
                prevToken, ProcessLogEvents.OutcomeClosed);
            _ = await TryEmitMappedAsync(ProtocolEventTypes.OverlayLifecycle, payload, ct)
                .ConfigureAwait(false);
        }

        if (nextOverlay && (!prevOverlay || !string.Equals(prevToken, nextToken, StringComparison.Ordinal)))
        {
            var payload = RuntimeEventPayloadJson.WriteOverlayLifecycle(
                nextToken, ProcessLogEvents.OutcomeOpened);
            _ = await TryEmitMappedAsync(ProtocolEventTypes.OverlayLifecycle, payload, ct)
                .ConfigureAwait(false);
        }
    }

    private static string NormalizeClientMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? "terminal" : mode.Trim().ToLowerInvariant();

    private static bool IsOverlayClientMode(string token) =>
        token is not "terminal" and not "prefix";

    /// <summary>
    /// Refuse unmapped wire types at emit. Do not journal them.
    /// Live-only types are not journaled on this path.
    /// </summary>
    internal async Task<bool> TryEmitMappedAsync(
        string type,
        string payloadJson,
        CancellationToken ct)
    {
        if (!EventClassMap.TryFromWireType(type, out var mapped))
            return false;
        if (mapped == EventClass.Render)
            return false;
        if (type is ProtocolEventTypes.ConfigReloaded
            or ProtocolEventTypes.PaneInputRejected
            or ProtocolEventTypes.TerminalRender
            or ProtocolEventTypes.WorkspaceMetadataUpdated
            or ProtocolEventTypes.PaneMetadataUpdated)
        {
            return false;
        }

        if (_journal is null)
            return true;

        payloadJson = _redactor.RedactJsonPayload(type, payloadJson);
        return await PublishReliableAsync(
            mapped,
            type,
            payloadJson,
            ct,
            notifyPlugin: true).ConfigureAwait(false);
    }

    private async Task EmitPaneMovedAsync(string paneId, string tabId, CancellationToken ct)
    {
        if (_journal is null)
            return;
        var payload = RuntimeEventPayloadJson.WritePaneMoved(paneId, tabId);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PaneLifecycle, payload);
        await PublishReliableAsync(
            EventClass.Lifecycle,
            ProtocolEventTypes.PaneLifecycle,
            payload,
            ct).ConfigureAwait(false);
    }

    private sealed class CommandOverlayTracker
    {
        public required CustomCommandOverlayState State { get; init; }

        public bool Armed;

        public bool ExitSeen;

        public bool CloseInFlight;
    }

    private void RegisterCommandOverlay(CustomCommandOverlayState overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        lock (_gate)
        {
            _commandOverlays[overlay.OverlayPaneId] = new CommandOverlayTracker { State = overlay };
        }
    }

    private void ArmCommandOverlay(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (_commandOverlays.TryGetValue(paneId, out var tracker))
                tracker.Armed = true;
        }
    }

    private CustomCommandOverlayState? TakeCommandOverlay(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return null;
        lock (_gate)
        {
            if (!_commandOverlays.TryGetValue(paneId, out var tracker))
                return null;
            _commandOverlays.Remove(paneId);
            return tracker.State;
        }
    }

    private void PutCommandOverlayBack(CustomCommandOverlayState overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        lock (_gate)
        {
            _commandOverlays[overlay.OverlayPaneId] = new CommandOverlayTracker
            {
                State = overlay,
                Armed = true,
                ExitSeen = true,
                CloseInFlight = false,
            };
        }
    }

    private bool TryBeginCommandOverlayClose(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        lock (_gate)
        {
            if (!_commandOverlays.TryGetValue(paneId, out var tracker))
                return false;
            tracker.ExitSeen = true;
            if (!tracker.Armed || tracker.CloseInFlight)
                return false;
            tracker.CloseInFlight = true;
            return true;
        }
    }

    private void EndCommandOverlayCloseInFlight(string paneId)
    {
        lock (_gate)
        {
            _overlayCloseTasks.Remove(paneId);
            if (_commandOverlays.TryGetValue(paneId, out var tracker))
                tracker.CloseInFlight = false;
        }
    }

    private void ScheduleCommandOverlayClose(string paneId)
    {
        if (!TryBeginCommandOverlayClose(paneId))
            return;
        if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
        {
            EndCommandOverlayCloseInFlight(paneId);
            return;
        }

        _ = ObserveCommandOverlayCloseAsync(paneId);
    }

    private async Task<bool> TryCloseCommandOverlayIfExitedAsync(string paneId)
    {
        var runtimeGone = TryGetRuntime(paneId, out var runtime)
            && runtime is { IsAlive: false };
        if (!runtimeGone)
        {
            lock (_gate)
            {
                if (!_commandOverlays.TryGetValue(paneId, out var tracker) || !tracker.ExitSeen)
                    return false;
            }
        }

        if (!TryBeginCommandOverlayClose(paneId))
            return false;
        if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
        {
            EndCommandOverlayCloseInFlight(paneId);
            return false;
        }

        await ObserveCommandOverlayCloseAsync(paneId).ConfigureAwait(false);
        return true;
    }

    private async Task DrainPendingCommandOverlayClosesAsync()
    {
        Task[] inFlight;
        lock (_gate)
            inFlight = [.. _overlayCloseTasks.Values];
        if (inFlight.Length > 0)
            await Task.WhenAll(inFlight).ConfigureAwait(false);

        List<string> pending;
        lock (_gate)
        {
            pending = [];
            foreach (var pair in _commandOverlays.ToArray())
            {
                if (!pair.Value.Armed || !pair.Value.ExitSeen || pair.Value.CloseInFlight)
                    continue;
                pair.Value.CloseInFlight = true;
                pending.Add(pair.Key);
            }
        }

        foreach (var paneId in pending)
            await ObserveCommandOverlayCloseAsync(paneId).ConfigureAwait(false);
    }

    private async Task ObserveCommandOverlayCloseAsync(string paneId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _overlayCloseTasks[paneId] = tcs.Task;
        try
        {
            await CloseCommandOverlayFromExitAsync(paneId).ConfigureAwait(false);
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            EndCommandOverlayCloseInFlight(paneId);
        }
    }

    private async Task CloseCommandOverlayFromExitAsync(string paneId)
    {
        try
        {
            var delay = DelayCommandOverlayCloseAsync;
            if (delay is not null)
                await delay(paneId).ConfigureAwait(false);

            if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
                return;

            if (_state.GetPane(new PaneId(paneId)) is not null)
            {
                try
                {
                    await PaneCloseAsync(
                            new PaneCloseParams { PaneId = paneId },
                            null,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                }
                catch (ControlPlaneException ex)
                    when (ex.Code == ProtocolErrorCodes.InvalidState
                        && ex.Message.Contains("frozen_read_only", StringComparison.Ordinal))
                {
                    return;
                }
                catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.NotFound)
                {
                    // Overlay row is already gone. Restore leftover tracker if present.
                }
            }

            if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
                return;

            var overlay = TakeCommandOverlay(paneId);
            if (overlay is null)
                return;
            try
            {
                await RestoreCommandOverlayAsync(overlay, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                PutCommandOverlayBack(overlay);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Custom-command overlay close failed");
        }
    }

    private void ApplyCommandOverlayRestoreUnlocked(CustomCommandOverlayState overlay)
    {
        var tab = _state.GetTab(new TabId(overlay.TabId));
        if (tab is null)
            return;

        var restorePane = _state.GetPane(new PaneId(overlay.RestorePaneId));
        var zoomTarget = overlay.RestoreZoomed && overlay.RestoreZoomedPaneId is { } zoomId
            ? _state.GetPane(new PaneId(zoomId))
            : null;
        var remaining = tab.PaneIds.Count;
        var canZoom = overlay.RestoreZoomed && zoomTarget is not null && remaining > 1;
        _state.UpdateTab(tab.Id, t => t with
        {
            FocusedPaneId = restorePane?.Id ?? t.FocusedPaneId,
            Zoomed = canZoom,
            ZoomedPaneId = canZoom ? zoomTarget!.Id : null,
        });
    }

    private async Task RestoreCommandOverlayAsync(
        CustomCommandOverlayState overlay,
        CancellationToken ct)
    {
        if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
        {
            PutCommandOverlayBack(overlay);
            return;
        }

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation("pane.close");
            ApplyCommandOverlayRestoreUnlocked(overlay);
        }
        catch (ControlPlaneException)
        {
            PutCommandOverlayBack(overlay);
            throw;
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        try
        {
            await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
            await EmitLayoutUpdatedAsync(overlay.TabId, overlay.RestorePaneId, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            PutCommandOverlayBack(overlay);
            throw;
        }
    }
}
