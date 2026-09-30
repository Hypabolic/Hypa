using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal async Task<JsonElement> HandlePaneShowAsync(
        PaneShowParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneShowAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandlePaneHideAsync(
        PaneHideParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneHideAsync(p, connection, ct).ConfigureAwait(false));

    private async Task<JsonObject> PaneShowAsync(
        PaneShowParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.show");
        var paneId = RequireField(p.PaneId, "pane_id");
        var mode = string.IsNullOrWhiteSpace(p.Mode) ? PanePlacementWire.Tiled : p.Mode.Trim();
        if (string.Equals(mode, PaneOverlayWire.Overlay, StringComparison.OrdinalIgnoreCase))
            return await PaneShowOverlayAsync(p, connection, ct).ConfigureAwait(false);

        if (!string.Equals(mode, PanePlacementWire.Tiled, StringComparison.OrdinalIgnoreCase))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                $"unknown mode: {mode}");
        }

        var ratio = p.Ratio ?? 0.5;
        var request = new PlacementAuthorityRequest
        {
            PaneId = new PaneId(paneId),
            OccupantToken = EmptyToNull(p.OccupantToken),
            ParentCapability = EmptyToNull(p.ParentCapability),
            LeaseId = EmptyToNull(p.LeaseId),
            HolderId = connection?.ConnectionId,
            Sequence = p.Seq,
            Mode = mode,
            AttachClientId = EmptyToNull(p.AttachClientId),
        };

        var shown = await RunVisibilityMutationAsync(
                paneId,
                request,
                connection,
                () => ShowTiledOrNewTab(p, paneId, ratio),
                mode,
                ct)
            .ConfigureAwait(false);
        ClearOverlayAfterTiledShow(paneId);
        return shown;
    }

    private async Task<JsonObject> PaneHideAsync(
        PaneHideParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.hide");
        var paneId = RequireField(p.PaneId, "pane_id");
        _ = p.Mode;
        var overlayHide = await TryHideOverlayAsync(p, connection, ct).ConfigureAwait(false);
        if (overlayHide is not null)
            return overlayHide;

        var request = new PlacementAuthorityRequest
        {
            PaneId = new PaneId(paneId),
            OccupantToken = EmptyToNull(p.OccupantToken),
            ParentCapability = EmptyToNull(p.ParentCapability),
            LeaseId = EmptyToNull(p.LeaseId),
            HolderId = connection?.ConnectionId,
            Sequence = p.Seq,
            Mode = PanePlacementWire.Tiled,
            AttachClientId = EmptyToNull(p.AttachClientId),
        };

        return await RunVisibilityMutationAsync(
                paneId,
                request,
                connection,
                () => _visibility.HideTiled(new PaneHideRequest { PaneId = new PaneId(paneId) }),
                PanePlacementWire.Tiled,
                ct)
            .ConfigureAwait(false);
    }

    private Result<PaneVisibilityChange, PaneVisibilityError> ShowTiledOrNewTab(
        PaneShowParams p,
        string paneId,
        double ratio)
    {
        var pane = _state.GetPane(new PaneId(paneId));
        if (pane is null)
        {
            return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                PaneVisibilityError.NotFound($"Pane not found: {paneId}"));
        }

        var destTabId = string.IsNullOrWhiteSpace(p.TabId) ? pane.TabId : new TabId(p.TabId);
        var dest = _state.GetTab(destTabId);
        if (dest is null)
        {
            return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                PaneVisibilityError.NotFound($"Tab not found: {destTabId.Value}"));
        }

        if (!LayoutNode.IsValidRatio(ratio))
        {
            return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                PaneVisibilityError.InvalidTarget("ratio must be in (0,1)"));
        }

        var hasSplit = !string.IsNullOrWhiteSpace(p.Direction)
            || !string.IsNullOrWhiteSpace(p.TargetPaneId);
        if (hasSplit && !string.IsNullOrWhiteSpace(p.TargetPaneId))
        {
            var target = new PaneId(p.TargetPaneId);
            if (dest.LayoutRoot is null || !LayoutTreeOperations.ContainsPane(dest.LayoutRoot, target))
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                    PaneVisibilityError.InvalidTarget($"Pane not found in layout: {target.Value}"));
            }
        }

        var destOccupied = dest.LayoutRoot is not null || dest.PaneIds.Count > 0;
        var revealHidden = pane.Placement == PanePlacement.Hidden && destOccupied && !hasSplit;
        TabId? created = null;
        WorkspaceId? priorWorkspace = null;
        TabId? priorTab = null;
        if (revealHidden)
        {
            var snap = _state.Snapshot();
            priorWorkspace = snap.FocusedWorkspaceId;
            if (priorWorkspace is { } focusedWorkspace)
                priorTab = _state.GetWorkspace(focusedWorkspace)?.FocusedTabId;
            try
            {
                var tab = _state.CreateTab(dest.WorkspaceId, focus: p.Focus ?? true);
                created = tab.Id;
                destTabId = tab.Id;
                if (priorTab is { } openedFrom && openedFrom.Value != tab.Id.Value)
                    _state.UpdateTab(tab.Id, current => current with { OpenedFromTabId = openedFrom });
            }
            catch (InvalidOperationException ex)
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                    PaneVisibilityError.InvalidTarget(ex.Message));
            }
        }

        var result = _visibility.ShowTiled(new PaneShowTiledRequest
        {
            PaneId = new PaneId(paneId),
            TabId = destTabId,
            TargetPaneId = string.IsNullOrWhiteSpace(p.TargetPaneId) ? null : new PaneId(p.TargetPaneId),
            Direction = p.Direction,
            Ratio = ratio,
            Focus = p.Focus ?? true,
        });
        if (!result.IsOk && created is { } extra)
        {
            _state.CloseTab(extra);
            _state.RestoreFocus(priorWorkspace, priorTab);
            return result;
        }

        if (result.IsOk && (p.Focus ?? true))
            _state.FocusTab(result.Value.TargetTabId);

        if (result.IsOk && created is { } createdTab)
            return Result<PaneVisibilityChange, PaneVisibilityError>.Ok(
                result.Value with { CreatedTabId = createdTab });
        return result;
    }

    private void ClearOverlayAfterTiledShow(string paneId)
    {
        var overlayOwner = _overlay.OwnerOf(new PaneId(paneId));
        if (overlayOwner is null)
            return;
        var cleared = _overlay.Hide(new PaneHideOverlayRequest
        {
            PaneId = new PaneId(paneId),
            AttachClientId = overlayOwner.AttachClientId,
        });
        if (cleared.IsOk && cleared.Value.Changed)
        {
            ResetOverlayRenderIdentity(paneId);
            PublishOverlayVisibleSet(overlayOwner.AttachClientId, overlayPaneId: null);
        }
    }

    /// <summary>
    /// Lock order: pane side-effect → binding (once). Reset Full identity
    /// before persist. Sequence commits only after a durable write. Emit
    /// uses the journal then non-blocking live post. Do not re-enter binding.
    /// </summary>
    private async Task<JsonObject> RunVisibilityMutationAsync(
        string paneId,
        PlacementAuthorityRequest request,
        IClientConnection? connection,
        Func<Result<PaneVisibilityChange, PaneVisibilityError>> mutate,
        string mode,
        CancellationToken ct)
    {
        var side = GetPaneSideEffectLock(paneId);
        await side.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PlacementApplyResult<PaneVisibilityChange, PaneVisibilityError> applied;
            PaneVisibilityChange? published = null;
            VisibilityRestorePoint? restore = null;
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureAttachPaneMutation(connection, mode == PanePlacementWire.Tiled ? "pane.show" : "pane.hide");
                EnsureNotFrozenForMutation("pane.visibility");
                restore = _state.CaptureVisibility(new PaneId(paneId));
                var outcome = _placement.Apply(request, mutate, _overlayFence, commitSequence: false);
                if (!outcome.IsOk)
                    throw ToPlacementException(outcome.Error, request);
                applied = outcome.Value;
                if (applied.MutationError is { } graph)
                    throw ToVisibilityException(graph);

                if (applied is { Changed: true, Value: { } value } && restore is not null)
                {
                    TabId? opTab = null;
                    if (_state.Snapshot().FocusedWorkspaceId is { } focusedWorkspace
                        && _state.GetWorkspace(focusedWorkspace) is { } workspace)
                    {
                        opTab = workspace.FocusedTabId;
                    }

                    restore = restore with
                    {
                        OperationFocusedTabId = opTab,
                        OperationFocusedPaneId = _state.GetTab(value.TargetTabId)?.FocusedPaneId,
                    };
                }

                if (applied is { Changed: true, Value: { Changed: true } change })
                {
                    ForgetPostedFull(paneId);
                    _renderCoalescer.Cancel(paneId);
                    VisibilityResetObservedForTests?.Invoke(paneId);
                    published = change;
                }
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            if (published is { } ready)
            {
                if (VisibilityAfterMutationBeforePublishForTests is { } delay)
                    await delay().ConfigureAwait(false);

                try
                {
                    await PersistGraphAsync(
                            requireDurable: true,
                            removeMissingPanes: ready.ClosedTabId is not null)
                        .ConfigureAwait(false);
                }
                catch
                {
                    if (restore is not null)
                    {
                        await _bindingMutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try
                        {
                            _state.RevertVisibility(restore, ready.CreatedTabId);
                        }
                        finally
                        {
                            _bindingMutationGate.Release();
                        }
                    }

                    throw;
                }

                if (_state.GetPane(new PaneId(paneId)) is null)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.NotFound,
                        $"Pane not found: {paneId}");
                }

                if (applied.Sequence is { } seq && applied.Grant is { } grant)
                    _placement.CommitSequence(grant, seq);

                // Request cancel after commit must not drop a journalled change.
                await EmitPanePlacementChangedAsync(ready, mode, CancellationToken.None)
                    .ConfigureAwait(false);
                if (ready.ClosedTabId is { } closedTab)
                {
                    var workspaceId = ready.Pane.WorkspaceId.Value;
                    await EmitTabLifecycleAsync(closedTab.Value, "closed", workspaceId, CancellationToken.None)
                        .ConfigureAwait(false);
                    ReconcileAttachClientViews();
                    var focused = _state.GetWorkspace(ready.Pane.WorkspaceId)?.FocusedTabId;
                    if (focused?.Value == ready.TargetTabId.Value
                        && ready.TargetTabId.Value != closedTab.Value)
                    {
                        await EmitTabLifecycleAsync(
                                ready.TargetTabId.Value,
                                "focused",
                                workspaceId,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }

                await EmitLayoutUpdatedAsync(
                        ready.TargetTabId.Value,
                        ready.Pane.Id.Value,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return VisibilityBody(ready.Pane, mode, changed: true);
            }

            if (applied.Changed && applied.Sequence is { } accepted && applied.Grant is { } granted)
                _placement.CommitSequence(granted, accepted);

            var pane = applied.Value?.Pane ?? _state.GetPane(new PaneId(paneId));
            if (pane is null)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    $"Pane not found: {paneId}");
            }

            return VisibilityBody(pane, mode, changed: false);
        }
        finally
        {
            side.Release();
        }
    }

    private static JsonObject VisibilityBody(PaneState pane, string mode, bool changed) =>
        new()
        {
            ["ok"] = true,
            ["changed"] = changed,
            ["pane_id"] = pane.Id.Value,
            ["tab_id"] = pane.TabId.Value,
            ["workspace_id"] = pane.WorkspaceId.Value,
            ["placement"] = PanePlacementWire.ToWire(pane.Placement),
            ["hidden"] = pane.Placement == PanePlacement.Hidden,
            ["mode"] = mode,
            ["cols"] = pane.Cols,
            ["rows"] = pane.Rows,
        };

    private async Task EmitPanePlacementChangedAsync(
        PaneVisibilityChange change,
        string mode,
        CancellationToken ct)
    {
        var pane = change.Pane;
        var payload = RuntimeEventPayloadJson.WritePanePlacementChanged(
            pane.Id.Value,
            pane.TabId.Value,
            pane.WorkspaceId.Value,
            PanePlacementWire.ToWire(change.From),
            PanePlacementWire.ToWire(change.To),
            mode);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PanePlacementChanged, payload);
        await EmitReliableAsync(
                EventClass.Lifecycle,
                ProtocolEventTypes.PanePlacementChanged,
                payload,
                ct)
            .ConfigureAwait(false);
    }

    private static ControlPlaneException ToPlacementException(
        PlacementAuthorityError error,
        PlacementAuthorityRequest request)
    {
        var leaseOnly = string.IsNullOrWhiteSpace(request.OccupantToken)
            && string.IsNullOrWhiteSpace(request.ParentCapability);

        if (error.Code == PlacementAuthorityError.MissingSequence.Code
            || error.Code == PlacementAuthorityError.UnprovenParent.Code
            || error.Code == PlacementAuthorityError.MissingAttachClient.Code)
        {
            return new ControlPlaneException(ProtocolErrorCodes.InvalidParams, error.Message);
        }

        if (error.Code == PlacementAuthorityError.OverlayBusy.Code)
            return new ControlPlaneException(ProtocolErrorCodes.Fenced, error.Message);

        if (leaseOnly
            && (error.Code == PlacementAuthorityError.ForeignCredential.Code
                || error.Code == PlacementAuthorityError.ForgedIdentity.Code))
        {
            return new ControlPlaneException(
                ProtocolErrorCodes.LeaseRequired,
                "Controller lease required");
        }

        return new ControlPlaneException(ProtocolErrorCodes.CapabilityInvalid, error.Message);
    }

    private static ControlPlaneException ToVisibilityException(PaneVisibilityError error) =>
        error.Code == PaneVisibilityError.NotFoundCode
            ? new ControlPlaneException(ProtocolErrorCodes.NotFound, error.Message)
            : new ControlPlaneException(ProtocolErrorCodes.InvalidParams, error.Message);
}
