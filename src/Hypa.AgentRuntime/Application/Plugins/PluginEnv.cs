namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
// / Hypa-native plugin environment.
// / and <c>env.rs:15-29</c>, with HYPA names.
/// </summary>
public static class PluginEnv
{
    public const string BinPath = "HYPA_BIN_PATH";
    public const string PluginId = "HYPA_PLUGIN_ID";
    public const string PluginRoot = "HYPA_PLUGIN_ROOT";
    public const string ConfigDir = "HYPA_PLUGIN_CONFIG_DIR";
    public const string StateDir = "HYPA_PLUGIN_STATE_DIR";
    public const string GrantToken = "HYPA_PLUGIN_GRANT_TOKEN";
    public const string ContextJson = "HYPA_PLUGIN_CONTEXT_JSON";
    public const string ActionId = "HYPA_PLUGIN_ACTION_ID";
    public const string Event = "HYPA_PLUGIN_EVENT";
    public const string EventJson = "HYPA_PLUGIN_EVENT_JSON";
    public const string EntrypointId = "HYPA_PLUGIN_ENTRYPOINT_ID";
    public const string RuntimeSocket = CustomCommandEnvironment.RuntimeSocket;
    public const string WorkspaceId = "HYPA_WORKSPACE_ID";
    public const string TabId = "HYPA_TAB_ID";
    public const string PaneId = "HYPA_PANE_ID";
    public const string ClickedUrl = "HYPA_PLUGIN_CLICKED_URL";
    public const string LinkHandlerId = "HYPA_PLUGIN_LINK_HANDLER_ID";

    public static readonly IReadOnlySet<string> ProtectedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        RuntimeSocket,
        PluginId,
        PluginRoot,
        ConfigDir,
        StateDir,
        GrantToken,
        EntrypointId,
        ContextJson,
        BinPath,
    };

    public static Dictionary<string, string> Build(
        InstalledPlugin plugin,
        string configDir,
        string stateDir,
        string contextJson,
        string? grantToken,
        string? binPath,
        string? socketPath,
        PluginInvocationContext context,
        string? actionId,
        string? eventName,
        string? eventJson,
        string? entrypointId)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PluginId] = plugin.PluginId,
            [PluginRoot] = plugin.PluginRoot,
            [ConfigDir] = configDir,
            [StateDir] = stateDir,
            [ContextJson] = contextJson,
        };
        if (!string.IsNullOrWhiteSpace(grantToken))
            env[GrantToken] = grantToken;
        if (!string.IsNullOrWhiteSpace(binPath))
            env[BinPath] = binPath;
        if (!string.IsNullOrWhiteSpace(socketPath))
            env[RuntimeSocket] = socketPath;
        if (!string.IsNullOrWhiteSpace(actionId))
            env[ActionId] = actionId;
        if (!string.IsNullOrWhiteSpace(eventName))
            env[Event] = eventName;
        if (!string.IsNullOrWhiteSpace(eventJson))
            env[EventJson] = eventJson;
        if (!string.IsNullOrWhiteSpace(entrypointId))
            env[EntrypointId] = entrypointId;
        if (!string.IsNullOrWhiteSpace(context.WorkspaceId))
            env[WorkspaceId] = context.WorkspaceId;
        if (!string.IsNullOrWhiteSpace(context.TabId))
            env[TabId] = context.TabId;
        if (!string.IsNullOrWhiteSpace(context.FocusedPaneId))
            env[PaneId] = context.FocusedPaneId;
        if (!string.IsNullOrWhiteSpace(context.ClickedUrl))
            env[ClickedUrl] = context.ClickedUrl;
        if (!string.IsNullOrWhiteSpace(context.LinkHandlerId))
            env[LinkHandlerId] = context.LinkHandlerId;
        return env;
    }

    public static Dictionary<string, string> MergeProtected(
        IReadOnlyDictionary<string, string>? extra,
        Dictionary<string, string> owned)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (extra is not null)
        {
            foreach (var kv in extra)
            {
                if (ProtectedKeys.Contains(kv.Key))
                    continue;
                merged[kv.Key] = kv.Value;
            }
        }

        foreach (var kv in owned)
            merged[kv.Key] = kv.Value;
        return merged;
    }
}
