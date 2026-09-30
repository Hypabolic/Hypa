using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Microsoft.Extensions.Logging;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal async Task<JsonElement> HandleUiClientModeAsync(
        UiClientModeParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "ui.client_mode requires a live connection");
        var mode = RequireField(p.ClientMode, "client_mode");
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        string? previous;
        try
        {
            lock (_gate)
                _attachClientModes.TryGetValue(connection.ConnectionId, out previous);
            RejectBusyModeWhileReserved(connection.ConnectionId, mode);
            NoteAttachClientMode(connection.ConnectionId, mode);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await EmitOverlayLifecycleIfChangedAsync(previous, mode, ct).ConfigureAwait(false);

        return Ok(new JsonObject
        {
            ["ok"] = true,
            ["attach_client_id"] = connection.ConnectionId,
            ["client_mode"] = mode.Trim().ToLowerInvariant(),
        });
    }

    /// <summary>
    // / slot.
    /// only when that slot is set. A reserved overlay must accept
    /// <c>overlay</c> and <c>terminal</c> publication. Other busy tokens
    /// wait on the same binding gate as show.
    /// </summary>
    private void RejectBusyModeWhileReserved(string connectionId, string? mode)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(mode))
            return;
        if (!_overlay.HasReservation(connectionId))
            return;
        var token = mode.Trim().ToLowerInvariant();
        if (string.Equals(token, "terminal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "overlay", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsBusyClientMode(token))
            throw UiBusy();
    }

    private IReadOnlyList<JsonObject> ListAttachClients()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clients = new List<JsonObject>();
        foreach (var attachment in _attachments.ListAll())
        {
            if (!seen.Add(attachment.ConnectionId))
                continue;
            clients.Add(AttachClientJson(attachment.ConnectionId, attachment.Mode));
        }

        foreach (var pane in _state.Snapshot().Panes.Values)
        {
            var lease = _leases.GetActive(pane.Id.Value, LeaseScopes.Input);
            if (lease is null || string.IsNullOrWhiteSpace(lease.HolderId))
                continue;
            if (!seen.Add(lease.HolderId))
                continue;
            clients.Add(AttachClientJson(lease.HolderId, AttachmentModes.Control));
        }

        IReadOnlyList<string> published;
        lock (_gate)
            published = _attachClientModes.Keys.ToArray();
        foreach (var connectionId in published)
        {
            if (!seen.Add(connectionId))
                continue;
            clients.Add(AttachClientJson(connectionId, AttachmentModes.Control));
        }

        return clients;
    }

    private JsonObject AttachClientJson(string attachClientId, string mode)
    {
        string? clientMode;
        lock (_gate)
            _attachClientModes.TryGetValue(attachClientId, out clientMode);
        return new JsonObject
        {
            ["attach_client_id"] = attachClientId,
            ["mode"] = mode,
            ["client_mode"] = clientMode,
        };
    }

    private bool IsLiveAttachClient(string attachClientId)
    {
        if (string.IsNullOrWhiteSpace(attachClientId))
            return false;
        foreach (var attachment in _attachments.ListAll())
        {
            if (string.Equals(attachment.ConnectionId, attachClientId, StringComparison.Ordinal))
                return true;
        }

        if (ConnectionHoldsInputLease(attachClientId))
            return true;
        lock (_gate)
            return _attachClientModes.ContainsKey(attachClientId);
    }

    private bool OverlayOwnerBusy(string attachClientId, string? paneId = null)
    {
        var existing = _overlay.TryGet(attachClientId);
        if (existing is not null
            && (string.IsNullOrWhiteSpace(paneId)
                || !string.Equals(existing.PaneId.Value, paneId, StringComparison.Ordinal)))
        {
            return true;
        }

        if (PopupIsOpen)
            return true;

        string? published;
        lock (_gate)
        {
            if (!_publishedAttachClientModes.Contains(attachClientId))
                return true;
            _attachClientModes.TryGetValue(attachClientId, out published);
        }

        if (string.IsNullOrWhiteSpace(published))
            return true;
        if (string.Equals(published, "overlay", StringComparison.OrdinalIgnoreCase)
            && existing is not null
            && (string.IsNullOrWhiteSpace(paneId)
                || string.Equals(existing.PaneId.Value, paneId, StringComparison.Ordinal)))
        {
            return false;
        }

        return IsBusyClientMode(published);
    }

    private void RejectForeignOverlayOwner(string paneId, string attachClientId)
    {
        var existing = _overlay.OwnerOf(new PaneId(paneId));
        if (existing is not null
            && !string.Equals(existing.AttachClientId, attachClientId, StringComparison.Ordinal))
        {
            throw UiBusy();
        }
    }

    private bool OverlayReservationExists()
    {
        foreach (var _ in _overlay.ListOwners())
            return true;
        return false;
    }

    private async Task<JsonObject> PaneShowOverlayAsync(
        PaneShowParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.show");
        var paneId = RequireField(p.PaneId, "pane_id");
        var leaseId = EmptyToNull(p.LeaseId);
        if (leaseId is null
            && EmptyToNull(p.OccupantToken) is null
            && EmptyToNull(p.ParentCapability) is null)
        {
            RequireField(p.LeaseId, "lease_id");
        }

        if (string.IsNullOrWhiteSpace(p.AttachClientId))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "overlay mode requires attach_client_id");
        }

        var attachClientId = p.AttachClientId.Trim();
        if (!IsLiveAttachClient(attachClientId))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "unknown attach client");
        }

        if (OverlayOwnerBusy(attachClientId, paneId))
            throw UiBusy();
        RejectForeignOverlayOwner(paneId, attachClientId);

        var request = new PlacementAuthorityRequest
        {
            PaneId = new PaneId(paneId),
            OccupantToken = EmptyToNull(p.OccupantToken),
            ParentCapability = EmptyToNull(p.ParentCapability),
            LeaseId = leaseId,
            HolderId = connection?.ConnectionId,
            Sequence = p.Seq,
            Mode = PaneOverlayWire.Overlay,
            AttachClientId = attachClientId,
        };
        return await RunOverlayMutationAsync(
                paneId,
                attachClientId,
                request,
                () =>
                {
                    if (OverlayOwnerBusy(attachClientId, paneId))
                        throw UiBusy();
                    RejectForeignOverlayOwner(paneId, attachClientId);
                    if (leaseId is not null)
                        AuthorizeResize(paneId, leaseId, connection?.ConnectionId);
                    return _overlay.Show(new PaneShowOverlayRequest
                    {
                        PaneId = new PaneId(paneId),
                        AttachClientId = attachClientId,
                        AreaCols = p.AreaCols,
                        AreaRows = p.AreaRows,
                    });
                },
                async change =>
                {
                    if (!change.Changed || p.AreaCols is not > 0 || p.AreaRows is not > 0)
                        return change;
                    await ResizeOverlayInnerAsync(
                            paneId,
                            change.Pane,
                            p.AreaCols.Value,
                            p.AreaRows.Value,
                            ct)
                        .ConfigureAwait(false);
                    return change with { Pane = _state.GetPane(new PaneId(paneId)) ?? change.Pane };
                },
                overlayPaneId: paneId,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<JsonObject?> TryHideOverlayAsync(
        PaneHideParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        var paneId = RequireField(p.PaneId, "pane_id");
        var explicitAttach = EmptyToNull(p.AttachClientId);
        var owner = _overlay.OwnerOf(new PaneId(paneId));
        var leaseId = EmptyToNull(p.LeaseId);
        var hasPlacement = leaseId is not null
            || EmptyToNull(p.OccupantToken) is not null
            || EmptyToNull(p.ParentCapability) is not null;
        if (owner is null)
        {
            if (!hasPlacement)
                return OverlayHideUnchanged(paneId);
            return null;
        }

        if (explicitAttach is not null
            && !string.Equals(owner.AttachClientId, explicitAttach, StringComparison.Ordinal))
        {
            if (!hasPlacement)
                throw UiBusy();
            return null;
        }

        if (!hasPlacement)
        {
            return await CancelOwnedOverlayReservationAsync(
                    paneId,
                    p.OverlayGeneration,
                    connection,
                    ct)
                .ConfigureAwait(false);
        }

        var request = new PlacementAuthorityRequest
        {
            PaneId = new PaneId(paneId),
            OccupantToken = EmptyToNull(p.OccupantToken),
            ParentCapability = EmptyToNull(p.ParentCapability),
            LeaseId = leaseId,
            HolderId = connection?.ConnectionId,
            Sequence = p.Seq,
            Mode = PaneOverlayWire.Overlay,
            AttachClientId = owner.AttachClientId,
        };
        return await RunOverlayMutationAsync(
                paneId,
                owner.AttachClientId,
                request,
                () =>
                {
                    if (leaseId is not null)
                        AuthorizeResize(paneId, leaseId, connection?.ConnectionId);
                    return _overlay.Hide(new PaneHideOverlayRequest
                    {
                        PaneId = new PaneId(paneId),
                        AttachClientId = owner.AttachClientId,
                        Generation = p.OverlayGeneration,
                    });
                },
                afterMutate: null,
                overlayPaneId: null,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    // / Owner cancel.
    /// exclusive surface before the next modal. The live connection
    /// must own this pane and generation. No lease. No foreign hide.
    /// </summary>
    private async Task<JsonObject> CancelOwnedOverlayReservationAsync(
        string paneId,
        long? generation,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (connection is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "pane.hide owner cancel requires a live connection");
        }

        if (generation is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "overlay_generation is required");
        }

        var side = GetPaneSideEffectLock(paneId);
        await side.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PaneOverlayChange? published = null;
            PaneOverlayOwner? priorOwner = null;
            var operationReleased = false;
            var operationGeneration = 0L;
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureNotFrozenForMutation("pane.overlay");
                var owner = _overlay.OwnerOf(new PaneId(paneId));
                if (owner is null)
                    return OverlayHideUnchanged(paneId);
                if (!string.Equals(owner.AttachClientId, connection.ConnectionId, StringComparison.Ordinal))
                    throw UiBusy();
                if (owner.Generation != generation.Value)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.Fenced,
                        "stale overlay generation");
                }

                priorOwner = owner;
                var pane = _state.GetPane(new PaneId(paneId));
                Result<PaneOverlayChange, PaneOverlayError> result;
                if (pane is null)
                    result = _overlay.ReleaseOwner(owner.AttachClientId);
                else
                {
                    result = _overlay.Hide(new PaneHideOverlayRequest
                    {
                        PaneId = new PaneId(paneId),
                        AttachClientId = owner.AttachClientId,
                        Generation = generation,
                    });
                }

                if (!result.IsOk)
                    throw ToOverlayException(result.Error);
                var operationOwner = _overlay.TryGet(owner.AttachClientId);
                operationReleased = operationOwner is null;
                operationGeneration = operationOwner?.Generation ?? priorOwner.Generation;
                if (result.Value.Changed)
                {
                    ResetOverlayRenderIdentity(paneId);
                    published = result.Value;
                }
                else
                    return OverlayHideUnchanged(paneId, owner.AttachClientId, generation.Value);
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            if (published is not { } ready || priorOwner is null)
                return OverlayHideUnchanged(paneId, priorOwner?.AttachClientId, generation.Value);

            try
            {
                await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
            }
            catch
            {
                await RevertOverlayReservationAsync(
                        paneId,
                        priorOwner.AttachClientId,
                        priorOwner,
                        priorCols: null,
                        priorRows: null,
                        appliedCols: null,
                        appliedRows: null,
                        operationGeneration,
                        operationReleased)
                    .ConfigureAwait(false);
                throw;
            }

            PublishOverlayVisibleSet(priorOwner.AttachClientId, overlayPaneId: null);
            return await FinishOverlayChangeAsync(ready, persist: false, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            side.Release();
        }
    }

    private static JsonObject OverlayHideUnchanged(
        string paneId,
        string? attachClientId = null,
        long generation = 0) =>
        new()
        {
            ["ok"] = true,
            ["changed"] = false,
            ["pane_id"] = paneId,
            ["mode"] = PaneOverlayWire.Hidden,
            ["attach_client_id"] = attachClientId,
            ["overlay_generation"] = generation,
        };

    /// <summary>
    /// Same occupant/parent/holder sequence as tiled show and hide.
    /// Reset Full identity before persist and emit. Do not nest the
    // / binding gate.
    /// composes one host-size frame.
    /// </summary>
    private async Task<JsonObject> RunOverlayMutationAsync(
        string paneId,
        string attachClientId,
        PlacementAuthorityRequest request,
        Func<Result<PaneOverlayChange, PaneOverlayError>> mutate,
        Func<PaneOverlayChange, Task<PaneOverlayChange>>? afterMutate,
        string? overlayPaneId,
        CancellationToken ct)
    {
        var side = GetPaneSideEffectLock(paneId);
        await side.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PlacementApplyResult<PaneOverlayChange, PaneOverlayError> applied;
            PaneOverlayChange? published = null;
            var priorOwner = _overlay.OwnerOf(new PaneId(paneId));
            var priorPane = _state.GetPane(new PaneId(paneId));
            var priorCols = priorPane?.Cols;
            var priorRows = priorPane?.Rows;
            var operationReleased = false;
            var operationGeneration = priorOwner?.Generation ?? 0;
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureNotFrozenForMutation("pane.overlay");
                var outcome = _placement.Apply(request, mutate, _overlayFence, commitSequence: false);
                if (!outcome.IsOk)
                    throw ToPlacementException(outcome.Error, request);
                applied = outcome.Value;
                if (applied.MutationError is { } graph)
                    throw ToOverlayException(graph);

                var operationOwner = _overlay.TryGet(attachClientId);
                operationReleased = priorOwner is not null && operationOwner is null;
                operationGeneration = operationOwner?.Generation ?? priorOwner?.Generation ?? 0;

                if (applied is { Changed: true, Value: { Changed: true } change })
                {
                    ResetOverlayRenderIdentity(paneId);
                    published = change;
                }
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            if (published is { } ready)
            {
                int? appliedCols = null;
                int? appliedRows = null;
                try
                {
                    if (afterMutate is not null)
                    {
                        ready = await afterMutate(ready).ConfigureAwait(false);
                        appliedCols = ready.Pane.Cols;
                        appliedRows = ready.Pane.Rows;
                    }
                }
                catch
                {
                    var partial = _state.GetPane(new PaneId(paneId));
                    await RevertOverlayReservationAsync(
                            paneId,
                            attachClientId,
                            priorOwner,
                            priorCols,
                            priorRows,
                            partial?.Cols,
                            partial?.Rows,
                            operationGeneration,
                            operationReleased)
                        .ConfigureAwait(false);
                    throw;
                }

                try
                {
                    await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
                }
                catch
                {
                    await RevertOverlayReservationAsync(
                            paneId,
                            attachClientId,
                            priorOwner,
                            priorCols,
                            priorRows,
                            appliedCols,
                            appliedRows,
                            operationGeneration,
                            operationReleased)
                        .ConfigureAwait(false);
                    throw;
                }

                if (applied.Sequence is { } seq && applied.Grant is { } grant)
                    _placement.CommitSequence(grant, seq);

                PublishOverlayVisibleSet(attachClientId, overlayPaneId);
                return await FinishOverlayChangeAsync(ready, persist: false, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (applied.Changed && applied.Sequence is { } accepted && applied.Grant is { } granted)
                _placement.CommitSequence(granted, accepted);

            var unchanged = applied.Value
                ?? new PaneOverlayChange
                {
                    Pane = _state.GetPane(new PaneId(paneId))
                        ?? throw new ControlPlaneException(
                            ProtocolErrorCodes.NotFound,
                            $"Pane not found: {paneId}"),
                    AttachClientId = attachClientId,
                    Generation = priorOwner?.Generation ?? 0,
                    Changed = false,
                    Mode = overlayPaneId is null ? PaneOverlayWire.Hidden : PaneOverlayWire.Overlay,
                };
            return await FinishOverlayChangeAsync(unchanged, persist: false, ct).ConfigureAwait(false);
        }
        finally
        {
            side.Release();
        }
    }

    /// <summary>
    /// Restore only the reservation this operation owned, then only the
    // / geometry it applied.
    /// resizes Ghostty to the kept cols and rows. Hide has no geometry
    /// side effect. A later owner or a later same-pane resize must
    /// survive. Compare, graph update, and actual runtime resize stay
    /// in the same binding-gate section so a later
    /// <c>pane.resize</c> cannot split graph and PTY. Do not hold that
    /// gate across store I/O. Runtime rollback uses a bounded
    /// independent cleanup token so a cancelled request cannot stop
    /// cleanup.
    /// </summary>
    private async Task RevertOverlayReservationAsync(
        string paneId,
        string attachClientId,
        PaneOverlayOwner? priorOwner,
        int? priorCols,
        int? priorRows,
        int? appliedCols,
        int? appliedRows,
        long operationGeneration,
        bool operationReleased)
    {
        await _bindingMutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _overlay.TryRevertOwner(
                attachClientId,
                priorOwner,
                new PaneId(paneId),
                operationGeneration,
                operationReleased);

            var owner = _overlay.OwnerOf(new PaneId(paneId));
            if (owner is not null
                && IsLaterOwnerOfOperationPane(owner, attachClientId, priorOwner, operationGeneration))
            {
                return;
            }

            if (appliedCols is not { } appliedW
                || appliedRows is not { } appliedH
                || priorCols is not { } cols
                || priorRows is not { } rows)
            {
                return;
            }

            var current = _state.GetPane(new PaneId(paneId));
            if (current is null || current.Cols != appliedW || current.Rows != appliedH)
                return;

            _state.UpdatePane(
                new PaneId(paneId),
                pane => pane with { Cols = cols, Rows = rows });
            var runtime = PeekRuntime(paneId);
            if (runtime is null)
                return;

            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            cleanup.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await runtime.ResizeAsync(cols, rows, cleanup.Token).ConfigureAwait(false);
            }
            catch
            {
                // Keep the original persist or resize error.
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }
    }

    private static bool IsLaterOwnerOfOperationPane(
        PaneOverlayOwner owner,
        string attachClientId,
        PaneOverlayOwner? priorOwner,
        long operationGeneration)
    {
        if (priorOwner is not null
            && owner.Generation == priorOwner.Generation
            && string.Equals(owner.AttachClientId, priorOwner.AttachClientId, StringComparison.Ordinal))
        {
            return false;
        }

        return owner.Generation != operationGeneration
            || !string.Equals(owner.AttachClientId, attachClientId, StringComparison.Ordinal);
    }

    internal IPaneOverlayService Overlay => _overlay;

    internal IOverlayVisibleSetHook? OverlayVisibleSet => _overlayVisibleSet;

    internal void ReleaseOverlayOwner(string attachClientId)
    {
        var result = _overlay.ReleaseOwner(attachClientId);
        if (!result.IsOk || !result.Value.Changed)
            return;
        ResetOverlayRenderIdentity(result.Value.Pane.Id.Value);
        PublishOverlayVisibleSet(attachClientId, overlayPaneId: null);
        ObserveOverlayFinish(FinishOverlayChangeAsync(result.Value, persist: true, CancellationToken.None));
    }

    internal void ReleaseOverlayPane(string paneId)
    {
        var owner = _overlay.OwnerOf(new PaneId(paneId));
        var result = _overlay.ReleasePane(new PaneId(paneId));
        if (!result.IsOk || !result.Value.Changed)
            return;
        ResetOverlayRenderIdentity(paneId);
        if (owner is not null)
            PublishOverlayVisibleSet(owner.AttachClientId, overlayPaneId: null);
        ObserveOverlayFinish(FinishOverlayChangeAsync(result.Value, persist: true, CancellationToken.None));
    }

    private void ResetOverlayRenderIdentity(string paneId)
    {
        ForgetPostedFull(paneId);
        _renderCoalescer.Cancel(paneId);
    }

    private void ObserveOverlayFinish(Task finish)
    {
        _ = finish.ContinueWith(
            static (task, state) =>
            {
                if (!task.IsFaulted || task.Exception is null)
                    return;
                var logger = (ILogger)state!;
                logger.LogError(task.Exception.GetBaseException(), "overlay finish failed");
            },
            _logger,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishOverlayVisibleSet(string attachClientId, string? overlayPaneId)
    {
        if (_overlayVisibleSet is null)
            return;
        _ = _overlayVisibleSet.PublishOverlay(attachClientId, overlayPaneId);
    }

    private async Task ResizeOverlayInnerAsync(
        string paneId,
        PaneState pane,
        int areaCols,
        int areaRows,
        CancellationToken ct)
    {
        var geometry = PopupGeometry.TryResolve(areaCols, areaRows);
        if (geometry is null)
            return;
        var cols = Math.Max(1, geometry.InnerCols);
        var rows = Math.Max(1, geometry.InnerRows);
        if (pane.Cols == cols && pane.Rows == rows)
            return;
        var runtime = PeekRuntime(paneId);
        if (runtime is not null)
            await runtime.ResizeAsync(cols, rows, ct).ConfigureAwait(false);
        _state.UpdatePane(new PaneId(paneId), current => current with { Cols = cols, Rows = rows });
    }

    private async Task<JsonObject> FinishOverlayChangeAsync(
        PaneOverlayChange change,
        bool persist,
        CancellationToken ct)
    {
        var pane = change.Pane;
        var body = new JsonObject
        {
            ["ok"] = true,
            ["changed"] = change.Changed,
            ["pane_id"] = pane.Id.Value,
            ["tab_id"] = pane.TabId.Value,
            ["workspace_id"] = pane.WorkspaceId.Value,
            ["placement"] = PanePlacementWire.ToWire(pane.Placement),
            ["hidden"] = pane.Placement == PanePlacement.Hidden,
            ["mode"] = change.Mode,
            ["attach_client_id"] = change.AttachClientId,
            ["overlay_generation"] = change.Generation,
            ["cols"] = pane.Cols,
            ["rows"] = pane.Rows,
        };
        if (!change.Changed)
            return body;

        if (string.Equals(change.Mode, PaneOverlayWire.Overlay, StringComparison.OrdinalIgnoreCase))
        {
            var owner = _overlay.TryGet(change.AttachClientId ?? "");
            if (owner is null
                || owner.Generation != change.Generation
                || owner.PaneId.Value != pane.Id.Value)
            {
                return body;
            }
        }
        else if (_overlay.OwnerOf(pane.Id) is { } current
                 && current.Generation != change.Generation)
        {
            return body;
        }

        if (persist)
            await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        var payload = RuntimeEventPayloadJson.WritePanePlacementChanged(
            pane.Id.Value,
            pane.TabId.Value,
            pane.WorkspaceId.Value,
            PanePlacementWire.Hidden,
            PanePlacementWire.Hidden,
            change.Mode,
            change.AttachClientId,
            change.Generation);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PanePlacementChanged, payload);
        await EmitReliableAsync(
                EventClass.Lifecycle,
                ProtocolEventTypes.PanePlacementChanged,
                payload,
                ct)
            .ConfigureAwait(false);
        return body;
    }

    private void RejectStaleOverlayInput(string paneId, IClientConnection? connection, long? generation)
    {
        if (generation is null)
            return;
        var attachClientId = connection?.ConnectionId;
        if (string.IsNullOrWhiteSpace(attachClientId))
            throw new ControlPlaneException(ProtocolErrorCodes.Fenced, "stale overlay generation");
        if (!_overlay.AdmitInput(new PaneOverlayInputAdmit
        {
            AttachClientId = attachClientId,
            PaneId = new PaneId(paneId),
            Generation = generation.Value,
        }))
        {
            throw new ControlPlaneException(ProtocolErrorCodes.Fenced, "stale overlay generation");
        }
    }

    private static ControlPlaneException ToOverlayException(PaneOverlayError error) =>
        error.Code switch
        {
            PaneOverlayError.NotFoundCode =>
                new ControlPlaneException(ProtocolErrorCodes.NotFound, error.Message),
            PaneOverlayError.BusyCode => UiBusy(),
            PaneOverlayError.StaleGenerationCode =>
                new ControlPlaneException(ProtocolErrorCodes.Fenced, error.Message),
            _ => new ControlPlaneException(ProtocolErrorCodes.InvalidParams, error.Message),
        };

    private sealed class ControlPlaneOverlayFence(ControlPlaneService service) : IOverlayPlacementFence
    {
        public Result<bool, PlacementAuthorityError> Check(string mode, string? attachClientId) =>
            Check(mode, attachClientId, paneId: null);

        public Result<bool, PlacementAuthorityError> Check(string mode, string? attachClientId, string? paneId)
        {
            if (!string.Equals(mode, PaneOverlayWire.Overlay, StringComparison.OrdinalIgnoreCase))
                return Result<bool, PlacementAuthorityError>.Ok(true);

            if (string.IsNullOrWhiteSpace(attachClientId))
                return Result<bool, PlacementAuthorityError>.Fail(PlacementAuthorityError.MissingAttachClient);

            if (service.OverlayOwnerBusy(attachClientId, paneId))
                return Result<bool, PlacementAuthorityError>.Fail(PlacementAuthorityError.OverlayBusy);

            return Result<bool, PlacementAuthorityError>.Ok(true);
        }
    }
}
