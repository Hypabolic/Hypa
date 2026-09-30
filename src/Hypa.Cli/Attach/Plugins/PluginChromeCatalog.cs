using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.Cli.Attach.Mouse;

namespace Hypa.Cli.Attach.Plugins;

/// <summary>Attach-side view of plugin menu and palette rows.</summary>
public static class PluginChromeCatalog
{
    public const string MenuItemIdPrefix = "plugin_action:";

    public static IReadOnlyList<ContextMenuItem> MenuItems(
        IEnumerable<InstalledPlugin> plugins,
        string context)
    {
        var rows = PluginChromeDiscovery.MenuItemsForContext(plugins, context);
        var items = new List<ContextMenuItem>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(new ContextMenuItem(
                MenuItemIdPrefix + row.QualifiedActionId,
                row.Title,
                PluginQualifiedActionId: row.QualifiedActionId));
        }

        return items;
    }

    public static IReadOnlyList<GotoTarget> PaletteTargets(IEnumerable<InstalledPlugin> plugins)
    {
        var rows = PluginChromeDiscovery.PaletteActions(plugins);
        var targets = new List<GotoTarget>(rows.Count);
        foreach (var row in rows)
        {
            targets.Add(new GotoTarget(
                GotoTargetKind.PluginAction,
                row.QualifiedActionId,
                row.Title,
                KindText: "command"));
        }

        return targets;
    }

    public static IReadOnlyList<GotoTarget> CommandPickerTargets(IEnumerable<InstalledPlugin> plugins) =>
        PaletteTargets(plugins);

    public static bool TryParseMenuActionId(string? itemId, out string qualifiedActionId)
    {
        qualifiedActionId = "";
        if (string.IsNullOrWhiteSpace(itemId)
            || !itemId.StartsWith(MenuItemIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        qualifiedActionId = itemId[MenuItemIdPrefix.Length..];
        return qualifiedActionId.Length > 0;
    }

    public static bool IsPluginActionTarget(GotoTarget target) =>
        target.Kind is GotoTargetKind.PluginAction;
}
