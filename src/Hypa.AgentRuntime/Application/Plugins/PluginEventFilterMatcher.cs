using System.Text.Json;
using Hypa.AgentRuntime.Domain.Plugins;

namespace Hypa.AgentRuntime.Application.Plugins;

public static class PluginEventFilterMatcher
{
    public static readonly IReadOnlySet<string> AllowedKeys =
        new HashSet<string>(StringComparer.Ordinal) { "workspace", "pane", "status", "agent" };

    public static bool Matches(
        PluginEventFilter? filter,
        PluginInvocationContext context,
        string payloadJson)
    {
        if (filter is null)
            return true;

        if (!ListAllows(filter.Workspace, context.WorkspaceId, payloadJson, "workspace_id"))
            return false;
        if (!ListAllows(filter.Pane, context.FocusedPaneId, payloadJson, "pane_id"))
            return false;
        if (!ListAllows(filter.Status, context.FocusedPaneStatus, payloadJson, "agent_status"))
            return false;
        if (!ListAllows(filter.Agent, context.FocusedPaneAgent, payloadJson, "agent"))
            return false;
        return true;
    }

    public static string? MapHookName(string eventType, string payloadJson)
    {
        if (PluginHookCatalog.IsForbiddenOutputHook(eventType))
            return null;

        return eventType switch
        {
            "pane.lifecycle" => MapPaneLifecycle(payloadJson),
            "workspace.lifecycle" => MapActionOrState(
                payloadJson,
                ("created", PluginHookCatalog.WorkspaceCreated),
                ("closed", PluginHookCatalog.WorkspaceClosed),
                ("focused", PluginHookCatalog.WorkspaceFocused)),
            "tab.lifecycle" => MapActionOrState(
                payloadJson,
                ("created", PluginHookCatalog.TabCreated),
                ("closed", PluginHookCatalog.TabClosed),
                ("focused", PluginHookCatalog.TabFocused)),
            "pane.agent_status_changed" => PluginHookCatalog.AgentStatusChanged,
            "occupant.lifecycle" => PluginHookCatalog.OccupantChanged,
            "worktree.created" => PluginHookCatalog.WorktreeCreated,
            "worktree.opened" => PluginHookCatalog.WorktreeOpened,
            "worktree.removed" => PluginHookCatalog.WorktreeRemoved,
            "config.reloaded" => PluginHookCatalog.ConfigReloaded,
            "popup.lifecycle" => MapPopup(payloadJson),
            "pane.placement_changed" => PluginHookCatalog.PanePlacementChanged,
            _ => null,
        };
    }

    private static string? MapPaneLifecycle(string payloadJson)
    {
        var action = Read(payloadJson, "action");
        var state = Read(payloadJson, "state");
        if (string.Equals(action, "focused", StringComparison.Ordinal)
            || string.Equals(state, "focused", StringComparison.Ordinal))
        {
            return PluginHookCatalog.PaneFocused;
        }

        if (string.Equals(state, "closed", StringComparison.Ordinal)
            || string.Equals(action, "closed", StringComparison.Ordinal))
        {
            return PluginHookCatalog.PaneClosed;
        }

        if (string.Equals(state, "exited", StringComparison.Ordinal)
            || string.Equals(action, "exited", StringComparison.Ordinal))
        {
            return PluginHookCatalog.PaneExited;
        }

        if (string.Equals(state, "created", StringComparison.Ordinal)
            || string.Equals(state, "starting", StringComparison.Ordinal)
            || string.Equals(action, "created", StringComparison.Ordinal))
        {
            return PluginHookCatalog.PaneCreated;
        }

        return null;
    }

    private static string? MapPopup(string payloadJson)
    {
        var state = Read(payloadJson, "state");
        if (string.Equals(state, "opened", StringComparison.Ordinal))
            return PluginHookCatalog.PopupOpened;
        if (string.Equals(state, "closed", StringComparison.Ordinal))
            return PluginHookCatalog.PopupClosed;
        return null;
    }

    private static string? MapActionOrState(
        string payloadJson,
        params (string Token, string Hook)[] map)
    {
        var action = Read(payloadJson, "action");
        var state = Read(payloadJson, "state");
        foreach (var (token, hook) in map)
        {
            if (string.Equals(action, token, StringComparison.Ordinal)
                || string.Equals(state, token, StringComparison.Ordinal))
            {
                return hook;
            }
        }

        return null;
    }

    private static bool ListAllows(
        IReadOnlyList<string>? list,
        string? contextValue,
        string payloadJson,
        string payloadField)
    {
        if (list is null || list.Count == 0)
            return true;
        var payloadValue = Read(payloadJson, payloadField);
        foreach (var item in list)
        {
            if (string.Equals(item, contextValue, StringComparison.Ordinal)
                || string.Equals(item, payloadValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Read(string payloadJson, string field)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (doc.RootElement.TryGetProperty(field, out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }

            if (string.Equals(field, "workspace_id", StringComparison.Ordinal)
                && doc.RootElement.TryGetProperty("workspace", out var workspace)
                && workspace.ValueKind == JsonValueKind.Object
                && workspace.TryGetProperty("workspace_id", out var nested)
                && nested.ValueKind == JsonValueKind.String)
            {
                return nested.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
