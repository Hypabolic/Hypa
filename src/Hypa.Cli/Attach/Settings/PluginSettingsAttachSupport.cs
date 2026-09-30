using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;

namespace Hypa.Cli.Attach.Settings;

internal static class PluginSettingsAttachSupport
{
    internal static async Task RefreshAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        try
        {
            var listed = await control.CallAsync(ProtocolMethods.PluginList, null, ct).ConfigureAwait(false);
            var dto = listed.Deserialize(ProtocolJsonContext.Default.PluginListResult);
            var pages = await BuildPagesAsync(control, dto, ct).ConfigureAwait(false);
            var registry = SettingsPageRegistry.ProductWithPlugins(pages);
            live.Engine.Settings.BindPluginPages(pages, registry);
        }
        catch (Exception ex)
        {
            live.StatusError = ex.Message;
            live.Engine.Settings.BindPluginPages([], SettingsPageRegistry.Product());
        }
    }

    internal static async Task ApplyPendingAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        var pending = live.Engine.Settings.PendingPluginConfigWrite;
        if (pending is null)
            return;

        try
        {
            var result = await control.CallAsync(
                    ProtocolMethods.PluginConfigSet,
                    new JsonObject
                    {
                        ["plugin_id"] = pending.PluginId,
                        ["key"] = pending.Key,
                        ["value"] = pending.Value,
                    },
                    ct)
                .ConfigureAwait(false);
            var dto = result.Deserialize(ProtocolJsonContext.Default.PluginConfigSetResult);
            if (dto?.Values is not null
                && dto.Key is not null
                && dto.PluginId is not null
                && dto.Values.TryGetValue(dto.Key, out var stored))
            {
                live.Engine.Settings.ApplyPluginWriteResult(dto.PluginId, dto.Key, dto.Applied, stored);
            }
        }
        catch (Exception ex)
        {
            live.StatusError = ex.Message;
        }
        finally
        {
            live.Engine.Settings.ClearPendingPatch();
        }
    }

    private static async Task<IReadOnlyList<PluginSettingsPageView>> BuildPagesAsync(
        IAttachCommandPort control,
        PluginListResult? listed,
        CancellationToken ct)
    {
        if (listed?.Plugins is null || listed.Plugins.Count == 0)
            return [];

        var pages = new List<PluginSettingsPageView>();
        foreach (var plugin in listed.Plugins)
        {
            if (plugin.Enabled != true
                || plugin.SettingsFields is null
                || plugin.SettingsFields.Count == 0
                || string.IsNullOrWhiteSpace(plugin.PluginId)
                || string.IsNullOrWhiteSpace(plugin.Name))
            {
                continue;
            }

            var values = await ReadValuesAsync(control, plugin.PluginId, ct).ConfigureAwait(false);
            var fields = new PluginSettingsFieldView[plugin.SettingsFields.Count];
            for (var i = 0; i < plugin.SettingsFields.Count; i++)
            {
                var field = plugin.SettingsFields[i];
                var key = field.Key ?? "";
                values.TryGetValue(key, out var value);
                if (string.IsNullOrEmpty(value))
                    value = field.Default ?? "";
                fields[i] = new PluginSettingsFieldView(
                    key,
                    field.Type ?? "string",
                    field.Title ?? key,
                    field.Choices ?? [],
                    value);
            }

            pages.Add(new PluginSettingsPageView(plugin.PluginId, plugin.Name, fields));
        }

        return pages;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadValuesAsync(
        IAttachCommandPort control,
        string pluginId,
        CancellationToken ct)
    {
        var result = await control.CallAsync(
                ProtocolMethods.PluginConfigGet,
                new JsonObject { ["plugin_id"] = pluginId },
                ct)
            .ConfigureAwait(false);
        var dto = result.Deserialize(ProtocolJsonContext.Default.PluginConfigGetResult);
        return dto?.Values is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(dto.Values, StringComparer.Ordinal);
    }
}
