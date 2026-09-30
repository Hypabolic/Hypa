using Hypa.AgentRuntime.Domain.Plugins;

namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>Builds plugin menu and palette rows from linked enabled manifests.</summary>
public static class PluginChromeDiscovery
{
    public static IReadOnlyList<PluginChromeMenuEntry> MenuItemsForContext(
        IEnumerable<InstalledPlugin> plugins,
        string context)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        if (!PluginMenuContexts.IsAllowed(context))
            return [];

        var rows = new List<PluginChromeMenuEntry>();
        foreach (var plugin in plugins.Where(Available))
        {
            foreach (var item in plugin.MenuItems)
            {
                if (!item.Contexts.Contains(context, StringComparer.Ordinal))
                    continue;
                if (!TryResolveAction(plugin, item.Action, out var action))
                    continue;
                rows.Add(new PluginChromeMenuEntry
                {
                    PluginId = plugin.PluginId,
                    ItemId = item.Id,
                    Title = item.Title,
                    ActionId = action.Id,
                    QualifiedActionId = plugin.PluginId + "." + action.Id,
                    Context = context,
                });
            }
        }

        rows.Sort(static (a, b) =>
            string.CompareOrdinal(a.PluginId, b.PluginId) is var byPlugin and not 0
                ? byPlugin
                : string.CompareOrdinal(a.Title, b.Title));
        return rows;
    }

    public static IReadOnlyList<PluginChromePaletteEntry> PaletteActions(IEnumerable<InstalledPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        var rows = new List<PluginChromePaletteEntry>();
        foreach (var plugin in plugins.Where(Available))
        {
            foreach (var action in plugin.Actions.Where(a => a.Palette))
            {
                rows.Add(new PluginChromePaletteEntry
                {
                    PluginId = plugin.PluginId,
                    ActionId = action.Id,
                    Title = action.Title,
                    Description = action.Description,
                    QualifiedActionId = plugin.PluginId + "." + action.Id,
                });
            }
        }

        rows.Sort(static (a, b) =>
            string.CompareOrdinal(a.PluginId, b.PluginId) is var byPlugin and not 0
                ? byPlugin
                : string.CompareOrdinal(a.Title, b.Title));
        return rows;
    }

    private static bool Available(InstalledPlugin plugin) =>
        plugin.Enabled && !HasManifestUnavailableWarning(plugin);

    private static bool HasManifestUnavailableWarning(InstalledPlugin plugin)
    {
        foreach (var warning in plugin.Warnings)
        {
            if (warning.StartsWith(PluginHostService.ManifestUnavailablePrefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryResolveAction(
        InstalledPlugin plugin,
        string actionId,
        out PluginManifestAction action)
    {
        action = default!;
        var local = PluginIdentifiers.NormalizeLocalId(actionId);
        if (local is null)
            return false;
        var found = plugin.Actions.FirstOrDefault(a => a.Id == local);
        if (found is null)
            return false;
        action = found;
        return true;
    }
}
