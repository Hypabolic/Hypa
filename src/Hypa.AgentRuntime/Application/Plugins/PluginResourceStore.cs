namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// In-memory plugin resource store. Token values are not persisted.
/// A bad publish is rejected as one unit. The last good revision stays.
/// </summary>
public sealed class PluginResourceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginResourceRecord> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _itemSequences = new(StringComparer.Ordinal);

    public PluginResult<PluginResourcePublishOutcome> Publish(PluginResourcePublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PublishBytes > PluginResourceLimits.MaxPublishBytes)
        {
            return PluginResult<PluginResourcePublishOutcome>.Fail(
                PluginError.ResourceFull,
                "publish exceeds 64 KiB");
        }

        if (request.Revision <= 0)
        {
            return PluginResult<PluginResourcePublishOutcome>.Fail(
                PluginError.ResourceMalformed,
                "revision must be a positive integer");
        }

        if (!string.Equals(request.Schema, PluginResourceLimits.CollectionSchema, StringComparison.Ordinal))
        {
            return PluginResult<PluginResourcePublishOutcome>.Fail(
                PluginError.UnknownProjection,
                "unknown projection version");
        }

        var value = CollectionProjection.Validate(request.Value);
        if (!value.IsOk)
            return PluginResult<PluginResourcePublishOutcome>.Fail(value.Error);

        lock (_gate)
        {
            var key = request.Id.Value;
            if (_byId.TryGetValue(key, out var existing) && request.Revision <= existing.Revision)
            {
                return PluginResult<PluginResourcePublishOutcome>.Ok(new PluginResourcePublishOutcome
                {
                    Ignored = true,
                    Resource = existing,
                });
            }

            if (existing is null)
            {
                if (CountForPlugin(request.Id.PluginId) >= PluginResourceLimits.MaxResourcesPerPlugin)
                {
                    return PluginResult<PluginResourcePublishOutcome>.Fail(
                        PluginError.ResourceFull,
                        "plugin resource cap reached");
                }

                if (_byId.Count >= PluginResourceLimits.MaxResourcesPerServer)
                {
                    return PluginResult<PluginResourcePublishOutcome>.Fail(
                        PluginError.ResourceFull,
                        "server resource cap reached");
                }
            }

            RecordItemSequences(request.Id.Value, value.Value);

            var record = new PluginResourceRecord
            {
                Id = request.Id,
                Schema = request.Schema,
                Revision = request.Revision,
                Freshness = PluginResourceLimits.FreshnessReady,
                ExpiresAt = request.ExpiresAt,
                Value = value.Value,
            };
            _byId[key] = record;
            return PluginResult<PluginResourcePublishOutcome>.Ok(new PluginResourcePublishOutcome
            {
                Ignored = false,
                Resource = record,
            });
        }
    }

    public PluginResult<PluginResourceRecord> Get(string resourceId)
    {
        if (!PluginResourceId.TryParse(resourceId, out var id))
        {
            return PluginResult<PluginResourceRecord>.Fail(
                PluginError.InvalidResourceId,
                "resource_id must be plugin:<plugin-id>/<local-id>");
        }

        lock (_gate)
        {
            if (!_byId.TryGetValue(id.Value, out var record))
            {
                return PluginResult<PluginResourceRecord>.Fail(
                    PluginError.ResourceNotFound,
                    "plugin resource not found");
            }

            return PluginResult<PluginResourceRecord>.Ok(WithExpiry(record));
        }
    }

    public IReadOnlyList<PluginResourceRecord> List(string? pluginId)
    {
        lock (_gate)
        {
            IEnumerable<PluginResourceRecord> items = _byId.Values;
            if (!string.IsNullOrWhiteSpace(pluginId))
            {
                items = items.Where(r =>
                    string.Equals(r.Id.PluginId, pluginId, StringComparison.Ordinal));
            }

            return items
                .Select(WithExpiry)
                .OrderBy(r => r.Id.Value, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public PluginResult<PluginResourceRecord> Remove(string resourceId, string? callerPluginId)
    {
        if (!PluginResourceId.TryParse(resourceId, out var id))
        {
            return PluginResult<PluginResourceRecord>.Fail(
                PluginError.InvalidResourceId,
                "resource_id must be plugin:<plugin-id>/<local-id>");
        }

        if (callerPluginId is not null
            && !string.Equals(id.PluginId, callerPluginId, StringComparison.Ordinal))
        {
            return PluginResult<PluginResourceRecord>.Fail(
                PluginError.SourceDenied,
                PluginError.SourceDenied);
        }

        lock (_gate)
        {
            if (!_byId.Remove(id.Value, out var record))
            {
                return PluginResult<PluginResourceRecord>.Fail(
                    PluginError.ResourceNotFound,
                    "plugin resource not found");
            }

            ClearItemSequences(id.Value);
            return PluginResult<PluginResourceRecord>.Ok(record);
        }
    }

    public IReadOnlyList<PluginResourceRecord> DropPlugin(string pluginId)
    {
        lock (_gate)
        {
            var removed = _byId.Values
                .Where(r => string.Equals(r.Id.PluginId, pluginId, StringComparison.Ordinal))
                .ToArray();
            foreach (var record in removed)
            {
                _byId.Remove(record.Id.Value);
                ClearItemSequences(record.Id.Value);
            }

            return removed;
        }
    }

    public PluginResult<PluginResourceRecord> MarkStale(string resourceId)
    {
        if (!PluginResourceId.TryParse(resourceId, out var id))
        {
            return PluginResult<PluginResourceRecord>.Fail(
                PluginError.InvalidResourceId,
                "resource_id must be plugin:<plugin-id>/<local-id>");
        }

        lock (_gate)
        {
            if (_byId.TryGetValue(id.Value, out var existing))
            {
                var next = existing with { Freshness = PluginResourceLimits.FreshnessStale };
                _byId[id.Value] = next;
                return PluginResult<PluginResourceRecord>.Ok(next);
            }

            var placeholder = new PluginResourceRecord
            {
                Id = id,
                Schema = PluginResourceLimits.CollectionSchema,
                Revision = 0,
                Freshness = PluginResourceLimits.FreshnessUnavailable,
                Value = new PluginCollectionValue(),
            };
            if (CountForPlugin(id.PluginId) >= PluginResourceLimits.MaxResourcesPerPlugin
                || _byId.Count >= PluginResourceLimits.MaxResourcesPerServer)
            {
                return PluginResult<PluginResourceRecord>.Ok(placeholder);
            }

            _byId[id.Value] = placeholder;
            return PluginResult<PluginResourceRecord>.Ok(placeholder);
        }
    }

    public bool TryGetRevision(string resourceId, out long revision)
    {
        revision = 0;
        lock (_gate)
        {
            if (!_byId.TryGetValue(resourceId, out var record))
                return false;
            revision = record.Revision;
            return true;
        }
    }

    private int CountForPlugin(string pluginId)
    {
        var count = 0;
        foreach (var record in _byId.Values)
        {
            if (string.Equals(record.Id.PluginId, pluginId, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    private void RecordItemSequences(string resourceId, PluginCollectionValue value)
    {
        foreach (var item in value.Items)
        {
            if (item.Sequence is not { } seq)
                continue;
            var seqKey = resourceId + "\u001f" + item.Id;
            if (_itemSequences.TryGetValue(seqKey, out var previous) && seq <= previous)
                continue;
            _itemSequences[seqKey] = seq;
        }
    }

    private void ClearItemSequences(string resourceId)
    {
        var prefix = resourceId + "\u001f";
        foreach (var key in _itemSequences.Keys.ToArray())
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                _itemSequences.Remove(key);
        }
    }

    private static PluginResourceRecord WithExpiry(PluginResourceRecord record)
    {
        if (record.ExpiresAt is { } deadline && deadline <= DateTimeOffset.UtcNow
            && record.Freshness == PluginResourceLimits.FreshnessReady)
        {
            return record with { Freshness = PluginResourceLimits.FreshnessStale };
        }

        return record;
    }
}
