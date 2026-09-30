namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Child-process pane identity. Popups must not export these keys.
/// </summary>
public static class PaneIdEnvironment
{
    public const string HypaPaneId = "HYPA_PANE_ID";
    public const string HypaTabId = "HYPA_TAB_ID";
    public const string HypaWorkspaceId = "HYPA_WORKSPACE_ID";
    public const string HerdrPaneId = "HERDR_PANE_ID";
    public const string HypaPaneToken = "HYPA_PANE_TOKEN";

    public static bool IsPaneIdKey(string key) =>
        string.Equals(key, HypaPaneId, StringComparison.Ordinal)
        || string.Equals(key, HerdrPaneId, StringComparison.Ordinal);

    public static bool IsSecretKey(string key) =>
        IsPaneIdKey(key)
        || string.Equals(key, HypaPaneToken, StringComparison.Ordinal);

    public static bool IsManagedIdentityKey(string key) =>
        IsPaneIdKey(key)
        || string.Equals(key, HypaTabId, StringComparison.Ordinal)
        || string.Equals(key, HypaWorkspaceId, StringComparison.Ordinal);

    /// <summary>
    /// Returns a new map without pane-id keys. Null source becomes empty (not inherit).
    /// </summary>
    public static Dictionary<string, string> Strip(IReadOnlyDictionary<string, string>? source)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is null)
            return env;

        foreach (var (key, value) in source)
        {
            if (IsSecretKey(key) || IsManagedIdentityKey(key))
                continue;
            env[key] = value;
        }

        return env;
    }
}
