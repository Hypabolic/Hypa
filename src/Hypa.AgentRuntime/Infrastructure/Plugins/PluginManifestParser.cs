using System.Text.RegularExpressions;
using Hypa.AgentRuntime.Application.Metadata;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain.Plugins;
using static Hypa.AgentRuntime.Domain.Plugins.PluginMenuContextError;
using Hypa.AgentRuntime.Infrastructure.Config;
using static Hypa.AgentRuntime.Infrastructure.Config.TomlSubsetParser;

namespace Hypa.AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// Unknown hooks and filter keys fail closed.
/// </summary>
public sealed class PluginManifestParser : IPluginManifestParser
{
    private static readonly HashSet<string> SupportedArrayTables = new(StringComparer.Ordinal)
    {
        "actions",
        "build",
        "events",
        "link_handlers",
        "menu_items",
        "panes",
        "resources",
        "settings.field",
        "startup",
        "doctor",
    };

    private static readonly HashSet<string> SupportedRegularTables = new(StringComparer.Ordinal)
    {
        "events.filter",
        "grants",
    };

    public PluginResult<PluginManifest> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var parsed = TomlSubsetParser.Parse(content);
        if (!parsed.IsOk)
        {
            var message = parsed.Errors.Count > 0 ? parsed.Errors[0].Message : "plugin manifest parse failed";
            return PluginResult<PluginManifest>.Fail(PluginError.ManifestParseFailed, message);
        }

        return Bind(parsed.Value!);
    }

    internal static PluginResult<PluginManifest> Bind(TomlDocument document)
    {
        foreach (var table in document.ArrayTables)
        {
            if (!SupportedArrayTables.Contains(table.Path))
            {
                return PluginResult<PluginManifest>.Fail(
                    PluginError.UnsupportedTable,
                    "unsupported_manifest_table: " + table.Path);
            }
        }

        foreach (var header in document.TableHeaders.Keys)
        {
            if (SupportedRegularTables.Contains(header))
                continue;
            if (SupportedArrayTables.Contains(header)
                && document.ArrayTables.Any(table => table.Path == header))
            {
                continue;
            }

            return PluginResult<PluginManifest>.Fail(
                PluginError.UnsupportedTable,
                "unsupported_manifest_table: " + header);
        }

        string? id = null;
        string? name = null;
        string? version = null;
        string? minVersion = null;
        string? description = null;
        IReadOnlyList<string>? platforms = null;
        IReadOnlyList<string>? grants = null;
        PluginEventFilter? defaultFilter = null;

        foreach (var assignment in document.Assignments)
        {
            switch (assignment.Path)
            {
                case "id":
                    if (!TryString(assignment, out id, out var idErr))
                        return idErr;
                    break;
                case "name":
                    if (!TryString(assignment, out name, out var nameErr))
                        return nameErr;
                    break;
                case "version":
                    if (!TryString(assignment, out version, out var verErr))
                        return verErr;
                    break;
                case "min_hypa_version":
                    if (!TryString(assignment, out minVersion, out var minErr))
                        return minErr;
                    break;
                case "description":
                    if (!TryString(assignment, out description, out var descErr))
                        return descErr;
                    break;
                case "platforms":
                    if (!TryStringList(assignment, out platforms, out var platErr))
                        return platErr;
                    if (platforms is { Count: 0 })
                    {
                        return PluginResult<PluginManifest>.Fail(
                            PluginError.InvalidPlatform,
                            "platforms must not be an empty array; omit the field to leave platforms undeclared");
                    }

                    break;
                case "grants.request":
                    if (!TryStringList(assignment, out grants, out var grantErr))
                        return grantErr;
                    break;
                default:
                    if (assignment.Path.StartsWith("events.filter.", StringComparison.Ordinal))
                    {
                        var key = assignment.Path["events.filter.".Length..];
                        var filterResult = MergeFilter(defaultFilter, key, assignment);
                        if (!filterResult.IsOk)
                            return PluginResult<PluginManifest>.Fail(filterResult.Error);
                        defaultFilter = filterResult.Value;
                        break;
                    }

                    break;
            }
        }

        var pluginId = PluginIdentifiers.NormalizePluginId(id);
        if (pluginId is null)
            return PluginResult<PluginManifest>.Fail(PluginError.InvalidId, "invalid plugin id");
        if (string.IsNullOrWhiteSpace(name))
            return PluginResult<PluginManifest>.Fail(PluginError.InvalidName, "plugin name is required");
        if (string.IsNullOrWhiteSpace(version))
            return PluginResult<PluginManifest>.Fail(PluginError.InvalidVersion, "plugin version is required");
        if (string.IsNullOrWhiteSpace(minVersion))
        {
            return PluginResult<PluginManifest>.Fail(
                PluginError.InvalidMinVersion,
                "plugin min_hypa_version is required");
        }

        if (platforms is not null)
        {
            var platCheck = NormalizePlatforms(platforms);
            if (!platCheck.IsOk)
                return PluginResult<PluginManifest>.Fail(platCheck.Error);
            platforms = platCheck.Value;
        }

        var requested = new List<string>();
        if (grants is not null)
        {
            foreach (var grant in grants)
            {
                var trimmed = grant.Trim();
                if (trimmed.Length == 0)
                    continue;
                if (!PluginGrantCatalog.IsKnown(trimmed))
                {
                    return PluginResult<PluginManifest>.Fail(
                        PluginError.UnknownGrant,
                        "unknown grant '" + trimmed + "'");
                }

                requested.Add(trimmed);
            }
        }

        var build = BindCommands(document, "build", out var buildErr);
        if (buildErr is not null)
            return PluginResult<PluginManifest>.Fail(buildErr);
        var startup = BindCommands(document, "startup", out var startErr);
        if (startErr is not null)
            return PluginResult<PluginManifest>.Fail(startErr);
        var actions = BindActions(document, out var actionErr);
        if (actionErr is not null)
            return PluginResult<PluginManifest>.Fail(actionErr);
        var events = BindEvents(document, defaultFilter, out var eventErr);
        if (eventErr is not null)
            return PluginResult<PluginManifest>.Fail(eventErr);
        var panes = BindPanes(document, out var paneErr);
        if (paneErr is not null)
            return PluginResult<PluginManifest>.Fail(paneErr);
        var linkHandlers = BindLinkHandlers(document, actions, out var linkErr);
        if (linkErr is not null)
            return PluginResult<PluginManifest>.Fail(linkErr);
        var resources = BindResources(document, out var resourceErr);
        if (resourceErr is not null)
            return PluginResult<PluginManifest>.Fail(resourceErr);
        var menuItems = BindMenuItems(document, actions, out var menuErr);
        if (menuErr is not null)
            return PluginResult<PluginManifest>.Fail(menuErr);
        var settingsFields = BindSettingsFields(document, out var settingsErr);
        if (settingsErr is not null)
            return PluginResult<PluginManifest>.Fail(settingsErr);
        var doctor = BindDoctor(document, out var doctorErr);
        if (doctorErr is not null)
            return PluginResult<PluginManifest>.Fail(doctorErr);

        var warnings = new List<string>();
        if (platforms is null)
            warnings.Add("manifest does not declare platforms; platform support unknown");

        return PluginResult<PluginManifest>.Ok(new PluginManifest
        {
            Id = pluginId,
            Name = name!.Trim(),
            Version = version!.Trim(),
            MinHypaVersion = minVersion!.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Platforms = platforms,
            Build = build,
            Startup = startup,
            Actions = actions,
            Events = events,
            Panes = panes,
            LinkHandlers = linkHandlers,
            Resources = resources,
            MenuItems = menuItems,
            SettingsFields = settingsFields,
            Doctor = doctor,
            RequestedGrants = requested,
            Warnings = warnings,
        });
    }

    private static IReadOnlyList<PluginCommandSpec> BindCommands(
        TomlDocument document,
        string table,
        out PluginError? error)
    {
        error = null;
        var list = new List<PluginCommandSpec>();
        foreach (var row in document.ArrayTables.Where(t => t.Path == table))
        {
            IReadOnlyList<string>? platforms = null;
            IReadOnlyList<string>? command = null;
            foreach (var field in row.Fields)
            {
                var local = LocalKey(field.Path, table);
                if (local == "platforms")
                {
                    if (!TryStringList(field, out platforms, out var fail))
                    {
                        error = fail.Error;
                        return [];
                    }
                }
                else if (local == "command")
                {
                    if (!TryStringList(field, out command, out var fail))
                    {
                        error = fail.Error;
                        return [];
                    }
                }
            }

            var cmd = NormalizeCommand(command);
            if (!cmd.IsOk)
            {
                error = cmd.Error;
                return [];
            }

            var plat = NormalizePlatforms(platforms);
            if (!plat.IsOk)
            {
                error = plat.Error;
                return [];
            }

            list.Add(new PluginCommandSpec { Platforms = plat.Value, Command = cmd.Value });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestDoctor> BindDoctor(
        TomlDocument document,
        out PluginError? error)
    {
        error = null;
        var list = new List<PluginManifestDoctor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in document.ArrayTables.Where(t => t.Path == "doctor"))
        {
            string? id = null;
            string? label = null;
            IReadOnlyList<string>? command = null;
            foreach (var field in row.Fields)
            {
                var local = LocalKey(field.Path, "doctor");
                switch (local)
                {
                    case "id":
                        if (!TryString(field, out id, out var idFail))
                        {
                            error = idFail.Error;
                            return [];
                        }

                        break;
                    case "label":
                        if (!TryString(field, out label, out var labelFail))
                        {
                            error = labelFail.Error;
                            return [];
                        }

                        break;
                    case "command":
                        if (!TryStringList(field, out command, out var cmdFail))
                        {
                            error = cmdFail.Error;
                            return [];
                        }

                        break;
                }
            }

            var doctorId = PluginIdentifiers.NormalizeLocalId(id);
            if (doctorId is null)
            {
                error = new PluginError(PluginError.InvalidDoctorId, "invalid doctor id");
                return [];
            }

            if (!seen.Add(doctorId))
            {
                error = new PluginError(
                    PluginError.DuplicateDoctor,
                    "duplicate doctor id '" + doctorId + "'");
                return [];
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                error = new PluginError(PluginError.InvalidName, "doctor label is required");
                return [];
            }

            var cmd = NormalizeCommand(command);
            if (!cmd.IsOk)
            {
                error = cmd.Error;
                return [];
            }

            list.Add(new PluginManifestDoctor
            {
                Id = doctorId,
                Label = label!.Trim(),
                Command = cmd.Value,
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestAction> BindActions(TomlDocument document, out PluginError? error)
    {
        error = null;
        var list = new List<PluginManifestAction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in document.ArrayTables.Where(t => t.Path == "actions"))
        {
            string? id = null;
            string? title = null;
            string? description = null;
            IReadOnlyList<string>? contexts = null;
            IReadOnlyList<string>? platforms = null;
            IReadOnlyList<string>? command = null;
            bool palette = false;
            string? suggestedKey = null;
            foreach (var field in row.Fields)
            {
                var local = LocalKey(field.Path, "actions");
                switch (local)
                {
                    case "id":
                        if (!TryString(field, out id, out var idFail))
                        {
                            error = idFail.Error;
                            return [];
                        }

                        break;
                    case "title":
                        if (!TryString(field, out title, out var titleFail))
                        {
                            error = titleFail.Error;
                            return [];
                        }

                        break;
                    case "description":
                        if (!TryString(field, out description, out var descFail))
                        {
                            error = descFail.Error;
                            return [];
                        }

                        break;
                    case "contexts":
                        if (!TryStringList(field, out contexts, out var ctxFail))
                        {
                            error = ctxFail.Error;
                            return [];
                        }

                        break;
                    case "platforms":
                        if (!TryStringList(field, out platforms, out var platFail))
                        {
                            error = platFail.Error;
                            return [];
                        }

                        break;
                    case "command":
                        if (!TryStringList(field, out command, out var cmdFail))
                        {
                            error = cmdFail.Error;
                            return [];
                        }

                        break;
                    case "palette":
                        if (field.Value is TomlBoolValue paletteValue)
                            palette = paletteValue.Value;
                        else
                        {
                            error = new PluginError(
                                PluginError.ManifestParseFailed,
                                "actions.palette must be a boolean");
                            return [];
                        }

                        break;
                    case "key":
                        if (!TryString(field, out suggestedKey, out var keyFail))
                        {
                            error = keyFail.Error;
                            return [];
                        }

                        break;
                }
            }

            var actionId = PluginIdentifiers.NormalizeLocalId(id);
            if (actionId is null)
            {
                error = new PluginError(PluginError.InvalidActionId, "invalid action id");
                return [];
            }

            if (!seen.Add(actionId))
            {
                error = new PluginError(PluginError.DuplicateAction, "duplicate action id '" + actionId + "'");
                return [];
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                error = new PluginError(PluginError.InvalidName, "action title is required");
                return [];
            }

            var cmd = NormalizeCommand(command);
            if (!cmd.IsOk)
            {
                error = cmd.Error;
                return [];
            }

            var plat = NormalizePlatforms(platforms);
            if (!plat.IsOk)
            {
                error = plat.Error;
                return [];
            }

            if (!PluginMenuContexts.TryNormalize(
                    contexts,
                    out var normalizedContexts,
                    out var contextCode,
                    out var contextMessage))
            {
                error = new PluginError(contextCode!, contextMessage!);
                return [];
            }

            var trimmedKey = string.IsNullOrWhiteSpace(suggestedKey) ? null : suggestedKey.Trim();
            if (PluginReservedKeybinds.CollidesWithCoreDefault(trimmedKey))
            {
                error = new PluginError(
                    PluginError.SuggestedKeyCoreCollision,
                    "action '" + actionId + "' suggested key collides with a core bind");
                return [];
            }

            list.Add(new PluginManifestAction
            {
                Id = actionId,
                Title = title!.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Contexts = normalizedContexts,
                Platforms = plat.Value,
                Command = cmd.Value,
                Palette = palette,
                SuggestedKey = trimmedKey,
            });
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    private static IReadOnlyList<PluginManifestMenuItem> BindMenuItems(
        TomlDocument document,
        IReadOnlyList<PluginManifestAction> actions,
        out PluginError? error)
    {
        error = null;
        var list = new List<PluginManifestMenuItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var actionIds = new HashSet<string>(actions.Select(a => a.Id), StringComparer.Ordinal);
        foreach (var row in document.ArrayTables.Where(t => t.Path == "menu_items"))
        {
            string? id = null;
            string? title = null;
            IReadOnlyList<string>? contexts = null;
            string? action = null;
            foreach (var field in row.Fields)
            {
                var local = LocalKey(field.Path, "menu_items");
                switch (local)
                {
                    case "id":
                        if (!TryString(field, out id, out var idFail))
                        {
                            error = idFail.Error;
                            return [];
                        }

                        break;
                    case "title":
                        if (!TryString(field, out title, out var titleFail))
                        {
                            error = titleFail.Error;
                            return [];
                        }

                        break;
                    case "contexts":
                        if (!TryStringList(field, out contexts, out var ctxFail))
                        {
                            error = ctxFail.Error;
                            return [];
                        }

                        break;
                    case "action":
                        if (!TryString(field, out action, out var actionFail))
                        {
                            error = actionFail.Error;
                            return [];
                        }

                        break;
                }
            }

            var itemId = PluginIdentifiers.NormalizeLocalId(id);
            if (itemId is null)
            {
                error = new PluginError(PluginError.InvalidMenuItemId, "invalid menu item id");
                return [];
            }

            if (!seen.Add(itemId))
            {
                error = new PluginError(
                    PluginError.DuplicateMenuItem,
                    "duplicate menu item id '" + itemId + "'");
                return [];
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                error = new PluginError(PluginError.InvalidName, "menu item title is required");
                return [];
            }

            if (!PluginMenuContexts.TryNormalize(
                    contexts,
                    out var normalizedContexts,
                    out var contextCode,
                    out var contextMessage))
            {
                error = new PluginError(contextCode!, contextMessage!);
                return [];
            }

            if (normalizedContexts.Count == 0)
            {
                error = new PluginError(
                    PluginError.InvalidParams,
                    "menu item '" + itemId + "' requires at least one context");
                return [];
            }

            var actionId = PluginIdentifiers.NormalizeLocalId(action);
            if (actionId is null || !actionIds.Contains(actionId))
            {
                error = new PluginError(
                    PluginError.InvalidMenuItemAction,
                    "menu item '" + itemId + "' references unknown action '" + (action ?? "") + "'");
                return [];
            }

            list.Add(new PluginManifestMenuItem
            {
                Id = itemId,
                Title = title!.Trim(),
                Contexts = normalizedContexts,
                Action = actionId,
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestEventHook> BindEvents(
        TomlDocument document,
        PluginEventFilter? defaultFilter,
        out PluginError? error)
    {
        error = null;
        var list = new List<PluginManifestEventHook>();
        foreach (var row in document.ArrayTables.Where(t => t.Path == "events"))
        {
            string? on = null;
            IReadOnlyList<string>? platforms = null;
            IReadOnlyList<string>? command = null;
            PluginEventFilter? filter = defaultFilter;
            foreach (var field in row.Fields)
            {
                var local = LocalKey(field.Path, "events");
                if (local == "on")
                {
                    if (!TryString(field, out on, out var onFail))
                    {
                        error = onFail.Error;
                        return [];
                    }
                }
                else if (local == "platforms")
                {
                    if (!TryStringList(field, out platforms, out var platFail))
                    {
                        error = platFail.Error;
                        return [];
                    }
                }
                else if (local == "command")
                {
                    if (!TryStringList(field, out command, out var cmdFail))
                    {
                        error = cmdFail.Error;
                        return [];
                    }
                }
                else if (local.StartsWith("filter.", StringComparison.Ordinal))
                {
                    var key = local["filter.".Length..];
                    var merged = MergeFilter(filter, key, field);
                    if (!merged.IsOk)
                    {
                        error = merged.Error;
                        return [];
                    }

                    filter = merged.Value;
                }
            }

            if (string.IsNullOrWhiteSpace(on))
            {
                error = new PluginError(PluginError.InvalidEvent, "event name is required");
                return [];
            }

            var hook = on.Trim();
            if (PluginHookCatalog.IsForbiddenOutputHook(hook) || !PluginHookCatalog.IsAllowed(hook))
            {
                error = new PluginError(PluginError.UnknownEvent, "unknown event '" + hook + "'");
                return [];
            }

            var cmd = NormalizeCommand(command);
            if (!cmd.IsOk)
            {
                error = cmd.Error;
                return [];
            }

            var plat = NormalizePlatforms(platforms);
            if (!plat.IsOk)
            {
                error = plat.Error;
                return [];
            }

            list.Add(new PluginManifestEventHook
            {
                On = hook,
                Platforms = plat.Value,
                Command = cmd.Value,
                Filter = filter,
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestPane> BindPanes(TomlDocument document, out PluginError? error)
    {
        error = null;
        var list = new List<PluginManifestPane>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in document.ArrayTables.Where(t => t.Path == "panes"))
        {
            string? id = null;
            string? title = null;
            string? description = null;
            IReadOnlyList<string>? platforms = null;
            string placement = "overlay";
            string? width = null;
            string? height = null;
            IReadOnlyList<string>? command = null;
            foreach (var field in row.Fields)
            {
                var local = LocalKey(field.Path, "panes");
                switch (local)
                {
                    case "id":
                        if (!TryString(field, out id, out var idFail))
                        {
                            error = idFail.Error;
                            return [];
                        }

                        break;
                    case "title":
                        if (!TryString(field, out title, out var titleFail))
                        {
                            error = titleFail.Error;
                            return [];
                        }

                        break;
                    case "description":
                        if (!TryString(field, out description, out var descFail))
                        {
                            error = descFail.Error;
                            return [];
                        }

                        break;
                    case "platforms":
                        if (!TryStringList(field, out platforms, out var platFail))
                        {
                            error = platFail.Error;
                            return [];
                        }

                        break;
                    case "placement":
                        if (!TryString(field, out var rawPlacement, out var placeFail))
                        {
                            error = placeFail.Error;
                            return [];
                        }

                        placement = rawPlacement!.Trim();
                        break;
                    case "width":
                        width = SizeLiteral(field);
                        break;
                    case "height":
                        height = SizeLiteral(field);
                        break;
                    case "command":
                        if (!TryStringList(field, out command, out var cmdFail))
                        {
                            error = cmdFail.Error;
                            return [];
                        }

                        break;
                }
            }

            var paneId = PluginIdentifiers.NormalizeLocalId(id);
            if (paneId is null)
            {
                error = new PluginError(PluginError.InvalidPaneId, "invalid pane id");
                return [];
            }

            if (!seen.Add(paneId))
            {
                error = new PluginError(PluginError.DuplicatePane, "duplicate pane id '" + paneId + "'");
                return [];
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                error = new PluginError(PluginError.InvalidName, "pane title is required");
                return [];
            }

            if (placement is not ("overlay" or "popup" or "split" or "tab" or "zoomed"))
            {
                error = new PluginError(PluginError.InvalidParams, "invalid plugin pane placement");
                return [];
            }

            var cmd = NormalizeCommand(command);
            if (!cmd.IsOk)
            {
                error = cmd.Error;
                return [];
            }

            var plat = NormalizePlatforms(platforms);
            if (!plat.IsOk)
            {
                error = plat.Error;
                return [];
            }

            if (placement != "popup" && (width is not null || height is not null))
            {
                error = new PluginError(
                    PluginError.InvalidPaneSize,
                    "pane width and height are only supported when placement is popup");
                return [];
            }

            list.Add(new PluginManifestPane
            {
                Id = paneId,
                Title = title!.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Platforms = plat.Value,
                Placement = placement,
                Width = width,
                Height = height,
                Command = cmd.Value,
            });
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    /// <summary>
    /// Keep manifest order. Fail closed on duplicate id, missing action,
    /// invalid regular expression, or invalid id.
    /// </summary>
    private static IReadOnlyList<PluginManifestLinkHandler> BindLinkHandlers(
        TomlDocument document,
        IReadOnlyList<PluginManifestAction> actions,
        out PluginError? error)
    {
        error = null;
        var list = new List<PluginManifestLinkHandler>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var actionIds = new HashSet<string>(actions.Select(a => a.Id), StringComparer.Ordinal);
        foreach (var row in document.ArrayTables.Where(t => t.Path == "link_handlers"))
        {
            string? id = null;
            string? title = null;
            string? pattern = null;
            string? action = null;
            IReadOnlyList<string>? platforms = null;
            foreach (var field in row.Fields)
            {
                var local = LocalKey(field.Path, "link_handlers");
                switch (local)
                {
                    case "id":
                        if (!TryString(field, out id, out var idFail))
                        {
                            error = idFail.Error;
                            return [];
                        }

                        break;
                    case "title":
                        if (!TryString(field, out title, out var titleFail))
                        {
                            error = titleFail.Error;
                            return [];
                        }

                        break;
                    case "pattern":
                        if (!TryString(field, out pattern, out var patternFail))
                        {
                            error = patternFail.Error;
                            return [];
                        }

                        break;
                    case "action":
                        if (!TryString(field, out action, out var actionFail))
                        {
                            error = actionFail.Error;
                            return [];
                        }

                        break;
                    case "platforms":
                        if (!TryStringList(field, out platforms, out var platFail))
                        {
                            error = platFail.Error;
                            return [];
                        }

                        break;
                }
            }

            var handlerId = PluginIdentifiers.NormalizeLocalId(id);
            if (handlerId is null)
            {
                error = new PluginError(PluginError.InvalidLinkHandlerId, "invalid link handler id");
                return [];
            }

            if (!seen.Add(handlerId))
            {
                error = new PluginError(
                    PluginError.DuplicateLinkHandler,
                    "duplicate link handler id '" + handlerId + "'");
                return [];
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                error = new PluginError(PluginError.InvalidLinkHandlerTitle, "link handler title is required");
                return [];
            }

            if (string.IsNullOrWhiteSpace(pattern))
            {
                error = new PluginError(
                    PluginError.InvalidLinkHandlerPattern,
                    "link handler pattern is required");
                return [];
            }

            var trimmedPattern = pattern.Trim();
            try
            {
                _ = new Regex(trimmedPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                error = new PluginError(PluginError.InvalidLinkHandlerPattern, ex.Message);
                return [];
            }

            var actionId = PluginIdentifiers.NormalizeLocalId(action);
            if (actionId is null)
            {
                error = new PluginError(
                    PluginError.InvalidLinkHandlerAction,
                    "invalid link handler action");
                return [];
            }

            if (!actionIds.Contains(actionId))
            {
                error = new PluginError(
                    PluginError.InvalidLinkHandlerAction,
                    "link handler '" + handlerId + "' references unknown action '" + actionId + "'");
                return [];
            }

            var plat = NormalizePlatforms(platforms);
            if (!plat.IsOk)
            {
                error = plat.Error;
                return [];
            }

            list.Add(new PluginManifestLinkHandler
            {
                Id = handlerId,
                Title = title!.Trim(),
                Pattern = trimmedPattern,
                Action = actionId,
                Platforms = plat.Value,
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestResource> BindResources(
        TomlDocument document,
        out PluginError? error)
    {
        error = null;
        var list = new List<PluginManifestResource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in document.ArrayTables.Where(t => t.Path == "resources"))
        {
            string? id = null;
            string? kind = null;
            string? projection = null;
            string? title = null;
            IReadOnlyList<string>? platforms = null;
            IReadOnlyList<string>? command = null;
            foreach (var field in row.Fields)
            {
                var local = LocalKey(field.Path, "resources");
                switch (local)
                {
                    case "id":
                        if (!TryString(field, out id, out var idFail))
                        {
                            error = idFail.Error;
                            return [];
                        }

                        break;
                    case "kind":
                        if (!TryString(field, out kind, out var kindFail))
                        {
                            error = kindFail.Error;
                            return [];
                        }

                        break;
                    case "projection":
                        if (!TryString(field, out projection, out var projFail))
                        {
                            error = projFail.Error;
                            return [];
                        }

                        break;
                    case "title":
                        if (!TryString(field, out title, out var titleFail))
                        {
                            error = titleFail.Error;
                            return [];
                        }

                        break;
                    case "platforms":
                        if (!TryStringList(field, out platforms, out var platFail))
                        {
                            error = platFail.Error;
                            return [];
                        }

                        break;
                    case "command":
                        if (!TryStringList(field, out command, out var cmdFail))
                        {
                            error = cmdFail.Error;
                            return [];
                        }

                        break;
                }
            }

            var resourceId = PluginIdentifiers.NormalizeLocalId(id);
            if (resourceId is null)
            {
                error = new PluginError(PluginError.InvalidResourceId, "invalid plugin resource id");
                return [];
            }

            if (!seen.Add(resourceId))
            {
                error = new PluginError(
                    PluginError.DuplicateResource,
                    "duplicate resource id '" + resourceId + "'");
                return [];
            }

            var kindValue = (kind ?? "").Trim();
            if (!string.Equals(kindValue, PluginResourceLimits.KindCollection, StringComparison.Ordinal))
            {
                error = new PluginError(
                    PluginError.InvalidResourceKind,
                    "plugin resource kind must be collection");
                return [];
            }

            var projectionValue = (projection ?? "").Trim();
            if (!string.Equals(projectionValue, PluginResourceLimits.CollectionSchema, StringComparison.Ordinal))
            {
                error = new PluginError(
                    PluginError.UnknownProjection,
                    "unknown projection version");
                return [];
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                error = new PluginError(PluginError.InvalidName, "resource title is required");
                return [];
            }

            IReadOnlyList<string>? refresh = null;
            if (command is not null)
            {
                var cmd = NormalizeCommand(command);
                if (!cmd.IsOk)
                {
                    error = cmd.Error;
                    return [];
                }

                refresh = cmd.Value;
            }

            var plat = NormalizePlatforms(platforms);
            if (!plat.IsOk)
            {
                error = plat.Error;
                return [];
            }

            list.Add(new PluginManifestResource
            {
                Id = resourceId,
                Kind = kindValue,
                Projection = projectionValue,
                Title = MetadataTokenNormalizer.NormalizeValue(title),
                Platforms = plat.Value,
                Command = refresh,
            });
        }

        return list;
    }

    private static IReadOnlyList<PluginManifestSettingsField> BindSettingsFields(
        TomlDocument document,
        out PluginError? error)
    {
        error = null;
        var list = new List<PluginManifestSettingsField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in document.ArrayTables.Where(t => t.Path == "settings.field"))
        {
            string? key = null;
            string? type = null;
            string? title = null;
            string? defaultValue = null;
            IReadOnlyList<string>? choices = null;
            foreach (var field in row.Fields)
            {
                var local = LocalKey(field.Path, "settings.field");
                switch (local)
                {
                    case "key":
                        if (!TryString(field, out key, out var keyFail))
                        {
                            error = keyFail.Error;
                            return [];
                        }

                        break;
                    case "type":
                        if (!TryString(field, out type, out var typeFail))
                        {
                            error = typeFail.Error;
                            return [];
                        }

                        break;
                    case "title":
                        if (!TryString(field, out title, out var titleFail))
                        {
                            error = titleFail.Error;
                            return [];
                        }

                        break;
                    case "default":
                        if (!TryString(field, out defaultValue, out var defaultFail))
                        {
                            error = defaultFail.Error;
                            return [];
                        }

                        break;
                    case "choices":
                        if (!TryStringList(field, out choices, out var choicesFail))
                        {
                            error = choicesFail.Error;
                            return [];
                        }

                        break;
                    case "widget":
                    case "color":
                    case "colour":
                    case "layout":
                    case "width":
                    case "style":
                        error = new PluginError(
                            PluginError.InvalidSettingsField,
                            "settings field cannot declare '" + local + "'");
                        return [];
                }
            }

            var normalizedKey = PluginIdentifiers.NormalizeLocalId(key);
            if (normalizedKey is null)
            {
                error = new PluginError(PluginError.InvalidSettingsField, "invalid settings field key");
                return [];
            }

            if (!seen.Add(normalizedKey))
            {
                error = new PluginError(
                    PluginError.InvalidSettingsField,
                    "duplicate settings field key '" + normalizedKey + "'");
                return [];
            }

            var typeValue = (type ?? "").Trim();
            if (!PluginSettingsFieldTypes.IsKnown(typeValue))
            {
                error = new PluginError(
                    PluginError.UnknownSettingsFieldType,
                    "unknown settings field type '" + typeValue + "'");
                return [];
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                error = new PluginError(PluginError.InvalidSettingsField, "settings field title is required");
                return [];
            }

            var defaultLiteral = defaultValue ?? "";
            if (typeValue == PluginSettingsFieldTypes.Choice)
            {
                if (choices is null || choices.Count == 0)
                {
                    error = new PluginError(
                        PluginError.InvalidSettingsField,
                        "choice settings field requires choices");
                    return [];
                }

                if (!choices.Contains(defaultLiteral, StringComparer.Ordinal))
                {
                    error = new PluginError(
                        PluginError.InvalidSettingsField,
                        "choice default must be one of the declared choices");
                    return [];
                }
            }
            else if (choices is { Count: > 0 })
            {
                error = new PluginError(
                    PluginError.InvalidSettingsField,
                    "choices are only supported for choice settings fields");
                return [];
            }

            if (!ValidateDefaultLiteral(typeValue, defaultLiteral, choices ?? [], out var defaultError))
            {
                error = defaultError;
                return [];
            }

            list.Add(new PluginManifestSettingsField
            {
                Key = normalizedKey,
                Type = typeValue,
                Title = title!.Trim(),
                Default = defaultLiteral,
                Choices = choices ?? [],
            });
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
    }

    private static bool ValidateDefaultLiteral(
        string type,
        string defaultLiteral,
        IReadOnlyList<string> choices,
        out PluginError? error)
    {
        error = null;
        switch (type)
        {
            case PluginSettingsFieldTypes.Integer:
                if (!int.TryParse(defaultLiteral, out _))
                {
                    error = new PluginError(PluginError.InvalidSettingsField, "integer default must be a number");
                    return false;
                }

                return true;
            case PluginSettingsFieldTypes.Boolean:
                if (defaultLiteral is not ("true" or "false"))
                {
                    error = new PluginError(PluginError.InvalidSettingsField, "boolean default must be true or false");
                    return false;
                }

                return true;
            case PluginSettingsFieldTypes.Choice:
                if (!choices.Contains(defaultLiteral, StringComparer.Ordinal))
                {
                    error = new PluginError(PluginError.InvalidSettingsField, "choice default must be declared");
                    return false;
                }

                return true;
            default:
                return true;
        }
    }

    private static PluginResult<PluginEventFilter> MergeFilter(
        PluginEventFilter? current,
        string key,
        TomlAssignment assignment)
    {
        if (!PluginEventFilterMatcher.AllowedKeys.Contains(key))
        {
            return PluginResult<PluginEventFilter>.Fail(
                PluginError.UnknownFilter,
                "unknown event filter key '" + key + "'");
        }

        if (!TryStringList(assignment, out var values, out var fail))
            return PluginResult<PluginEventFilter>.Fail(fail.Error);
        if (values is { Count: > PluginCommandLimits.FilterListMax })
        {
            return PluginResult<PluginEventFilter>.Fail(
                PluginError.UnknownFilter,
                "event filter list exceeds 16 entries");
        }

        current ??= new PluginEventFilter();
        return PluginResult<PluginEventFilter>.Ok(key switch
        {
            "workspace" => current with { Workspace = values },
            "pane" => current with { Pane = values },
            "status" => current with { Status = values },
            "agent" => current with { Agent = values },
            _ => current,
        });
    }

    private static PluginResult<IReadOnlyList<string>> NormalizeCommand(IReadOnlyList<string>? command)
    {
        if (command is null || command.Count == 0 || command.Any(string.IsNullOrEmpty))
        {
            return PluginResult<IReadOnlyList<string>>.Fail(
                PluginError.InvalidCommand,
                "command must contain non-empty argv strings");
        }

        if (!PluginIdentifiers.IsAllowedUnixCommand(command[0]))
        {
            return PluginResult<IReadOnlyList<string>>.Fail(
                PluginError.UnsupportedCommand,
                ".ps1 commands are refused until Windows plugin commands ship");
        }

        return PluginResult<IReadOnlyList<string>>.Ok(command);
    }

    private static PluginResult<IReadOnlyList<string>?> NormalizePlatforms(IReadOnlyList<string>? platforms)
    {
        if (platforms is null)
            return PluginResult<IReadOnlyList<string>?>.Ok(null);
        if (platforms.Count == 0)
        {
            return PluginResult<IReadOnlyList<string>?>.Fail(
                PluginError.InvalidPlatform,
                "platforms must not be an empty array; omit the field to leave platforms undeclared");
        }

        var list = new List<string>(platforms.Count);
        foreach (var raw in platforms)
        {
            var value = raw.Trim();
            if (value is not ("linux" or "macos" or "windows"))
            {
                return PluginResult<IReadOnlyList<string>?>.Fail(
                    PluginError.InvalidPlatform,
                    "unknown platform '" + value + "'");
            }

            list.Add(value);
        }

        return PluginResult<IReadOnlyList<string>?>.Ok(list);
    }

    private static bool TryString(TomlAssignment assignment, out string? value, out PluginResult<PluginManifest> fail)
    {
        fail = default!;
        if (assignment.Value is TomlStringValue s)
        {
            value = s.Value;
            return true;
        }

        value = null;
        fail = PluginResult<PluginManifest>.Fail(PluginError.ManifestParseFailed, assignment.Path + " must be a string");
        return false;
    }

    private static bool TryStringList(
        TomlAssignment assignment,
        out IReadOnlyList<string>? value,
        out PluginResult<PluginManifest> fail)
    {
        fail = default!;
        if (assignment.Value is not TomlArrayValue array)
        {
            value = null;
            fail = PluginResult<PluginManifest>.Fail(
                PluginError.ManifestParseFailed,
                assignment.Path + " must be an array of strings");
            return false;
        }

        var list = new List<string>(array.Items.Count);
        foreach (var item in array.Items)
        {
            if (item is not TomlStringValue s)
            {
                value = null;
                fail = PluginResult<PluginManifest>.Fail(
                    PluginError.ManifestParseFailed,
                    assignment.Path + " must be an array of strings");
                return false;
            }

            list.Add(s.Value);
        }

        value = list;
        return true;
    }

    private static string? SizeLiteral(TomlAssignment assignment) =>
        assignment.Value switch
        {
            TomlStringValue s => s.Value,
            TomlIntValue n => n.Value.ToString(),
            _ => null,
        };

    private static string LocalKey(string path, string table)
    {
        var prefix = table + ".";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }
}
