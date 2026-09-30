namespace Hypa.AgentRuntime.Application.Plugins;

public sealed record PluginError(string Code, string Message)
{
    public const string ManifestNotFound = "plugin_manifest_not_found";
    public const string ManifestReadFailed = "plugin_manifest_read_failed";
    public const string ManifestParseFailed = "plugin_manifest_parse_failed";
    public const string InvalidId = "invalid_plugin_id";
    public const string InvalidName = "invalid_plugin_name";
    public const string InvalidVersion = "invalid_plugin_version";
    public const string InvalidMinVersion = "invalid_plugin_min_hypa_version";
    public const string RequiresNewer = "plugin_requires_newer_hypa";
    public const string InvalidCommand = "invalid_plugin_command";
    public const string UnsupportedCommand = "unsupported_plugin_command";
    public const string InvalidPlatform = "invalid_plugin_platform";
    public const string PlatformUnsupported = "platform_unsupported";
    public const string DuplicateAction = "duplicate_plugin_action_id";
    public const string DuplicatePane = "duplicate_plugin_pane_id";
    public const string DuplicateLinkHandler = "duplicate_plugin_link_handler_id";
    public const string InvalidActionId = "invalid_plugin_action_id";
    public const string InvalidPaneId = "invalid_plugin_pane_id";
    public const string InvalidLinkHandlerId = "invalid_plugin_link_handler_id";
    public const string InvalidLinkHandlerAction = "invalid_plugin_link_handler_action";
    public const string InvalidLinkHandlerPattern = "invalid_plugin_link_handler_pattern";
    public const string InvalidLinkHandlerTitle = "invalid_plugin_link_handler_title";
    public const string InvalidEvent = "invalid_plugin_event";
    public const string UnknownEvent = "unknown_plugin_event";
    public const string UnknownFilter = "unknown_plugin_event_filter";
    public const string UnknownGrant = "unknown_plugin_grant";
    public const string UnsupportedTable = "unsupported_manifest_table";
    public const string NotFound = "plugin_not_found";
    public const string Disabled = "plugin_disabled";
    public const string ActionNotFound = "plugin_action_not_found";
    public const string AmbiguousAction = "ambiguous_plugin_action";
    public const string PaneNotFound = "plugin_pane_not_found";
    public const string InvalidEntrypoint = "invalid_plugin_entrypoint";
    public const string ManifestUnavailable = "plugin_manifest_unavailable";
    public const string UserDirFailed = "plugin_user_dir_create_failed";
    public const string RegistrySaveFailed = "plugin_registry_save_failed";
    public const string RegistryLoadFailed = "plugin_registry_load_failed";
    public const string CommandLimit = "plugin_command_limit_reached";
    public const string InvalidContext = "invalid_plugin_context";
    public const string InvalidParams = "invalid_params";
    public const string SourceRefused = "plugin_source_refused";
    public const string CapabilityMissing = "capability_missing";
    public const string PaneOpenFailed = "plugin_pane_open_failed";
    public const string InvalidPaneSize = "invalid_plugin_pane_size";
    public const string SourceDenied = "source_denied";
    public const string StaleRevision = "stale_revision";
    public const string ResourceNotFound = "plugin_resource_not_found";
    public const string InvalidResourceId = "invalid_plugin_resource_id";
    public const string DuplicateResource = "duplicate_plugin_resource_id";
    public const string UnknownProjection = "unknown_projection";
    public const string ResourceFull = "plugin_resource_full";
    public const string ResourceMalformed = "plugin_resource_malformed";
    public const string InvalidResourceKind = "invalid_plugin_resource_kind";
    public const string SuggestedKeyCoreCollision = "plugin_suggested_key_core_collision";
    public const string InvalidMenuItemId = "invalid_plugin_menu_item_id";
    public const string DuplicateMenuItem = "duplicate_plugin_menu_item_id";
    public const string InvalidMenuItemAction = "invalid_plugin_menu_item_action";
    public const string UnknownSettingsFieldType = "unknown_plugin_settings_field_type";
    public const string InvalidSettingsField = "invalid_plugin_settings_field";
    public const string InvalidSettingsValue = "invalid_plugin_settings_value";
    public const string SettingsKeyUnknown = "unknown_plugin_settings_key";
    public const string SettingsMalformed = "plugin_settings_malformed";
    public const string DuplicateDoctor = "duplicate_plugin_doctor_id";
    public const string InvalidDoctorId = "invalid_plugin_doctor_id";

    public static PluginError Of(string code, string message) => new(code, message);
}

public readonly record struct PluginResult<T>
{
    private readonly T? _value;
    private readonly PluginError? _error;

    public bool IsOk { get; }

    public T Value => IsOk
        ? _value!
        : throw new InvalidOperationException("Result is not Ok.");

    public PluginError Error => !IsOk
        ? _error!
        : throw new InvalidOperationException("Result is not Fail.");

    private PluginResult(T value)
    {
        IsOk = true;
        _value = value;
        _error = null;
    }

    private PluginResult(PluginError error)
    {
        IsOk = false;
        _value = default;
        _error = error;
    }

    public static PluginResult<T> Ok(T value) => new(value);

    public static PluginResult<T> Fail(PluginError error) => new(error);

    public static PluginResult<T> Fail(string code, string message) => new(new PluginError(code, message));
}
