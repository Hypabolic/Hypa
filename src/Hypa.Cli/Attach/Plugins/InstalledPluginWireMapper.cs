using System.Text.Json;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.Cli.Attach.Plugins;

/// <summary>
/// Maps <c>plugin.list</c> wire rows onto attach-side
/// <see cref="InstalledPlugin"/> records. Palette, menu, and settings
/// read this list.
/// </summary>
internal static class InstalledPluginWireMapper
{
    public static IReadOnlyList<InstalledPlugin> FromListResult(JsonElement listed)
    {
        if (listed.ValueKind != JsonValueKind.Object)
            return [];
        var dto = listed.Deserialize(ProtocolJsonContext.Default.PluginListResult);
        return FromList(dto);
    }

    public static IReadOnlyList<InstalledPlugin> FromList(PluginListResult? listed)
    {
        if (listed?.Plugins is null || listed.Plugins.Count == 0)
            return [];
        var list = new List<InstalledPlugin>(listed.Plugins.Count);
        foreach (var plugin in listed.Plugins)
        {
            var mapped = FromDto(plugin);
            if (mapped is not null)
                list.Add(mapped);
        }

        return list;
    }

    public static InstalledPlugin FromManifest(PluginManifest manifest, string manifestPath, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var parent = Path.GetDirectoryName(manifestPath) ?? "";
        return new InstalledPlugin
        {
            PluginId = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            MinHypaVersion = manifest.MinHypaVersion,
            Description = manifest.Description,
            ManifestPath = manifestPath,
            PluginRoot = parent,
            Enabled = enabled,
            Platforms = manifest.Platforms,
            Build = manifest.Build,
            Startup = manifest.Startup,
            Actions = manifest.Actions,
            Events = manifest.Events,
            Panes = manifest.Panes,
            LinkHandlers = manifest.LinkHandlers,
            Resources = manifest.Resources,
            MenuItems = manifest.MenuItems,
            SettingsFields = manifest.SettingsFields,
            Doctor = manifest.Doctor,
            RequestedGrants = manifest.RequestedGrants,
            Warnings = manifest.Warnings,
            EnableGeneration = 0,
            SourceKind = "local",
        };
    }

    public static InstalledPluginDto ToDto(InstalledPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return new InstalledPluginDto
        {
            PluginId = plugin.PluginId,
            Name = plugin.Name,
            Version = plugin.Version,
            MinHypaVersion = plugin.MinHypaVersion,
            Description = plugin.Description,
            ManifestPath = plugin.ManifestPath,
            PluginRoot = plugin.PluginRoot,
            Enabled = plugin.Enabled,
            Platforms = plugin.Platforms,
            Actions = plugin.Actions.Select(a => new PluginManifestActionDto
            {
                Id = a.Id,
                Title = a.Title,
                Description = a.Description,
                Contexts = a.Contexts,
                Platforms = a.Platforms,
                Command = a.Command,
                Palette = a.Palette,
                Key = a.SuggestedKey,
            }).ToArray(),
            Events = plugin.Events.Select(e => new PluginManifestEventDto
            {
                On = e.On,
                Platforms = e.Platforms,
                Command = e.Command,
            }).ToArray(),
            Panes = plugin.Panes.Select(pane => new PluginManifestPaneDto
            {
                Id = pane.Id,
                Title = pane.Title,
                Description = pane.Description,
                Platforms = pane.Platforms,
                Placement = pane.Placement,
                Command = pane.Command,
            }).ToArray(),
            LinkHandlers = plugin.LinkHandlers.Select(handler => new PluginManifestLinkHandlerDto
            {
                Id = handler.Id,
                Title = handler.Title,
                Pattern = handler.Pattern,
                Action = handler.Action,
                Platforms = handler.Platforms,
            }).ToArray(),
            Startup = plugin.Startup.Select(s => new PluginCommandSpecDto
            {
                Platforms = s.Platforms,
                Command = s.Command,
            }).ToArray(),
            Resources = plugin.Resources.Count == 0
                ? null
                : plugin.Resources.Select(r => new PluginManifestResourceDto
                {
                    Id = r.Id,
                    Kind = r.Kind,
                    Projection = r.Projection,
                    Title = r.Title,
                    Platforms = r.Platforms,
                    Command = r.Command,
                }).ToArray(),
            MenuItems = plugin.MenuItems.Count == 0
                ? null
                : plugin.MenuItems.Select(item => new PluginManifestMenuItemDto
                {
                    Id = item.Id,
                    Title = item.Title,
                    Contexts = item.Contexts,
                    Action = item.Action,
                }).ToArray(),
            SettingsFields = plugin.SettingsFields.Count == 0
                ? null
                : plugin.SettingsFields.Select(f => new PluginManifestSettingsFieldDto
                {
                    Key = f.Key,
                    Type = f.Type,
                    Title = f.Title,
                    Default = f.Default,
                    Choices = f.Choices.Count == 0 ? null : f.Choices,
                }).ToArray(),
            Grants = plugin.RequestedGrants,
            Warnings = plugin.Warnings,
        };
    }

    public static InstalledPlugin? FromDto(InstalledPluginDto? dto)
    {
        if (dto is null
            || string.IsNullOrWhiteSpace(dto.PluginId)
            || string.IsNullOrWhiteSpace(dto.Name))
        {
            return null;
        }

        return new InstalledPlugin
        {
            PluginId = dto.PluginId,
            Name = dto.Name,
            Version = string.IsNullOrWhiteSpace(dto.Version) ? "0.0.0" : dto.Version,
            MinHypaVersion = dto.MinHypaVersion ?? "0.0.0",
            Description = dto.Description,
            ManifestPath = dto.ManifestPath ?? "",
            PluginRoot = dto.PluginRoot ?? "",
            Enabled = dto.Enabled,
            Platforms = dto.Platforms,
            Startup = MapCommands(dto.Startup),
            Actions = MapActions(dto.Actions),
            Events = MapEvents(dto.Events),
            Panes = MapPanes(dto.Panes),
            LinkHandlers = MapLinkHandlers(dto.LinkHandlers),
            Resources = MapResources(dto.Resources),
            MenuItems = MapMenuItems(dto.MenuItems),
            SettingsFields = MapSettings(dto.SettingsFields),
            RequestedGrants = dto.Grants ?? [],
            Warnings = dto.Warnings ?? [],
            SourceKind = "local",
        };
    }

    private static IReadOnlyList<PluginCommandSpec> MapCommands(IReadOnlyList<PluginCommandSpecDto>? rows)
    {
        if (rows is null || rows.Count == 0)
            return [];
        var list = new List<PluginCommandSpec>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Command is null || row.Command.Count == 0)
                continue;
            list.Add(new PluginCommandSpec { Platforms = row.Platforms, Command = row.Command });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestAction> MapActions(IReadOnlyList<PluginManifestActionDto>? rows)
    {
        if (rows is null || rows.Count == 0)
            return [];
        var list = new List<PluginManifestAction>(rows.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Title))
                continue;
            list.Add(new PluginManifestAction
            {
                Id = row.Id,
                Title = row.Title,
                Description = row.Description,
                Contexts = row.Contexts ?? [],
                Platforms = row.Platforms,
                Command = row.Command ?? [],
                Palette = row.Palette,
                SuggestedKey = row.Key,
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestEventHook> MapEvents(IReadOnlyList<PluginManifestEventDto>? rows)
    {
        if (rows is null || rows.Count == 0)
            return [];
        var list = new List<PluginManifestEventHook>(rows.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.On) || row.Command is null || row.Command.Count == 0)
                continue;
            list.Add(new PluginManifestEventHook
            {
                On = row.On,
                Platforms = row.Platforms,
                Command = row.Command,
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestPane> MapPanes(IReadOnlyList<PluginManifestPaneDto>? rows)
    {
        if (rows is null || rows.Count == 0)
            return [];
        var list = new List<PluginManifestPane>(rows.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Title))
                continue;
            list.Add(new PluginManifestPane
            {
                Id = row.Id,
                Title = row.Title,
                Description = row.Description,
                Platforms = row.Platforms,
                Placement = string.IsNullOrWhiteSpace(row.Placement) ? "overlay" : row.Placement,
                Command = row.Command ?? [],
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestLinkHandler> MapLinkHandlers(
        IReadOnlyList<PluginManifestLinkHandlerDto>? rows)
    {
        if (rows is null || rows.Count == 0)
            return [];
        var list = new List<PluginManifestLinkHandler>(rows.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id)
                || string.IsNullOrWhiteSpace(row.Title)
                || string.IsNullOrWhiteSpace(row.Pattern)
                || string.IsNullOrWhiteSpace(row.Action))
            {
                continue;
            }

            list.Add(new PluginManifestLinkHandler
            {
                Id = row.Id,
                Title = row.Title,
                Pattern = row.Pattern,
                Action = row.Action,
                Platforms = row.Platforms,
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestResource> MapResources(IReadOnlyList<PluginManifestResourceDto>? rows)
    {
        if (rows is null || rows.Count == 0)
            return [];
        var list = new List<PluginManifestResource>(rows.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id)
                || string.IsNullOrWhiteSpace(row.Kind)
                || string.IsNullOrWhiteSpace(row.Projection)
                || string.IsNullOrWhiteSpace(row.Title))
            {
                continue;
            }

            list.Add(new PluginManifestResource
            {
                Id = row.Id,
                Kind = row.Kind,
                Projection = row.Projection,
                Title = row.Title,
                Platforms = row.Platforms,
                Command = row.Command,
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestMenuItem> MapMenuItems(IReadOnlyList<PluginManifestMenuItemDto>? rows)
    {
        if (rows is null || rows.Count == 0)
            return [];
        var list = new List<PluginManifestMenuItem>(rows.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id)
                || string.IsNullOrWhiteSpace(row.Title)
                || string.IsNullOrWhiteSpace(row.Action))
            {
                continue;
            }

            list.Add(new PluginManifestMenuItem
            {
                Id = row.Id,
                Title = row.Title,
                Contexts = row.Contexts ?? [],
                Action = row.Action,
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestSettingsField> MapSettings(
        IReadOnlyList<PluginManifestSettingsFieldDto>? rows)
    {
        if (rows is null || rows.Count == 0)
            return [];
        var list = new List<PluginManifestSettingsField>(rows.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Key)
                || string.IsNullOrWhiteSpace(row.Type)
                || string.IsNullOrWhiteSpace(row.Title))
            {
                continue;
            }

            list.Add(new PluginManifestSettingsField
            {
                Key = row.Key,
                Type = row.Type,
                Title = row.Title,
                Default = row.Default ?? "",
                Choices = row.Choices ?? [],
            });
        }

        return list;
    }
}
