using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly PluginResourceStore _pluginResources = new();
    private readonly object _resourceChangedGate = new();
    private readonly Dictionary<string, ResourceChangedPending> _pendingResourceChanges =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resourceRefreshGates =
        new(StringComparer.Ordinal);
    private Task _resourceChangedFlush = Task.CompletedTask;
    private bool _resourceChangedArmed;

    internal Task<JsonElement> HandlePluginResourceListAsync(PluginResourceListParams p, CancellationToken ct)
    {
        _ = ct;
        var listed = _pluginResources.List(EmptyToNull(p.PluginId));
        return Task.FromResult(OkTyped(
            new PluginResourceListResult { Resources = listed.Select(ToResourceDto).ToArray() },
            ProtocolJsonContext.Default.PluginResourceListResult));
    }

    internal Task<JsonElement> HandlePluginResourceGetAsync(PluginResourceGetParams p, CancellationToken ct)
    {
        _ = ct;
        var id = RequireField(p.ResourceId, "resource_id");
        var got = _pluginResources.Get(id);
        if (!got.IsOk)
            throw PluginFault(got.Error);
        return Task.FromResult(OkTyped(
            new PluginResourceGetResult { Resource = ToResourceDto(got.Value) },
            ProtocolJsonContext.Default.PluginResourceGetResult));
    }

    internal Task<JsonElement> HandlePluginResourcePublishAsync(PluginResourcePublishParams p, CancellationToken ct)
    {
        _ = ct;
        EnsureNotFrozenForMutation(ProtocolMethods.PluginResourcePublish);
        var envelope = RequirePublishEnvelope(p);
        var published = _pluginResources.Publish(envelope);
        if (!published.IsOk)
            throw PluginFault(published.Error);
        if (!published.Value.Ignored)
            ScheduleResourceChanged(published.Value.Resource, removed: false);
        return Task.FromResult(OkTyped(
            new PluginResourcePublishResult
            {
                Ignored = published.Value.Ignored,
                Resource = ToResourceDto(published.Value.Resource),
            },
            ProtocolJsonContext.Default.PluginResourcePublishResult));
    }

    internal Task<JsonElement> HandlePluginResourceRemoveAsync(PluginResourceRemoveParams p, CancellationToken ct)
    {
        _ = ct;
        EnsureNotFrozenForMutation(ProtocolMethods.PluginResourceRemove);
        var id = RequireField(p.ResourceId, "resource_id");
        var removed = _pluginResources.Remove(id, callerPluginId: null);
        if (!removed.IsOk)
            throw PluginFault(removed.Error);
        ScheduleResourceChanged(removed.Value, removed: true);
        return Task.FromResult(OkTyped(
            new PluginResourceRemoveResult { ResourceId = removed.Value.Id.Value, Removed = true },
            ProtocolJsonContext.Default.PluginResourceRemoveResult));
    }

    internal async Task RefreshPluginResourceAsync(string pluginId, string localId, CancellationToken ct)
    {
        if (IsShuttingDown)
            return;

        var key = pluginId + "/" + localId;
        var gate = _resourceRefreshGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsShuttingDown)
                return;
            RunDeclaredResourceRefresh(pluginId, localId);
        }
        finally
        {
            gate.Release();
        }
    }

    private void RunDeclaredResourceRefresh(string pluginId, string localId)
    {
        PluginResourceId id;
        try
        {
            if (!PluginResourceId.TryParse(
                    PluginIdentifiers.SourcePrefix + pluginId + "/" + localId,
                    out id))
            {
                return;
            }

            var log = _plugins.RunDeclaredRefresh(pluginId, localId);
            if (!log.IsOk || !string.Equals(log.Value.Status, "succeeded", StringComparison.Ordinal))
            {
                MarkResourceStale(id);
                return;
            }

            var stdout = log.Value.Stdout ?? "";
            if (stdout.Contains("[hypa truncated plugin output after", StringComparison.Ordinal))
            {
                MarkResourceStale(id);
                return;
            }

            var envelope = TryParseRefreshEnvelope(stdout, id);
            if (envelope is null)
            {
                MarkResourceStale(id);
                return;
            }

            var published = _pluginResources.Publish(envelope);
            if (!published.IsOk)
            {
                MarkResourceStale(id);
                return;
            }

            if (!published.Value.Ignored)
                ScheduleResourceChanged(published.Value.Resource, removed: false);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "plugin resource refresh failed");
            if (PluginResourceId.TryParse(
                    PluginIdentifiers.SourcePrefix + pluginId + "/" + localId,
                    out var staleId))
            {
                MarkResourceStale(staleId);
            }
        }
    }

    internal void KickDeclaredResourceRefresh()
    {
        try
        {
            if (IsShuttingDown)
                return;
            var listed = _plugins.List(null);
            if (!listed.IsOk)
                return;
            foreach (var plugin in listed.Value ?? [])
            {
                if (plugin is not { Enabled: true })
                    continue;
                foreach (var resource in _plugins.DeclaredResources(plugin.PluginId) ?? [])
                {
                    if (resource?.Command is not { Count: > 0 })
                        continue;
                    _ = RefreshPluginResourceAsync(plugin.PluginId, resource.Id, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "plugin resource refresh kick failed");
        }
    }

    internal Task ResourceChangedFlush => _resourceChangedFlush;

    internal void DisposePluginResourceCoalesce()
    {
        // Coalesce uses Task.Delay. The flush task observes IsShuttingDown.
    }

    private void DropPluginResources(string pluginId)
    {
        var removed = _pluginResources.DropPlugin(pluginId);
        foreach (var record in removed)
            ScheduleResourceChanged(record, removed: true);
    }

    private void MarkResourceStale(PluginResourceId id)
    {
        var marked = _pluginResources.MarkStale(id.Value);
        if (!marked.IsOk)
            return;
        if (!_pluginResources.Get(id.Value).IsOk)
            return;
        ScheduleResourceChanged(marked.Value, removed: false);
    }

    private PluginResourcePublishRequest RequirePublishEnvelope(PluginResourcePublishParams p)
    {
        var json = JsonSerializer.Serialize(p, ProtocolJsonContext.Default.PluginResourcePublishParams);
        var bytes = Encoding.UTF8.GetByteCount(json);
        var resourceId = RequireField(p.ResourceId, "resource_id");
        if (!PluginResourceId.TryParse(resourceId, out var id))
        {
            throw PluginFault(PluginError.Of(
                PluginError.InvalidResourceId,
                "resource_id must be plugin:<plugin-id>/<local-id>"));
        }

        if (!string.IsNullOrWhiteSpace(p.OwnerId)
            && !string.Equals(p.OwnerId, id.OwnerId, StringComparison.Ordinal))
        {
            throw PluginFault(PluginError.Of(PluginError.SourceDenied, PluginError.SourceDenied));
        }

        var schema = EmptyToNull(p.Schema) ?? PluginResourceLimits.CollectionSchema;
        if (p.Revision is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "revision is required");
        }

        DateTimeOffset? expiresAt = null;
        if (!string.IsNullOrWhiteSpace(p.ExpiresAt))
        {
            if (!DateTimeOffset.TryParse(p.ExpiresAt, out var parsed))
            {
                throw PluginFault(PluginError.Of(
                    PluginError.ResourceMalformed,
                    "expires_at is invalid"));
            }

            expiresAt = parsed;
        }

        if (p.Value is null)
        {
            throw PluginFault(PluginError.Of(
                PluginError.ResourceMalformed,
                "collection value is required"));
        }

        var value = FromCollectionDto(p.Value);
        var validated = CollectionProjection.Validate(value);
        if (!validated.IsOk)
            throw PluginFault(validated.Error);
        ValidateCollectionAgainstSession(id.PluginId, validated.Value);

        return new PluginResourcePublishRequest
        {
            Id = id,
            Schema = schema,
            Revision = p.Revision.Value,
            ExpiresAt = expiresAt,
            Value = validated.Value,
            PublishBytes = bytes,
        };
    }

    private PluginResourcePublishRequest? TryParseRefreshEnvelope(string stdout, PluginResourceId id)
    {
        var text = stdout.Trim();
        if (text.Length == 0)
            return null;

        PluginResourcePublishParams? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(text, ProtocolJsonContext.Default.PluginResourcePublishParams);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null)
            return null;

        parsed = parsed with
        {
            ResourceId = EmptyToNull(parsed.ResourceId) ?? id.Value,
            Schema = EmptyToNull(parsed.Schema) ?? PluginResourceLimits.CollectionSchema,
            Revision = parsed.Revision ?? NextRefreshRevision(id.Value),
        };

        try
        {
            return RequirePublishEnvelope(parsed);
        }
        catch (ControlPlaneException)
        {
            return null;
        }
    }

    private long NextRefreshRevision(string resourceId) =>
        _pluginResources.TryGetRevision(resourceId, out var revision) && revision > 0
            ? revision + 1
            : 1;

    private void ValidateCollectionAgainstSession(string pluginId, PluginCollectionValue value)
    {
        foreach (var item in value.Items)
        {
            if (item.Action is not null && !_plugins.HasAction(pluginId, item.Action))
            {
                throw PluginFault(PluginError.Of(
                    PluginError.ResourceMalformed,
                    "collection item action is not declared"));
            }

            if (item.Target is null)
                continue;
            if (item.Target.PaneId is { } paneId && _state.GetPane(new PaneId(paneId)) is null)
            {
                throw PluginFault(PluginError.Of(
                    PluginError.ResourceMalformed,
                    "collection item target pane does not exist"));
            }

            if (item.Target.WorkspaceId is { } workspaceId
                && _state.GetWorkspace(new WorkspaceId(workspaceId)) is null)
            {
                throw PluginFault(PluginError.Of(
                    PluginError.ResourceMalformed,
                    "collection item target workspace does not exist"));
            }

            if (item.Target.TabId is { } tabId && _state.GetTab(new TabId(tabId)) is null)
            {
                throw PluginFault(PluginError.Of(
                    PluginError.ResourceMalformed,
                    "collection item target tab does not exist"));
            }
        }
    }

    private void ScheduleResourceChanged(PluginResourceRecord record, bool removed)
    {
        lock (_resourceChangedGate)
        {
            _pendingResourceChanges[record.Id.Value] = new ResourceChangedPending(
                record.Id.Value,
                record.Revision,
                removed,
                record.Value.Summary);
            if (_resourceChangedArmed)
                return;
            _resourceChangedArmed = true;
            _resourceChangedFlush = FlushResourceChangedAfterCoalesceAsync();
        }
    }

    private async Task FlushResourceChangedAfterCoalesceAsync()
    {
        await Task.Delay(PluginResourceLimits.ChangedCoalesce).ConfigureAwait(false);

        ResourceChangedPending[] batch;
        lock (_resourceChangedGate)
        {
            batch = _pendingResourceChanges.Values.ToArray();
            _pendingResourceChanges.Clear();
            _resourceChangedArmed = false;
        }

        if (batch.Length == 0 || _subscriptions is null || IsShuttingDown)
            return;

        foreach (var pending in batch)
            await EmitLiveResourceChangedAsync(pending, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EmitLiveResourceChangedAsync(ResourceChangedPending pending, CancellationToken ct)
    {
        if (_subscriptions is null)
            return;

        var payload = RuntimeEventPayloadJson.WriteResourceChanged(
            pending.ResourceId,
            pending.Revision,
            pending.Removed,
            pending.Summary);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.ResourceChanged, payload);
        var subscriptions = _subscriptions;
        var occurredAt = _time.GetUtcNow();
        await EmitInAllocationOrderAsync(
                _resourceChangedEmitGate,
                () => _paneRenderSeq.AddOrUpdate(
                    ProtocolEventTypes.ResourceChanged, 1L, static (_, prev) => prev + 1),
                async (seq, token) =>
                {
                    var rec = new RuntimeEventRecord
                    {
                        Seq = seq,
                        Class = EventClass.Lifecycle,
                        Reliability = EventReliability.Reliable,
                        Type = ProtocolEventTypes.ResourceChanged,
                        OccurredAt = occurredAt,
                        PayloadJson = payload,
                    };
                    await subscriptions.FanoutLiveAsync(rec, token).ConfigureAwait(false);
                },
                ct)
            .ConfigureAwait(false);
    }

    private JsonArray? SnapshotPluginResources()
    {
        var listed = _pluginResources.List(null);
        if (listed.Count == 0)
            return null;
        var arr = new JsonArray();
        foreach (var record in listed)
        {
            var json = JsonSerializer.Serialize(ToResourceDto(record), ProtocolJsonContext.Default.PluginResourceDto);
            arr.Add(JsonNode.Parse(json));
        }

        return arr;
    }

    private void EnsureActionRevision(PluginActionInvokeParams p)
    {
        var resourceId = EmptyToNull(p.ResourceId);
        if (resourceId is null)
            return;
        if (p.Revision is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "revision is required when resource_id is set");
        }

        if (!_pluginResources.TryGetRevision(resourceId, out var current))
        {
            throw PluginFault(PluginError.Of(
                PluginError.ResourceNotFound,
                "plugin resource not found"));
        }

        if (p.Revision.Value != current)
        {
            throw PluginFault(PluginError.Of(
                PluginError.StaleRevision,
                PluginError.StaleRevision));
        }
    }

    private static PluginResourceDto ToResourceDto(PluginResourceRecord record) =>
        new()
        {
            OwnerId = record.Id.OwnerId,
            ResourceId = record.Id.Value,
            Schema = record.Schema,
            Revision = record.Revision,
            Freshness = record.Freshness,
            ExpiresAt = record.ExpiresAt?.ToString("O"),
            Value = ToCollectionDto(record.Value),
        };

    private static PluginCollectionValue FromCollectionDto(PluginCollectionValueDto? value) =>
        new()
        {
            Summary = value?.Summary,
            Items = (value?.Items ?? []).Select(FromItemDto).ToArray(),
        };

    private static PluginCollectionItem FromItemDto(PluginCollectionItemDto item) =>
        new()
        {
            Id = item.Id ?? "",
            Label = item.Label,
            Status = item.Status ?? "",
            Attention = item.Attention,
            Tokens = item.Tokens is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(item.Tokens, StringComparer.Ordinal),
            Action = item.Action,
            Target = item.Target is null
                ? null
                : new PluginResourceTarget
                {
                    PaneId = item.Target.PaneId,
                    WorkspaceId = item.Target.WorkspaceId,
                    TabId = item.Target.TabId,
                },
            Sequence = item.Sequence,
            TtlMs = item.TtlMs,
        };

    private static PluginCollectionValueDto ToCollectionDto(PluginCollectionValue value) =>
        new()
        {
            Summary = value.Summary,
            Items = value.Items.Select(ToItemDto).ToArray(),
        };

    private static PluginCollectionItemDto ToItemDto(PluginCollectionItem item) =>
        new()
        {
            Id = item.Id,
            Label = item.Label,
            Status = item.Status,
            Attention = item.Attention,
            Tokens = item.Tokens.Count == 0
                ? null
                : new Dictionary<string, string>(item.Tokens, StringComparer.Ordinal),
            Action = item.Action,
            Target = item.Target is null
                ? null
                : new PluginResourceTargetDto
                {
                    PaneId = item.Target.PaneId,
                    WorkspaceId = item.Target.WorkspaceId,
                    TabId = item.Target.TabId,
                },
            Sequence = item.Sequence,
            TtlMs = item.TtlMs,
        };

    private sealed record ResourceChangedPending(
        string ResourceId,
        long Revision,
        bool Removed,
        string? Summary);
}
