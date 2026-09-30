namespace Hypa.AgentRuntime.Domain.Plugins;

/// <summary>Allowed plugin menu and action context names for chrome discovery.</summary>
public static class PluginMenuContexts
{
    public const string GlobalMenu = "global_menu";
    public const string WorkspaceRow = "workspace_row";
    public const string Tab = "tab";
    public const string Pane = "pane";
    public const string CollectionItem = "collection_item";

    public const string AgentRow = "agent_row";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        GlobalMenu,
        WorkspaceRow,
        Tab,
        Pane,
        CollectionItem,
    };

    public static IReadOnlyCollection<string> All => Allowed;

    public static bool IsAllowed(string context) =>
        Allowed.Contains(context);

    public static bool TryNormalize(
        IReadOnlyList<string>? contexts,
        out IReadOnlyList<string> normalized,
        out string? errorCode,
        out string? message)
    {
        normalized = [];
        errorCode = null;
        message = null;
        if (contexts is null || contexts.Count == 0)
            return true;

        var list = new List<string>(contexts.Count);
        foreach (var raw in contexts)
        {
            var value = raw.Trim();
            if (value.Length == 0)
                continue;
            if (string.Equals(value, AgentRow, StringComparison.Ordinal))
            {
                errorCode = PluginMenuContextError.AgentRowForbidden;
                message = "agent_row is not a plugin menu context";
                return false;
            }

            if (!IsAllowed(value))
            {
                errorCode = PluginMenuContextError.UnknownContext;
                message = "unknown plugin menu context '" + value + "'";
                return false;
            }

            if (!list.Contains(value, StringComparer.Ordinal))
                list.Add(value);
        }

        normalized = list;
        return true;
    }
}

public static class PluginMenuContextError
{
    public const string UnknownContext = "unknown_plugin_menu_context";
    public const string AgentRowForbidden = "agent_row_menu_context_forbidden";
}
