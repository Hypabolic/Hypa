namespace Hypa.Annotate.Application;

/// <summary>
/// Hypa-native annotate environment names. Not in <c>PluginEnv.ProtectedKeys</c>.
/// </summary>
public static class AnnotateEnv
{
    public const string PendingPath = "HYPA_ANNOTATE_PENDING";

    public const string LastReviewPath = "HYPA_ANNOTATE_LAST_REVIEW";

    // / <summary>Host injects this.
    public const string BinPath = "HYPA_BIN_PATH";

    // / <summary>Host injects this.
    public const string StateDir = "HYPA_PLUGIN_STATE_DIR";

    // / <summary>Host injects this.
    public const string PluginRoot = "HYPA_PLUGIN_ROOT";

    // / <summary>Host injects this.
    public const string PluginId = "HYPA_PLUGIN_ID";

    // / <summary>Focused pane.
    public const string PaneId = "HYPA_PANE_ID";

    // / <summary>Host injects this.
    public const string ContextJson = "HYPA_PLUGIN_CONTEXT_JSON";

    public const string AnnotatePluginId = "annotate";
}
