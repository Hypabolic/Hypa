using Hypa.AgentRuntime.Application.Plugins;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Plugins;

namespace Hypa.Cli.Attach.Sidebar;

/// <summary>Footer global menu. Settings / keybinds / reload / pack notes / detach.</summary>
public static class GlobalMenuModel
{
    public const string Settings = "settings";
    public const string Keybinds = "keybinds";
    public const string Reload = "reload";
    public const string WhatsNew = "whats_new";
    public const string HiddenPanes = HiddenPaneMenuModel.HiddenPanes;
    public const string Detach = ContextMenuModel.Detach;

    public static IReadOnlyList<ContextMenuItem> Items(IEnumerable<InstalledPlugin>? plugins = null)
    {
        var items = new List<ContextMenuItem>
        {
            new(Settings, "settings", KeyActionId.Settings),
            new(Keybinds, "keybinds", KeyActionId.Help),
            new(Reload, "reload config", KeyActionId.ReloadConfig),
            new(HiddenPanes, HiddenPaneMenuModel.HiddenPanesLabel),
            new(WhatsNew, "what's new"),
            new(Detach, "detach", KeyActionId.Detach),
        };
        foreach (var pluginItem in PluginChromeCatalog.MenuItems(
                     plugins ?? [],
                     Hypa.AgentRuntime.Domain.Plugins.PluginMenuContexts.GlobalMenu))
        {
            items.Insert(Math.Max(0, items.Count - 1), pluginItem);
        }

        return items;
    }

    public static ContextMenuModel ForGlobal(int col, int row, int cols, int rows) =>
        ContextMenuModel.ForGlobal(col, row, cols, rows);

}
