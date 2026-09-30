using Hypa.AgentRuntime.Application.Metadata;

namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// Validates <c>hypa.projection.collection.v1</c>. Unknown versions fail closed.
/// A bad envelope is rejected as one unit.
/// </summary>
public static class CollectionProjection
{
    public static PluginResult<PluginCollectionValue> Validate(PluginCollectionValue? value)
    {
        if (value is null)
        {
            return PluginResult<PluginCollectionValue>.Fail(
                PluginError.ResourceMalformed,
                "collection value is required");
        }

        var summary = EmptyToNull(MetadataTokenNormalizer.NormalizeValue(value.Summary));
        var rawItems = value.Items ?? [];
        if (rawItems.Count > PluginResourceLimits.MaxItemsPerCollection)
        {
            return PluginResult<PluginCollectionValue>.Fail(
                PluginError.ResourceFull,
                "collection exceeds 200 items");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<PluginCollectionItem>(rawItems.Count);
        foreach (var raw in rawItems)
        {
            var item = BindItem(raw);
            if (!item.IsOk)
                return PluginResult<PluginCollectionValue>.Fail(item.Error);
            if (!seen.Add(item.Value.Id))
            {
                return PluginResult<PluginCollectionValue>.Fail(
                    PluginError.ResourceMalformed,
                    "duplicate collection item id");
            }

            items.Add(item.Value);
        }

        return PluginResult<PluginCollectionValue>.Ok(new PluginCollectionValue
        {
            Summary = summary,
            Items = items,
        });
    }

    private static PluginResult<PluginCollectionItem> BindItem(PluginCollectionItem? raw)
    {
        if (raw is null)
        {
            return PluginResult<PluginCollectionItem>.Fail(
                PluginError.ResourceMalformed,
                "collection item is required");
        }

        if (!PluginItemId.TryNormalize(raw.Id, out var id))
        {
            return PluginResult<PluginCollectionItem>.Fail(
                PluginError.ResourceMalformed,
                "collection item id is invalid");
        }

        var status = string.IsNullOrWhiteSpace(raw.Status) ? "unknown" : raw.Status.Trim();
        if (!PluginResourceLimits.ItemStatuses.Contains(status))
        {
            return PluginResult<PluginCollectionItem>.Fail(
                PluginError.ResourceMalformed,
                "collection item status is invalid");
        }

        if (raw.Attention < 0)
        {
            return PluginResult<PluginCollectionItem>.Fail(
                PluginError.ResourceMalformed,
                "collection item attention must be unsigned");
        }

        if (raw.Sequence is { } seq && seq < 0)
        {
            return PluginResult<PluginCollectionItem>.Fail(
                PluginError.ResourceMalformed,
                "collection item seq is invalid");
        }

        if (!MetadataTokenNormalizer.TryNormalizeTtl(raw.TtlMs, out _))
        {
            return PluginResult<PluginCollectionItem>.Fail(
                PluginError.ResourceMalformed,
                "ttl_ms is out of range");
        }

        var tokens = BindTokens(raw.Tokens);
        if (!tokens.IsOk)
            return PluginResult<PluginCollectionItem>.Fail(tokens.Error);

        var action = EmptyToNull(raw.Action);
        if (action is not null && PluginIdentifiers.NormalizeLocalId(action) is null)
        {
            return PluginResult<PluginCollectionItem>.Fail(
                PluginError.ResourceMalformed,
                "collection item action is invalid");
        }

        var target = BindTarget(raw.Target);
        if (!target.IsOk)
            return PluginResult<PluginCollectionItem>.Fail(target.Error);

        return PluginResult<PluginCollectionItem>.Ok(new PluginCollectionItem
        {
            Id = id,
            Label = EmptyToNull(MetadataTokenNormalizer.NormalizeValue(raw.Label)),
            Status = status,
            Attention = raw.Attention,
            Tokens = tokens.Value,
            Action = action is null ? null : PluginIdentifiers.NormalizeLocalId(action),
            Target = target.Value,
            Sequence = raw.Sequence,
            TtlMs = raw.TtlMs,
        });
    }

    private static PluginResult<IReadOnlyDictionary<string, string>> BindTokens(
        IReadOnlyDictionary<string, string>? tokens)
    {
        if (tokens is null || tokens.Count == 0)
        {
            return PluginResult<IReadOnlyDictionary<string, string>>.Ok(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        if (tokens.Count > MetadataTokenLimits.MaxKeysPerReport)
        {
            return PluginResult<IReadOnlyDictionary<string, string>>.Fail(
                PluginError.ResourceMalformed,
                "tokens must contain 1 to 16 keys");
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in tokens)
        {
            if (!MetadataTokenNormalizer.TryNormalizeKey(pair.Key, out var key))
            {
                return PluginResult<IReadOnlyDictionary<string, string>>.Fail(
                    PluginError.ResourceMalformed,
                    "token key is invalid");
            }

            var text = MetadataTokenNormalizer.NormalizeValue(pair.Value);
            if (text.Length == 0)
                continue;
            normalized[key] = text;
        }

        if (normalized.Count > MetadataTokenLimits.MaxRetainedKeys)
        {
            return PluginResult<IReadOnlyDictionary<string, string>>.Fail(
                PluginError.ResourceMalformed,
                "too many retained token keys");
        }

        return PluginResult<IReadOnlyDictionary<string, string>>.Ok(normalized);
    }

    private static PluginResult<PluginResourceTarget?> BindTarget(PluginResourceTarget? target)
    {
        if (target is null)
            return PluginResult<PluginResourceTarget?>.Ok(null);

        var pane = EmptyToNull(target.PaneId);
        var workspace = EmptyToNull(target.WorkspaceId);
        var tab = EmptyToNull(target.TabId);
        var count = (pane is null ? 0 : 1) + (workspace is null ? 0 : 1) + (tab is null ? 0 : 1);
        if (count == 0)
            return PluginResult<PluginResourceTarget?>.Ok(null);
        if (count != 1)
        {
            return PluginResult<PluginResourceTarget?>.Fail(
                PluginError.ResourceMalformed,
                "collection item target must name one workspace, tab, or pane");
        }

        return PluginResult<PluginResourceTarget?>.Ok(new PluginResourceTarget
        {
            PaneId = pane,
            WorkspaceId = workspace,
            TabId = tab,
        });
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
