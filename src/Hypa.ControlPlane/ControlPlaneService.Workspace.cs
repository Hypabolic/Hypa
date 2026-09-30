using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Metadata;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal async Task<JsonElement> HandleWorkspaceMoveAsync(
        WorkspaceMoveParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await WorkspaceMoveAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleWorkspaceMoveBlockAsync(
        WorkspaceMoveBlockParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await WorkspaceMoveBlockAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleWorkspaceReportMetadataAsync(
        WorkspaceReportMetadataParams p, CancellationToken ct)
    {
        var result = await WorkspaceReportMetadataAsync(p, ct).ConfigureAwait(false);
        return OkTyped(result, ProtocolJsonContext.Default.MetadataReportResult);
    }

    private async Task<JsonArray> WorkspaceMoveAsync(
        WorkspaceMoveParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.WorkspaceMove);
        var id = RequireField(p.WorkspaceId, "workspace_id");
        var insertIndex = p.InsertIndex
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "insert_index is required");
        WorkspaceState? moved;
        bool changed;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, ProtocolMethods.WorkspaceMove);
            EnsureNotFrozenForMutation(ProtocolMethods.WorkspaceMove);
            try
            {
                var before = _state.ListWorkspaces().Select(w => w.Id.Value).ToArray();
                moved = _state.MoveWorkspace(new WorkspaceId(id), insertIndex);
                changed = moved is not null
                    && !before.SequenceEqual(
                        _state.ListWorkspaces().Select(w => w.Id.Value),
                        StringComparer.Ordinal);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "insert_index is out of range");
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }
        if (moved is null)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {id}");
        if (changed)
        {
            await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
            await EmitWorkspaceLifecycleAsync(id, "moved", ct).ConfigureAwait(false);
        }
        await FlushExpiredMetadataAsync(ct).ConfigureAwait(false);
        return WorkspaceList();
    }

    private async Task<JsonArray> WorkspaceMoveBlockAsync(
        WorkspaceMoveBlockParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.WorkspaceMoveBlock);
        if (p.WorkspaceIds is null || p.WorkspaceIds.Count == 0)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "workspace_ids is required");
        var ids = p.WorkspaceIds.Select(id =>
            string.IsNullOrWhiteSpace(id)
                ? throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "workspace_id is required")
                : new WorkspaceId(id)).ToArray();
        WorkspaceId? beforeId = p.BeforeWorkspaceId is null ? null : new WorkspaceId(p.BeforeWorkspaceId);

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        MoveWorkspaceBlockOutcome outcome;
        try
        {
            EnsureAttachPaneMutation(connection, ProtocolMethods.WorkspaceMoveBlock);
            EnsureNotFrozenForMutation(ProtocolMethods.WorkspaceMoveBlock);
            if (ids.Select(id => id.Value).Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "workspace_ids must be unique");
            var current = _state.ListWorkspaces();
            if (ids.Any(id => current.All(w => w.Id.Value != id.Value)))
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Workspace not found");
            if (beforeId is { } anchor && current.All(w => w.Id.Value != anchor.Value))
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Workspace anchor not found");
            if (beforeId is { } inBlock && ids.Any(id => id.Value == inBlock.Value))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "anchor cannot be in workspace_ids");
            outcome = _state.MoveWorkspaceBlock(ids, beforeId);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        if (outcome is MoveWorkspaceBlockOutcome.NotFound)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Workspace not found");
        if (outcome is MoveWorkspaceBlockOutcome.Invalid)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "workspace_ids is invalid");
        if (outcome is MoveWorkspaceBlockOutcome.Moved)
        {
            await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
            await EmitWorkspaceLifecycleAsync(ids[0].Value, "reordered", ct).ConfigureAwait(false);
        }
        await FlushExpiredMetadataAsync(ct).ConfigureAwait(false);
        return WorkspaceList();
    }

    private async Task<MetadataReportResult> WorkspaceReportMetadataAsync(
        WorkspaceReportMetadataParams p, CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.WorkspaceReportMetadata);
        var id = RequireField(p.WorkspaceId, "workspace_id");
        var source = RequireField(p.Source, "source");
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation(ProtocolMethods.WorkspaceReportMetadata);
            if (_state.GetWorkspace(new WorkspaceId(id)) is null)
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {id}");
            await _metadataEmitGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EmitExpiredMetadataLockedAsync(ct).ConfigureAwait(false);
                if (_state.GetWorkspace(new WorkspaceId(id)) is null)
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {id}");
                var result = _metadata.ApplyPatch("workspace", id, source, p.Tokens, p.Sequence, p.TtlMs, _time.GetUtcNow());
                if (!result.Accepted)
                    throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, result.Error ?? "invalid metadata");
                if (result.Changed)
                {
                    if (_state.GetWorkspace(new WorkspaceId(id)) is null)
                        throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {id}");
                    await EmitLiveWorkspaceMetadataUpdatedAsync(id, result.Values, ct)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _metadataEmitGate.Release();
            }

            return new MetadataReportResult { Ok = true };
        }
        finally
        {
            _bindingMutationGate.Release();
        }
    }

    internal async Task<JsonElement> HandleWorkspaceFocusAsync(
        WorkspaceFocusParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await WorkspaceFocusAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleWorkspaceRenameAsync(
        WorkspaceRenameParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await WorkspaceRenameAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleWorkspaceCloseAsync(
        WorkspaceCloseParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await WorkspaceCloseAsync(p, connection, ct).ConfigureAwait(false));

    private async Task<JsonObject> WorkspaceFocusAsync(
        WorkspaceFocusParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("workspace.focus");
        var id = RequireField(p.WorkspaceId, "workspace_id");
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        WorkspaceState ws;
        try
        {
            EnsureAttachPaneMutation(connection, "workspace.focus");
            EnsureNotFrozenForMutation("workspace.focus");
            ws = _state.FocusWorkspace(new WorkspaceId(id))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {id}");
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        var focusedTab = ws.FocusedTabId?.Value;
        var focusedPane = focusedTab is { } tabId
            ? _state.GetTab(new TabId(tabId))?.FocusedPaneId?.Value
            : null;
        RememberAttachClientSelection(connection, ws.Id.Value, focusedTab, focusedPane);
        await EmitWorkspaceLifecycleAsync(ws.Id.Value, "focused", ct).ConfigureAwait(false);
        await FlushExpiredMetadataAsync(ct).ConfigureAwait(false);
        return WorkspaceToJson(ws);
    }

    private async Task<JsonObject> WorkspaceRenameAsync(
        WorkspaceRenameParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("workspace.rename");
        var id = RequireField(p.WorkspaceId, "workspace_id");
        var label = RejectUnsafeLabel(RequireField(p.Label, "label"))!;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        WorkspaceState ws;
        try
        {
            EnsureAttachPaneMutation(connection, "workspace.rename");
            EnsureNotFrozenForMutation("workspace.rename");
            ws = _state.RenameWorkspace(new WorkspaceId(id), label)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {id}");
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        await EmitWorkspaceLifecycleAsync(ws.Id.Value, "renamed", ct).ConfigureAwait(false);
        await FlushExpiredMetadataAsync(ct).ConfigureAwait(false);
        return WorkspaceToJson(ws);
    }

    private async Task<JsonObject> WorkspaceCloseAsync(
        WorkspaceCloseParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("workspace.close");
        var id = RequireField(p.WorkspaceId, "workspace_id");
        var workspaceId = new WorkspaceId(id);

        WorkspaceCloseCommit commit;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, "workspace.close");
            commit = WorkspaceCloseUnderGate(workspaceId);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await FinishWorkspaceCloseAsync(commit, ct).ConfigureAwait(false);
        return new JsonObject { ["ok"] = true, ["workspace_id"] = id };
    }

    /// <summary>
    /// Caller holds <see cref="_bindingMutationGate"/>. Split from the gate-owning
    /// wrapper so worktree remove can check last-workspace, delete, and close
    /// without a deadlock.
    /// </summary>
    private WorkspaceCloseCommit WorkspaceCloseUnderGate(WorkspaceId workspaceId)
    {
        EnsureNotFrozenForMutation("workspace.close");
        var ws = _state.GetWorkspace(workspaceId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {workspaceId.Value}");
        var group = WorktreeCloseGroup(ws);
        var closedIds = group.Select(g => g.Value).ToArray();
        if (_state.ListWorkspaces().Count <= group.Count)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "Cannot close the last workspace");
        }

        var ids = new List<(string Id, int OccupantGeneration)>();
        foreach (var memberId in group)
        {
            var member = _state.GetWorkspace(memberId);
            if (member is null)
                continue;
            foreach (var tab in _state.ListTabs(member.Id))
            {
                foreach (var paneId in tab.PaneIds)
                    ids.Add((paneId.Value, SnapshotOccupantGeneration(paneId)));
            }

            foreach (var pane in _state.ListPanes())
            {
                if (pane.WorkspaceId.Value == member.Id.Value
                    && !ids.Exists(p => string.Equals(p.Id, pane.Id.Value, StringComparison.Ordinal)))
                {
                    ids.Add((pane.Id.Value, NormalizeOccupantGeneration(pane.OccupantGeneration)));
                }
            }
        }

        foreach (var memberId in group)
        {
            var outcome = _state.CloseWorkspace(memberId);
            if (outcome == CloseWorkspaceOutcome.NotFound)
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Workspace not found: {memberId.Value}");
            if (outcome == CloseWorkspaceOutcome.LastWorkspace)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    "Cannot close the last workspace");
            }

            _metadata.Drop("workspace", memberId.Value);
        }

        foreach (var pane in ids)
            _metadata.Drop("pane", pane.Id);

        return new WorkspaceCloseCommit(ids, closedIds);
    }

    private async Task FinishWorkspaceCloseAsync(WorkspaceCloseCommit commit, CancellationToken ct)
    {
        foreach (var pane in commit.Panes)
            await TeardownRemovedPaneRuntimeAsync(pane.Id, pane.OccupantGeneration, ct).ConfigureAwait(false);

        // src/app/session.rs:14-25 schedules a later save. Publish the
        // lifecycle event from the reduced graph before the durable write.
        foreach (var memberId in commit.ClosedIds)
            await EmitWorkspaceLifecycleAsync(memberId, "closed", ct).ConfigureAwait(false);

        await PersistGraphAsync(requireDurable: true, removeMissingPanes: true).ConfigureAwait(false);
        ReconcileAttachClientViews();
    }

    private readonly record struct WorkspaceCloseCommit(
        IReadOnlyList<(string Id, int OccupantGeneration)> Panes,
        IReadOnlyList<string> ClosedIds);

    private async Task EmitWorkspaceLifecycleAsync(string workspaceId, string action, CancellationToken ct)
    {
        if (_journal is null)
            return;
        var payload = RuntimeEventPayloadJson.WriteWorkspaceLifecycle(workspaceId, action);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.WorkspaceLifecycle, payload);
        await PublishReliableAsync(
            EventClass.Lifecycle,
            ProtocolEventTypes.WorkspaceLifecycle,
            payload,
            ct,
            notifyPlugin: true).ConfigureAwait(false);
    }

    private async Task FlushExpiredMetadataAsync(CancellationToken ct)
    {
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _metadataEmitGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EmitExpiredMetadataLockedAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _metadataEmitGate.Release();
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }
    }

    /// <summary>
    /// Expire-on-read for pane/agent gets. Does not take
    /// <see cref="_bindingMutationGate"/> so <c>agent.get</c> can run while
    /// <c>agent.prompt</c> holds that gate across a write. Takes
    /// <see cref="_metadataEmitGate"/> so expire+emit stays ordered with
    /// <c>report_metadata</c>.
    /// </summary>
    private async Task FlushExpiredMetadataForReadAsync(CancellationToken ct)
    {
        await _metadataEmitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EmitExpiredMetadataLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _metadataEmitGate.Release();
        }
    }

    /// <summary>
    /// Caller holds <see cref="_metadataEmitGate"/>. Emit the ExpireDue snapshot,
    /// not a later Get(), so a concurrent report cannot be wiped.
    /// </summary>
    private async Task EmitExpiredMetadataLockedAsync(CancellationToken ct)
    {
        var expired = _metadata.ExpireDue(_time.GetUtcNow());
        foreach (var item in expired)
        {
            if (string.Equals(item.Kind, "workspace", StringComparison.Ordinal))
            {
                if (_state.GetWorkspace(new WorkspaceId(item.Id)) is null)
                    continue;
                await EmitLiveWorkspaceMetadataUpdatedAsync(item.Id, item.Values, ct)
                    .ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(item.Kind, "pane", StringComparison.Ordinal))
                continue;
            if (_state.GetPane(new PaneId(item.Id)) is null)
                continue;
            await EmitLivePaneMetadataUpdatedAsync(item.Id, item.Values, ct)
                .ConfigureAwait(false);
        }
    }

    private Task OnMetadataSweepRequested()
    {
        if (IsShuttingDown)
            return Task.CompletedTask;

        lock (_metadataSweepSync)
        {
            if (!_metadataSweepTask.IsCompleted)
                return _metadataSweepTask;
            _metadataSweepTask = SweepExpiredMetadataFromTimerAsync();
            return _metadataSweepTask;
        }
    }

    private async Task SweepExpiredMetadataFromTimerAsync()
    {
        try
        {
            if (IsShuttingDown)
                return;
            await FlushExpiredMetadataAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Metadata TTL sweep failed");
        }
    }

    private void UnbindMetadataStore()
    {
        _metadata.SweepRequested -= OnMetadataSweepRequested;
    }

    /// <summary>
    /// Live-only Lifecycle emit for <c>workspace.metadata_updated</c>. Never journals.
    /// Uses the render seq domain so journal seq is not torn.
    /// Reliability is Reliable so a full attach lifecycle queue does not drop it.
    /// Caller holds <see cref="_metadataEmitGate"/>. Waits for live delivery so a
    /// TTL timer callback does not return before subscribers observe the event.
    /// </summary>
    private async Task EmitLiveWorkspaceMetadataUpdatedAsync(
        string workspaceId, IReadOnlyDictionary<string, string> tokens, CancellationToken ct)
    {
        if (_subscriptions is null)
            return;

        var payload = RuntimeEventPayloadJson.WriteWorkspaceMetadataUpdated(workspaceId, tokens);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.WorkspaceMetadataUpdated, payload);
        var seq = _paneRenderSeq.AddOrUpdate(
            ProtocolEventTypes.WorkspaceMetadataUpdated, 1L, static (_, prev) => prev + 1);
        var rec = new RuntimeEventRecord
        {
            Seq = seq,
            Class = EventClass.Lifecycle,
            Reliability = EventReliability.Reliable,
            Type = ProtocolEventTypes.WorkspaceMetadataUpdated,
            OccurredAt = _time.GetUtcNow(),
            PayloadJson = payload,
        };
        await _subscriptions.FanoutLiveAsync(rec, ct).ConfigureAwait(false);
    }
}
