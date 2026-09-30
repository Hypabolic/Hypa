namespace Hypa.AgentRuntime.Domain.AttachConfig;

/// <summary>Expected attach-config failure. Not <c>RuntimeResult</c> (that type is persistence).</summary>
public sealed record AttachConfigError(string Code, string Message, string? Key, int? Line)
{
    public const string InvalidToml = "attach_config.invalid_toml";
    public const string UnknownKey = "attach_config.unknown_key";
    public const string UnknownSection = "attach_config.unknown_section";
    public const string WrongType = "attach_config.wrong_type";
    public const string InvalidValue = "attach_config.invalid_value";
    public const string MissingFile = "attach_config.missing_file";
    public const string IoError = "attach_config.io";
    public const string UnsafeRewrite = "attach_config.unsafe_rewrite";
    public const string SidebarSectionUnknownId = "attach_config.sidebar_section_unknown_id";
    public const string SidebarSectionDuplicateId = "attach_config.sidebar_section_duplicate_id";
    public const string SidebarSectionResourceUnsupported = "attach_config.sidebar_section_resource_unsupported";
    public const string SidebarSectionResourceInvalid = "attach_config.sidebar_section_resource_invalid";
    public const string SidebarSectionBuiltInResource = "attach_config.sidebar_section_builtin_resource";
    public const string SidebarSectionPluginRequiresResource = "attach_config.sidebar_section_plugin_requires_resource";

    public static AttachConfigError Toml(string message, int? line = null) =>
        new(InvalidToml, message, Key: null, line);

    public static AttachConfigError Unknown(string key, int? line) =>
        new(UnknownKey, $"Unknown config key '{key}'.", key, line);

    public static AttachConfigError Section(string key, int? line) =>
        new(UnknownSection, $"Unknown config section '{key}'.", key, line);

    public static AttachConfigError Type(string key, string expected, int? line) =>
        new(WrongType, $"Config key '{key}' must be {expected}.", key, line);

    public static AttachConfigError Value(string key, string message, int? line) =>
        new(InvalidValue, message, key, line);

    public static AttachConfigError Missing(string path) =>
        new(MissingFile, $"Attach config file not found: {path}", Key: "HYPA_CONFIG_PATH", Line: null);

    public override string ToString()
    {
        var loc = Line is int line ? $":{line}" : "";
        var key = string.IsNullOrEmpty(Key) ? "" : $" [{Key}]";
        return $"config{loc}{key}: {Message}";
    }
}
