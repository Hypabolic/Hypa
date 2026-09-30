namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// Wire resource id <c>plugin:&lt;plugin-id&gt;/&lt;local-id&gt;</c>.
/// </summary>
public readonly record struct PluginResourceId(string PluginId, string LocalId)
{
    public string Value => PluginIdentifiers.SourcePrefix + PluginId + "/" + LocalId;

    public string OwnerId => PluginIdentifiers.SourceOf(PluginId);

    public string Prefix => PluginIdentifiers.SourcePrefix + PluginId + "/";

    public static bool TryParse(string? value, out PluginResourceId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(PluginIdentifiers.SourcePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = value[PluginIdentifiers.SourcePrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
            return false;

        var pluginId = PluginIdentifiers.NormalizePluginId(rest[..slash]);
        var localId = PluginIdentifiers.NormalizeLocalId(rest[(slash + 1)..]);
        if (pluginId is null || localId is null)
            return false;

        id = new PluginResourceId(pluginId, localId);
        return true;
    }

    public static bool BelongsTo(string? resourceId, string pluginId) =>
        TryParse(resourceId, out var parsed)
        && string.Equals(parsed.PluginId, pluginId, StringComparison.Ordinal);
}
