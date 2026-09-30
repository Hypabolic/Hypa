using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Worktrees;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Single AOT source-generated writer for control-plane event object payloads.
/// Lifecycle, terminal output, lease, binding, export, session, and checkpoint
/// objects are never concatenated.
/// </summary>
public static class RuntimeEventPayloadJson
{
    public static string WritePaneLifecycle(string paneId, string state, int occupantGeneration) =>
        JsonSerializer.Serialize(
            new PaneLifecyclePayload
            {
                PaneId = paneId,
                State = state,
                OccupantGeneration = occupantGeneration,
            },
            EventPayloadJsonContext.Default.PaneLifecyclePayload);

    public static string WriteTerminalOutput(string paneId, string base64Data, int byteCount) =>
        JsonSerializer.Serialize(
            new TerminalOutputPayload
            {
                PaneId = paneId,
                Encoding = "base64",
                Data = base64Data,
                ByteCount = byteCount,
            },
            EventPayloadJsonContext.Default.TerminalOutputPayload);

    public static string WritePaneBell(string paneId, int count) =>
        JsonSerializer.Serialize(
            new PaneBellPayload
            {
                PaneId = paneId,
                Count = Math.Clamp(count, 1, ushort.MaxValue),
            },
            EventPayloadJsonContext.Default.PaneBellPayload);

    /// <summary>
    /// Live-only <c>terminal.render</c> payload. Same shape as terminal.output.
    /// Pass <paramref name="route"/> for leftover byte-fallback so the client can
    /// tag the host write. Omit it for ordinary live bytes.
    /// </summary>
    public static string WriteTerminalRender(
        string paneId,
        string base64Data,
        int byteCount,
        string? route = null) =>
        JsonSerializer.Serialize(
            new TerminalOutputPayload
            {
                PaneId = paneId,
                Encoding = "base64",
                Data = base64Data,
                ByteCount = byteCount,
                Route = route,
            },
            EventPayloadJsonContext.Default.TerminalOutputPayload);

    /// <summary>
    /// Live-only attach snapshot <c>terminal.render</c> payload (<c>kind=snapshot</c>).
    /// Byte renders omit <c>kind</c>.
    /// </summary>
    public static string WriteTerminalRenderSnapshot(
        string paneId,
        int rowStart,
        int rowEnd,
        bool complete,
        int gridCols,
        int gridRows,
        JsonElement snapshot,
        long generation = 0,
        bool patch = false,
        int occupantGeneration = 0) =>
        JsonSerializer.Serialize(
            new TerminalRenderSnapshotPayload
            {
                PaneId = paneId,
                Kind = TerminalRenderSnapshotPayload.KindSnapshot,
                RowStart = rowStart,
                RowEnd = rowEnd,
                Complete = complete,
                GridCols = gridCols,
                GridRows = gridRows,
                Generation = generation,
                Patch = patch,
                OccupantGeneration = occupantGeneration,
                Snapshot = snapshot,
            },
            EventPayloadJsonContext.Default.TerminalRenderSnapshotPayload);

    public static string WriteLeaseChanged(
        string leaseId,
        string paneId,
        string scope,
        string state,
        string holderId,
        string actorId,
        string? oldHolderId,
        string? newHolderId,
        string reasonHash,
        string policyDecision) =>
        JsonSerializer.Serialize(
            new LeaseChangedPayload
            {
                LeaseId = leaseId,
                PaneId = paneId,
                Scope = scope,
                State = state,
                HolderId = holderId,
                ActorId = actorId,
                OldHolderId = oldHolderId,
                NewHolderId = newHolderId,
                ReasonHash = reasonHash,
                PolicyDecision = policyDecision,
            },
            EventPayloadJsonContext.Default.LeaseChangedPayload);

    public static string WriteBindingChanged(string? paneId, AtomicBinding binding) =>
        JsonSerializer.Serialize(
            new BindingChangedPayload
            {
                PaneId = paneId,
                Binding = BindingEventPayload.FromDomain(binding),
            },
            EventPayloadJsonContext.Default.BindingChangedPayload);

    public static string WriteExportAcked(string exportId, long ackedSeq) =>
        JsonSerializer.Serialize(
            new ExportAckedPayload
            {
                ExportId = exportId,
                AckedSeq = ackedSeq,
            },
            EventPayloadJsonContext.Default.ExportAckedPayload);

    public static string WriteSessionLifecycle(string sessionId, string state) =>
        JsonSerializer.Serialize(
            new SessionLifecyclePayload
            {
                SessionId = sessionId,
                State = state,
            },
            EventPayloadJsonContext.Default.SessionLifecyclePayload);

    public static string WriteCheckpointLifecycle(string checkpointId, string state, long barrierSeq) =>
        JsonSerializer.Serialize(
            new CheckpointLifecyclePayload
            {
                CheckpointId = checkpointId,
                State = state,
                BarrierSeq = barrierSeq,
            },
            EventPayloadJsonContext.Default.CheckpointLifecyclePayload);

    public static string WriteTabLifecycle(
        string tabId,
        string action,
        string workspaceId,
        string? requestId = null) =>
        JsonSerializer.Serialize(
            new TabLifecyclePayload
            {
                TabId = tabId,
                Action = action,
                WorkspaceId = workspaceId,
                RequestId = string.IsNullOrEmpty(requestId) ? null : requestId,
            },
            EventPayloadJsonContext.Default.TabLifecyclePayload);

    public static string WriteSettingsChanged(
        string key,
        string? value,
        string valueKind,
        string? digest) =>
        JsonSerializer.Serialize(
            new SettingsChangedPayload
            {
                Key = key,
                Value = value,
                ValueKind = valueKind,
                Digest = digest,
            },
            EventPayloadJsonContext.Default.SettingsChangedPayload);

    public static string WriteOverlayLifecycle(string surface, string state) =>
        JsonSerializer.Serialize(
            new OverlayLifecyclePayload
            {
                Surface = surface,
                State = state,
            },
            EventPayloadJsonContext.Default.OverlayLifecyclePayload);

    /// <summary>
    /// Live <c>workspace.lifecycle</c> payload. Additive <c>action</c> on the P0
    /// <c>workspace_id</c> + <c>state</c> shape. Close uses <c>state=closed</c>;
    /// focus and rename keep <c>state=ready</c>. Does not replace the P0 golden.
    /// </summary>
    public static string WriteWorkspaceLifecycle(string workspaceId, string action)
    {
        var state = string.Equals(action, "closed", StringComparison.Ordinal)
            ? "closed"
            : "ready";
        return JsonSerializer.Serialize(
            new WorkspaceLifecyclePayload
            {
                WorkspaceId = workspaceId,
                State = state,
                Action = action,
            },
            EventPayloadJsonContext.Default.WorkspaceLifecyclePayload);
    }

    public static string WriteWorktreeLifecycle(
        string eventType,
        string workspaceId,
        string? branch,
        string label,
        bool alreadyOpen)
    {
        _ = eventType;
        return JsonSerializer.Serialize(
            new WorktreeLifecyclePayload
            {
                WorkspaceId = workspaceId,
                Branch = branch,
                Label = label,
                Path = WorktreePathRules.RedactedCheckout,
                AlreadyOpen = alreadyOpen ? true : null,
            },
            EventPayloadJsonContext.Default.WorktreeLifecyclePayload);
    }

    public static string WriteWorkspaceMetadataUpdated(
        string workspaceId, IReadOnlyDictionary<string, string> tokens) =>
        JsonSerializer.Serialize(
            new WorkspaceMetadataUpdatedPayload
            {
                WorkspaceId = workspaceId,
                Tokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal),
            },
            EventPayloadJsonContext.Default.WorkspaceMetadataUpdatedPayload);

    public static string WritePaneMetadataUpdated(
        string paneId, IReadOnlyDictionary<string, string> tokens) =>
        JsonSerializer.Serialize(
            new PaneMetadataUpdatedPayload
            {
                PaneId = paneId,
                Tokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal),
            },
            EventPayloadJsonContext.Default.PaneMetadataUpdatedPayload);

    public static string WriteResourceChanged(
        string resourceId, long revision, bool removed, string? summary) =>
        JsonSerializer.Serialize(
            new ResourceChangedPayload
            {
                ResourceId = resourceId,
                Revision = revision,
                Removed = removed,
                Summary = summary,
            },
            EventPayloadJsonContext.Default.ResourceChangedPayload);

    public static string WriteConfigChanged(string pluginId, string key, string value) =>
        JsonSerializer.Serialize(
            new ConfigChangedPayload
            {
                PluginId = pluginId,
                Key = key,
                Value = value,
            },
            EventPayloadJsonContext.Default.ConfigChangedPayload);

    public static string WritePaneMoved(string paneId, string tabId) =>
        JsonSerializer.Serialize(
            new PaneMovedPayload
            {
                PaneId = paneId,
                TabId = tabId,
                Action = "moved",
            },
            EventPayloadJsonContext.Default.PaneMovedPayload);

    public static string WritePanePlacementChanged(
        string paneId,
        string tabId,
        string workspaceId,
        string from,
        string to,
        string mode,
        string? attachClientId = null,
        long? overlayGeneration = null) =>
        JsonSerializer.Serialize(
            new PanePlacementChangedPayload
            {
                PaneId = paneId,
                TabId = tabId,
                WorkspaceId = workspaceId,
                From = from,
                To = to,
                Placement = to,
                Hidden = string.Equals(to, PanePlacementWire.Hidden, StringComparison.Ordinal)
                    || string.Equals(to, "hidden", StringComparison.Ordinal),
                Mode = mode,
                AttachClientId = attachClientId,
                OverlayGeneration = overlayGeneration,
            },
            EventPayloadJsonContext.Default.PanePlacementChangedPayload);

    public static string WritePaneAgentStatusChanged(
        string paneId,
        string tabId,
        string workspaceId,
        int occupantGeneration,
        string agentStatus,
        string? previousStatus,
        string? agent,
        string? message,
        bool seen) =>
        JsonSerializer.Serialize(
            new PaneAgentStatusChangedPayload
            {
                PaneId = paneId,
                TabId = tabId,
                WorkspaceId = workspaceId,
                OccupantGeneration = occupantGeneration,
                AgentStatus = agentStatus,
                PreviousStatus = previousStatus,
                Agent = agent,
                Message = message,
                Seen = seen,
            },
            EventPayloadJsonContext.Default.PaneAgentStatusChangedPayload);

    public static string WritePaneScrollChanged(string paneId, int offset, int maxOffset) =>
        JsonSerializer.Serialize(
            new PaneScrollChangedPayload
            {
                PaneId = paneId,
                Offset = offset,
                MaxOffset = maxOffset,
            },
            EventPayloadJsonContext.Default.PaneScrollChangedPayload);

    public static string WritePaneOutputMatched(string paneId, bool matched) =>
        JsonSerializer.Serialize(
            new PaneOutputMatchedPayload
            {
                PaneId = paneId,
                Matched = matched,
            },
            EventPayloadJsonContext.Default.PaneOutputMatchedPayload);

    public static string WriteNotificationShown(
        string title,
        string? body,
        string source,
        string sound,
        string? paneId,
        string reason) =>
        JsonSerializer.Serialize(
            new NotificationShownPayload
            {
                Title = title,
                Body = body,
                Source = source,
                Sound = sound,
                PaneId = paneId,
                Reason = reason,
            },
            EventPayloadJsonContext.Default.NotificationShownPayload);

    public static string WriteWindowTitleChanged(bool overridden, string? title) =>
        JsonSerializer.Serialize(
            new WindowTitleChangedPayload
            {
                Overridden = overridden,
                Title = title,
            },
            EventPayloadJsonContext.Default.WindowTitleChangedPayload);

    public static string WritePopupLifecycle(string state) =>
        WritePopupLifecycle(state, geometry: null);

    public static string WritePopupLifecycle(string state, PopupGeometryResult? geometry) =>
        WritePopupLifecycle(state, geometry, width: null, height: null, areaCols: null, areaRows: null);

    public static string WritePopupLifecycle(
        string state,
        PopupGeometryResult? geometry,
        PopupSize? width,
        PopupSize? height,
        int? areaCols,
        int? areaRows) =>
        JsonSerializer.Serialize(
            new PopupLifecyclePayload
            {
                State = state,
                Cols = geometry?.InnerCols,
                Rows = geometry?.InnerRows,
                OuterCols = geometry?.OuterCols,
                OuterRows = geometry?.OuterRows,
                AreaCols = areaCols,
                AreaRows = areaRows,
                Width = width,
                Height = height,
            },
            EventPayloadJsonContext.Default.PopupLifecyclePayload);

    /// <summary>Live-only popup <c>terminal.render</c> bytes. No <c>pane_id</c>.</summary>
    public static string WritePopupTerminalRender(string base64Data, int byteCount) =>
        JsonSerializer.Serialize(
            new PopupTerminalRenderPayload
            {
                Target = ProtocolEventTypes.TerminalRenderTargetPopup,
                Encoding = "base64",
                Data = base64Data,
                ByteCount = byteCount,
            },
            EventPayloadJsonContext.Default.PopupTerminalRenderPayload);

    /// <summary>Live-only popup attach snapshot. No <c>pane_id</c>.</summary>
    public static string WritePopupTerminalRenderSnapshot(
        int rowStart,
        int rowEnd,
        bool complete,
        int gridCols,
        int gridRows,
        JsonElement snapshot,
        long generation = 0,
        bool patch = false,
        int occupantGeneration = 0) =>
        JsonSerializer.Serialize(
            new TerminalRenderSnapshotPayload
            {
                PaneId = null,
                Target = ProtocolEventTypes.TerminalRenderTargetPopup,
                Kind = TerminalRenderSnapshotPayload.KindSnapshot,
                RowStart = rowStart,
                RowEnd = rowEnd,
                Complete = complete,
                GridCols = gridCols,
                GridRows = gridRows,
                Generation = generation,
                Patch = patch,
                OccupantGeneration = occupantGeneration,
                Snapshot = snapshot,
            },
            EventPayloadJsonContext.Default.TerminalRenderSnapshotPayload);

    /// <summary>
    /// Live-only <c>pane.input_rejected</c> payload. Never journaled.
    /// </summary>
    public static string WritePaneInputRejected(
        string paneId,
        string method,
        int code,
        string message,
        string? leaseId = null) =>
        JsonSerializer.Serialize(
            new PaneInputRejectedPayload
            {
                PaneId = paneId,
                Method = method,
                Code = code,
                Message = message,
                LeaseId = string.IsNullOrWhiteSpace(leaseId) ? null : leaseId,
            },
            EventPayloadJsonContext.Default.PaneInputRejectedPayload);

    /// <summary>
    /// Bound <c>pane.input_rejected</c> so the formatted NDJSON line fits
    /// the socket cap. Prefer full pane and lease identity. Omit ids rather
    /// than truncate them so attach matching stays exact. Empty ids still
    // / detach the sending connection (connection-scoped fanout).
    /// <c>src/server/headless.rs:3141-3143</c> keeps Input failure on that client.
    /// </summary>
    public static string WritePaneInputRejectedFitting(
        string paneId,
        string method,
        int code,
        string message,
        string? leaseId,
        int maxLineBytes = AttachSnapshotPacker.MaxNdjsonLineBytes,
        string? subscriptionId = null)
    {
        paneId ??= "";
        method = string.IsNullOrWhiteSpace(method) ? ProtocolMethods.PaneSendKeys : method;
        message ??= "";
        if (maxLineBytes < 1)
            maxLineBytes = AttachSnapshotPacker.MaxNdjsonLineBytes;

        var estimateSub = string.IsNullOrEmpty(subscriptionId)
            ? new string('s', AttachSnapshotPacker.EstimateSubscriptionIdChars)
            : subscriptionId;
        const int messageCap = 256;
        var cappedMessage = message.Length > messageCap ? message[..messageCap] : message;

        var full = WritePaneInputRejected(paneId, method, code, message, leaseId);
        if (PaneInputRejectedFits(full, estimateSub, maxLineBytes))
            return full;

        var capped = WritePaneInputRejected(paneId, method, code, cappedMessage, leaseId);
        if (PaneInputRejectedFits(capped, estimateSub, maxLineBytes))
            return capped;

        var noLease = WritePaneInputRejected(paneId, method, code, cappedMessage, null);
        if (PaneInputRejectedFits(noLease, estimateSub, maxLineBytes))
            return noLease;

        var noIds = WritePaneInputRejected("", method, code, cappedMessage, null);
        if (PaneInputRejectedFits(noIds, estimateSub, maxLineBytes))
            return noIds;

        return WritePaneInputRejected("", method, code, "input rejected", null);
    }

    /// <summary>
    /// Re-fit an already serialized <c>pane.input_rejected</c> payload.
    /// </summary>
    public static string FitPaneInputRejectedPayload(
        string payloadJson,
        int maxLineBytes = AttachSnapshotPacker.MaxNdjsonLineBytes,
        string? subscriptionId = null)
    {
        var paneId = "";
        string? leaseId = null;
        var method = ProtocolMethods.PaneSendKeys;
        var code = 0;
        var message = "input rejected";
        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("pane_id", out var paneEl)
                        && paneEl.ValueKind == JsonValueKind.String)
                    {
                        paneId = paneEl.GetString() ?? "";
                    }

                    if (root.TryGetProperty("lease_id", out var leaseEl)
                        && leaseEl.ValueKind == JsonValueKind.String)
                    {
                        leaseId = leaseEl.GetString();
                    }

                    if (root.TryGetProperty("method", out var methodEl)
                        && methodEl.ValueKind == JsonValueKind.String)
                    {
                        method = methodEl.GetString() ?? method;
                    }

                    if (root.TryGetProperty("code", out var codeEl)
                        && codeEl.TryGetInt32(out var parsed))
                    {
                        code = parsed;
                    }

                    if (root.TryGetProperty("message", out var messageEl)
                        && messageEl.ValueKind == JsonValueKind.String)
                    {
                        message = messageEl.GetString() ?? message;
                    }
                }
            }
            catch (JsonException)
            {
                // compact fallback below
            }
        }

        return WritePaneInputRejectedFitting(
            paneId, method, code, message, leaseId, maxLineBytes, subscriptionId);
    }

    private static bool PaneInputRejectedFits(string payload, string subscriptionId, int maxLineBytes)
    {
        var rec = new RuntimeEventRecord
        {
            Seq = long.MaxValue,
            Class = EventClass.Control,
            Reliability = EventReliability.Reliable,
            Type = ProtocolEventTypes.PaneInputRejected,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = payload,
            Lane = WriterLaneNames.Control,
        };
        var utf8 = EventSubscriptionHub.FormatRuntimeEventUtf8(rec, subscriptionId);
        return AttachCellsLinePacker.FitsNdjsonLine(utf8, maxLineBytes);
    }
}

/// <summary>Pane.lifecycle / occupant.lifecycle stored and live payload.</summary>
public sealed record PaneLifecyclePayload
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("occupant_generation")]
    public int OccupantGeneration { get; init; }
}

/// <summary>Terminal.output stored and live payload.</summary>
public sealed record TerminalOutputPayload
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("encoding")]
    public required string Encoding { get; init; }

    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("byte_count")]
    public int ByteCount { get; init; }

    [JsonPropertyName("route")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Route { get; init; }
}

/// <summary>Pane.bell stored and live resource payload.</summary>
public sealed record PaneBellPayload
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

/// <summary>Lease.changed stored and live payload.</summary>
public sealed record LeaseChangedPayload
{
    [JsonPropertyName("lease_id")]
    public required string LeaseId { get; init; }

    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("holder_id")]
    public required string HolderId { get; init; }

    [JsonPropertyName("actor_id")]
    public required string ActorId { get; init; }

    [JsonPropertyName("old_holder_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? OldHolderId { get; init; }

    [JsonPropertyName("new_holder_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? NewHolderId { get; init; }

    [JsonPropertyName("reason_hash")]
    public required string ReasonHash { get; init; }

    [JsonPropertyName("policy_decision")]
    public required string PolicyDecision { get; init; }
}

/// <summary>Binding.changed stored and live payload.</summary>
public sealed record BindingChangedPayload
{
    [JsonPropertyName("pane_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PaneId { get; init; }

    [JsonPropertyName("binding")]
    public required BindingEventPayload Binding { get; init; }
}

/// <summary>Opaque Atomic binding identifiers on the event wire.</summary>
public sealed record BindingEventPayload
{
    [JsonPropertyName("agent_session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AgentSessionId { get; init; }

    [JsonPropertyName("run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RunId { get; init; }

    [JsonPropertyName("step_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? StepId { get; init; }

    [JsonPropertyName("memory_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? MemoryId { get; init; }

    [JsonPropertyName("project_root")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ProjectRoot { get; init; }

    [JsonPropertyName("tenant_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? TenantId { get; init; }

    public static BindingEventPayload FromDomain(AtomicBinding binding) => new()
    {
        AgentSessionId = binding.AgentSessionId,
        RunId = binding.RunId,
        StepId = binding.StepId,
        MemoryId = binding.MemoryId,
        ProjectRoot = binding.ProjectRoot,
        TenantId = binding.TenantId,
    };
}

/// <summary>Export.acked stored and live payload.</summary>
public sealed record ExportAckedPayload
{
    [JsonPropertyName("export_id")]
    public required string ExportId { get; init; }

    [JsonPropertyName("acked_seq")]
    public long AckedSeq { get; init; }
}

/// <summary>Session.lifecycle stored and live payload.</summary>
public sealed record SessionLifecyclePayload
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }
}

/// <summary>Checkpoint.lifecycle stored and live payload.</summary>
public sealed record CheckpointLifecyclePayload
{
    [JsonPropertyName("checkpoint_id")]
    public required string CheckpointId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("barrier_seq")]
    public long BarrierSeq { get; init; }
}

/// <summary>Tab.lifecycle stored and live payload.</summary>
public sealed record TabLifecyclePayload
{
    [JsonPropertyName("tab_id")]
    public required string TabId { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("request_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }
}

/// <summary>Workspace.lifecycle live payload (P0 state plus additive action).</summary>
public sealed record WorkspaceLifecyclePayload
{
    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }
}

public sealed record WorktreeLifecyclePayload
{
    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("branch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Branch { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("already_open")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AlreadyOpen { get; init; }
}

public sealed record WorkspaceMetadataUpdatedPayload
{
    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("tokens")]
    public required IReadOnlyDictionary<string, string> Tokens { get; init; }
}

public sealed record PaneMetadataUpdatedPayload
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("tokens")]
    public required IReadOnlyDictionary<string, string> Tokens { get; init; }
}

public sealed record ResourceChangedPayload
{
    [JsonPropertyName("resource_id")]
    public required string ResourceId { get; init; }

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    [JsonPropertyName("removed")]
    public required bool Removed { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }
}

public sealed record PaneMovedPayload
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("tab_id")]
    public required string TabId { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }
}

/// <summary>Pane.placement_changed payload after tiled show or hide.</summary>
public sealed record PanePlacementChangedPayload
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("tab_id")]
    public required string TabId { get; init; }

    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("placement")]
    public required string Placement { get; init; }

    [JsonPropertyName("hidden")]
    public required bool Hidden { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("attach_client_id")]
    public string? AttachClientId { get; init; }

    [JsonPropertyName("overlay_generation")]
    public long? OverlayGeneration { get; init; }
}

/// <summary>Pane.agent_status_changed payload.</summary>
public sealed record PaneAgentStatusChangedPayload
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("tab_id")]
    public required string TabId { get; init; }

    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("occupant_generation")]
    public int OccupantGeneration { get; init; }

    [JsonPropertyName("agent_status")]
    public required string AgentStatus { get; init; }

    [JsonPropertyName("previous_status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PreviousStatus { get; init; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Agent { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Message { get; init; }

    [JsonPropertyName("seen")]
    public bool Seen { get; init; }
}

/// <summary>Pane.scroll_changed payload.</summary>
public sealed record PaneScrollChangedPayload
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("max_offset")]
    public int MaxOffset { get; init; }
}

/// <summary>Pane.output_matched payload.</summary>
public sealed record PaneOutputMatchedPayload
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("matched")]
    public bool Matched { get; init; }
}

/// <summary>Notification.shown payload.</summary>
public sealed record NotificationShownPayload
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Body { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("sound")]
    public required string Sound { get; init; }

    [JsonPropertyName("pane_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PaneId { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>Client.window_title.changed payload.</summary>
public sealed record WindowTitleChangedPayload
{
    [JsonPropertyName("overridden")]
    public required bool Overridden { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

/// <summary>Popup.lifecycle payload. Never includes <c>pane_id</c>.</summary>
public sealed record PopupLifecyclePayload
{
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("cols")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Cols { get; init; }

    [JsonPropertyName("rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Rows { get; init; }

    [JsonPropertyName("outer_cols")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OuterCols { get; init; }

    [JsonPropertyName("outer_rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OuterRows { get; init; }

    [JsonPropertyName("area_cols")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AreaCols { get; init; }

    [JsonPropertyName("area_rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AreaRows { get; init; }

    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PopupSizeJsonConverter))]
    public PopupSize? Width { get; init; }

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PopupSizeJsonConverter))]
    public PopupSize? Height { get; init; }
}

/// <summary>Live-only <c>pane.input_rejected</c> payload. Never journaled.</summary>
public sealed record PaneInputRejectedPayload
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("lease_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeaseId { get; init; }
}

/// <summary>Committed settings change. Lifecycle. Replays.</summary>
public sealed record SettingsChangedPayload
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; init; }

    [JsonPropertyName("value_kind")]
    public required string ValueKind { get; init; }

    [JsonPropertyName("digest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Digest { get; init; }
}

/// <summary>Overlay open or close. Never carries pane_id.</summary>
public sealed record OverlayLifecyclePayload
{
    [JsonPropertyName("surface")]
    public required string Surface { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }
}

/// <summary>Live popup <c>terminal.render</c> bytes. No <c>pane_id</c>.</summary>
public sealed record PopupTerminalRenderPayload
{
    [JsonPropertyName("target")]
    public required string Target { get; init; }

    [JsonPropertyName("encoding")]
    public required string Encoding { get; init; }

    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("byte_count")]
    public int ByteCount { get; init; }
}

[JsonSerializable(typeof(PaneLifecyclePayload))]
[JsonSerializable(typeof(TerminalOutputPayload))]
[JsonSerializable(typeof(PaneBellPayload))]
[JsonSerializable(typeof(TerminalRenderSnapshotPayload))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(LeaseChangedPayload))]
[JsonSerializable(typeof(BindingChangedPayload))]
[JsonSerializable(typeof(BindingEventPayload))]
[JsonSerializable(typeof(ExportAckedPayload))]
[JsonSerializable(typeof(SessionLifecyclePayload))]
[JsonSerializable(typeof(CheckpointLifecyclePayload))]
[JsonSerializable(typeof(TabLifecyclePayload))]
[JsonSerializable(typeof(WorkspaceLifecyclePayload))]
[JsonSerializable(typeof(WorktreeLifecyclePayload))]
[JsonSerializable(typeof(WorkspaceMetadataUpdatedPayload))]
[JsonSerializable(typeof(PaneMetadataUpdatedPayload))]
[JsonSerializable(typeof(ResourceChangedPayload))]
[JsonSerializable(typeof(ConfigChangedPayload))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(PaneMovedPayload))]
[JsonSerializable(typeof(PanePlacementChangedPayload))]
[JsonSerializable(typeof(PaneAgentStatusChangedPayload))]
[JsonSerializable(typeof(PaneScrollChangedPayload))]
[JsonSerializable(typeof(PaneOutputMatchedPayload))]
[JsonSerializable(typeof(NotificationShownPayload))]
[JsonSerializable(typeof(WindowTitleChangedPayload))]
[JsonSerializable(typeof(PopupLifecyclePayload))]
[JsonSerializable(typeof(PopupTerminalRenderPayload))]
[JsonSerializable(typeof(PaneInputRejectedPayload))]
[JsonSerializable(typeof(SettingsChangedPayload))]
[JsonSerializable(typeof(OverlayLifecyclePayload))]
[JsonSerializable(typeof(PopupSize))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = false)]
public partial class EventPayloadJsonContext : JsonSerializerContext;
