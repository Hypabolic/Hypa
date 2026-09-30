namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>Caps for the server-owned plugin resource store.</summary>
public static class PluginResourceLimits
{
    public const string CollectionSchema = "hypa.projection.collection.v1";

    public const string KindCollection = "collection";

    public const string FreshnessReady = "ready";

    public const string FreshnessStale = "stale";

    public const string FreshnessUnavailable = "unavailable";

    public const int MaxItemsPerCollection = 200;

    public const int MaxResourcesPerPlugin = 8;

    public const int MaxResourcesPerServer = 64;

    public const int MaxPublishBytes = 64 * 1024;

    public static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan ChangedCoalesce = TimeSpan.FromMilliseconds(16);

    public static readonly HashSet<string> ItemStatuses = new(StringComparer.Ordinal)
    {
        "working",
        "blocked",
        "idle",
        "done",
        "unknown",
    };
}
