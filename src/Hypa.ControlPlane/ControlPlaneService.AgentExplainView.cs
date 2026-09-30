using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal const string ScreenDetectionSkipReason = "full_lifecycle_hook_authority";

    internal Task<JsonElement> HandleAgentExplainAsync(AgentTargetParams p, CancellationToken ct) =>
        Task.FromResult(AgentExplain(p));

    internal async Task<JsonElement> HandleAgentRenameAsync(
        AgentRenameParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await AgentRenameAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleAgentFocusAsync(
        AgentTargetParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await AgentFocusAsync(p, connection, ct).ConfigureAwait(false));

    internal async Task<JsonElement> HandleAgentSendKeysAsync(
        AgentSendKeysParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        OkTyped(await AgentSendKeysAsync(p, connection, ct).ConfigureAwait(false),
            ProtocolJsonContext.Default.AgentSendKeysResult);

    internal Task<JsonElement> HandleAgentViewSetAsync(AgentViewSetParams p, CancellationToken ct) =>
        Task.FromResult(AgentViewSet(p));

    internal Task<JsonElement> HandleAgentViewClearAsync(AgentViewClearParams p, CancellationToken ct) =>
        Task.FromResult(AgentViewClear(p));

    /// <summary>
    /// Full lifecycle hook authority skips screen detection (lines 264-286).
    /// </summary>
    private JsonElement AgentExplain(AgentTargetParams p)
    {
        var pane = ResolveAgentTarget(p.PaneId, p.AgentId);
        if (HoldsSemanticAuthority(pane))
        {
            return OkTyped(
                new AgentExplainResult
                {
                    Agent = pane.AgentKind ?? "unknown",
                    State = pane.AgentStatus.ToString().ToLowerInvariant(),
                    ScreenDetectionSkipped = true,
                    ScreenDetectionSkipReason = ScreenDetectionSkipReason,
                    OscTitle = "",
                    OscProgress = "",
                },
                ProtocolJsonContext.Default.AgentExplainResult);
        }

        var text = "";
        var oscTitle = "";
        var oscProgress = "";
        if (TryGetRuntime(pane.Id.Value, out var runtime) && runtime is not null)
        {
            text = runtime.ReadDetectionText();
            oscTitle = runtime.ReadDetectionOscTitle();
            oscProgress = runtime.ReadDetectionOscProgress();
        }

        var processName = runtime is not null
            ? ResolveDetectionProcessName(runtime, pane)
            : pane.Command;
        // Occupancy is presence.CurrentAgent. ResolveDetectionProcessName already
        string? kind = null;
        if (_agentPresence.TryGetValue(pane.Id.Value, out var presence)
            && presence.OccupantGeneration == pane.OccupantGeneration)
        {
            kind = presence.CurrentAgent;
        }
        if (string.IsNullOrWhiteSpace(kind)
            && AgentKindCatalog.TryResolve(processName, out var fromProcess))
        {
            kind = fromProcess;
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                $"agent target {pane.Id.Value} does not have a detected agent label");
        }

        var detection = _detector.Detect(text, kind, oscTitle, oscProgress);

        return OkTyped(
            new AgentExplainResult
            {
                Agent = detection.AgentKind ?? kind,
                State = detection.Status.ToString().ToLowerInvariant(),
                ManifestSource = detection.ManifestSource,
                ManifestSourceKind = detection.ManifestSourceKind,
                ManifestVersion = detection.ManifestVersion,
                MatchedRule = string.IsNullOrEmpty(detection.MatchedRuleId)
                    ? null
                    : new AgentExplainMatchedRule { Id = detection.MatchedRuleId },
                SkipStateUpdate = detection.SkipStateUpdate,
                FallbackReason = detection.FallbackReason,
                Warning = detection.Warning,
                OscTitle = oscTitle,
                OscProgress = oscProgress,
            },
            ProtocolJsonContext.Default.AgentExplainResult);
    }

    /// <summary>
    /// </summary>
    private async Task<JsonObject> AgentRenameAsync(
        AgentRenameParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.AgentRename);
        var pane = ResolveAgentTarget(p.PaneId, p.AgentId);
        if (string.IsNullOrWhiteSpace(pane.AgentKind) && string.IsNullOrEmpty(pane.AgentName))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.NotFound,
                "agent target does not currently host an agent");
        }

        string? nextName;
        if (p.Name is null)
        {
            nextName = null;
        }
        else if (AgentNameGrammar.IsValid(p.Name))
        {
            nextName = p.Name;
        }
        else
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                AgentNameGrammar.InvalidMessage);
        }

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, ProtocolMethods.AgentRename);
            EnsureNotFrozenForMutation(ProtocolMethods.AgentRename);
            pane = RequirePane(pane.Id.Value);
            if (nextName is not null)
            {
                foreach (var other in _state.ListPanes())
                {
                    if (other.Id.Value == pane.Id.Value)
                        continue;
                    if (!string.Equals(other.AgentName, nextName, StringComparison.Ordinal))
                        continue;
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidState,
                        $"agent name {nextName} is already used");
                }
            }

            pane = _state.UpdatePane(pane.Id, current => current with
            {
                AgentName = nextName,
                UpdatedAt = _time.GetUtcNow(),
            }) ?? pane;
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        return BuildAgentStatusResult(pane, wait: null, timedOut: null);
    }

    /// <summary>
    /// plus <c>mark_active_tab_seen</c> in <c>src/app/actions.rs</c> lines 523-543.
    /// </summary>
    private async Task<JsonObject> AgentFocusAsync(
        AgentTargetParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.AgentFocus);
        var pane = ResolveAgentTarget(p.PaneId, p.AgentId);
        if (string.IsNullOrWhiteSpace(pane.AgentKind) && string.IsNullOrEmpty(pane.AgentName))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.NotFound,
                "agent target does not currently host an agent");
        }

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, ProtocolMethods.AgentFocus);
            EnsureNotFrozenForMutation(ProtocolMethods.AgentFocus);
            RejectHiddenPane(pane, ProtocolMethods.AgentFocus);
            pane = _state.FocusPane(pane.Id)
                ?? throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound, $"Pane not found: {pane.Id.Value}");
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

            _state.MarkTabSeen(pane.TabId);
            pane = _state.GetPane(pane.Id) ?? pane;
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        return BuildAgentStatusResult(pane, wait: null, timedOut: null);
    }

    /// <summary>
    /// and <c>encode_api_keys</c> in <c>src/app/api_helpers.rs</c> lines 34-46.
    /// Validate every logical key before writing. Do not require a lease.
    /// </summary>
    private async Task<AgentSendKeysResult> AgentSendKeysAsync(
        AgentSendKeysParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.AgentSendKeys);
        var pane = ResolveAgentTarget(p.PaneId, p.AgentId);
        if (string.IsNullOrWhiteSpace(pane.AgentKind))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                $"agent {pane.Id.Value} is not an active named agent");
        }

        var keys = p.Keys ?? [];
        var encodedKeys = new List<byte[]>(keys.Count);
        foreach (var key in keys)
        {
            if (!_keyComboEncoder.TryEncode([key], out var encoded, out _))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    $"unsupported key {key}");
            }

            encodedKeys.Add(encoded);
        }

        var payload = new List<byte>();
        foreach (var encoded in encodedKeys)
            payload.AddRange(encoded);
        var bytes = payload.ToArray();

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, ProtocolMethods.AgentSendKeys);
            EnsureNotFrozenForMutation(ProtocolMethods.AgentSendKeys);
            var runtime = GetRuntime(pane.Id.Value);
            await runtime.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        return new AgentSendKeysResult { Ok = true };
    }

    /// <summary>
    /// Plugin sources validate grammar only. Do not look up plugins.
    /// </summary>
    private JsonElement AgentViewSet(AgentViewSetParams p)
    {
        if (!AgentViewProjection.TryValidate(p.Source, p.Label, p.Filter, p.Sort, out var spec, out var error))
        {
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, error);
        }

        lock (_agentViewGate)
            _agentViewOverride = spec;

        return OkTyped(
            new AgentViewResult
            {
                Active = true,
                Source = spec.Source,
                Label = spec.Label,
            },
            ProtocolJsonContext.Default.AgentViewResult);
    }

    /// <summary>
    /// A supplied source is always validated. Empty or whitespace is invalid.
    /// Null or omitted source is an unconditional clear.
    /// </summary>
    private JsonElement AgentViewClear(AgentViewClearParams p)
    {
        string? source = null;
        if (p.Source is not null)
        {
            if (!AgentViewProjection.TryNormalizeSource(p.Source, out var normalized, out var error))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, error);
            source = normalized;
        }

        lock (_agentViewGate)
        {
            if (source is null
                || (_agentViewOverride is { } active
                    && string.Equals(active.Source, source, StringComparison.Ordinal)))
            {
                _agentViewOverride = null;
            }
        }

        AgentViewSpec? remaining;
        lock (_agentViewGate)
            remaining = _agentViewOverride;

        return OkTyped(
            new AgentViewResult
            {
                Active = remaining is not null,
                Source = remaining?.Source,
                Label = remaining?.Label,
            },
            ProtocolJsonContext.Default.AgentViewResult);
    }

    /// <summary>
    /// pane id first, then unique live name.
    /// </summary>
    private PaneState ResolveAgentTarget(string? paneId, string? agentId)
    {
        var raw = EmptyToNull(paneId) ?? EmptyToNull(agentId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "pane_id is required");

        var byId = _state.GetPane(new PaneId(raw));
        if (byId is not null)
            return byId;

        PaneState? match = null;
        var count = 0;
        foreach (var pane in _state.ListPanes())
        {
            if (!string.Equals(pane.AgentName, raw, StringComparison.Ordinal))
                continue;
            match = pane;
            count++;
        }

        if (count > 1)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                $"agent target {raw} is ambiguous");
        }

        if (match is not null)
            return match;

        throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {raw}");
    }

    private void WriteAgentViewSnapshot(JsonObject snapshot)
    {
        AgentViewSpec? spec;
        lock (_agentViewGate)
            spec = _agentViewOverride;
        if (spec is null)
            return;

        snapshot["agent_view"] = new JsonObject
        {
            ["active"] = true,
            ["source"] = spec.Source,
            ["label"] = spec.Label,
            ["has_sort"] = spec.HasSort,
        };

        var order = ComputeAgentOrder(spec);
        var nodes = new JsonNode?[order.Count];
        for (var i = 0; i < order.Count; i++)
            nodes[i] = JsonValue.Create(order[i]);
        snapshot["agent_order"] = new JsonArray(nodes);
    }

    private IReadOnlyList<string> ComputeAgentOrder(AgentViewSpec spec)
    {
        var snap = _state.Snapshot();
        var workspaceOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var workspaces = _state.ListWorkspaces();
        for (var i = 0; i < workspaces.Count; i++)
            workspaceOrder[workspaces[i].Id.Value] = i;

        var tabOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tab in snap.Tabs.Values)
            tabOrder[tab.Id.Value] = tab.Ordinal;

        var paneOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in snap.Panes.Values.GroupBy(p => p.TabId.Value, StringComparer.Ordinal))
        {
            var i = 0;
            foreach (var pane in group.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
                paneOrder[pane.Id.Value] = i++;
        }

        var rows = new List<AgentViewRow>();
        foreach (var pane in _state.ListPanes())
        {
            if (string.IsNullOrWhiteSpace(pane.AgentKind) && string.IsNullOrEmpty(pane.AgentName))
                continue;
            var tokens = _metadata.Get("pane", pane.Id.Value);
            rows.Add(new AgentViewRow
            {
                PaneId = pane.Id.Value,
                TabId = pane.TabId.Value,
                WorkspaceId = pane.WorkspaceId.Value,
                Agent = pane.AgentKind,
                Status = AgentViewProjection.StatusName(pane.AgentStatus, pane.Seen),
                Seen = pane.Seen,
                Tokens = tokens,
                WorkspaceOrder = workspaceOrder.GetValueOrDefault(pane.WorkspaceId.Value, int.MaxValue),
                TabOrder = tabOrder.GetValueOrDefault(pane.TabId.Value, int.MaxValue),
                PaneOrder = paneOrder.GetValueOrDefault(pane.Id.Value, int.MaxValue),
                Attention = AgentViewProjection.AttentionPriority(pane.AgentStatus, pane.Seen),
                StateChangeSeq = pane.LastAgentStateChangeSeq,
            });
        }

        rows.Sort((left, right) =>
        {
            var ws = left.WorkspaceOrder.CompareTo(right.WorkspaceOrder);
            if (ws != 0)
                return ws;
            var tab = left.TabOrder.CompareTo(right.TabOrder);
            if (tab != 0)
                return tab;
            return string.Compare(left.PaneId, right.PaneId, StringComparison.Ordinal);
        });

        var focusedWorkspace = snap.FocusedWorkspaceId?.Value;
        string? focusedTab = null;
        if (focusedWorkspace is not null
            && snap.Workspaces.TryGetValue(focusedWorkspace, out var wsState))
        {
            focusedTab = wsState.FocusedTabId?.Value;
        }

        var panelSort = spec.HasSort ? AgentPanelSort.Spaces : AgentPanelSort.Priority;
        return AgentViewProjection.Apply(
            rows,
            spec,
            new AgentViewEvalContext(focusedWorkspace, focusedTab),
            panelSort);
    }
}
