namespace Hypa.AgentRuntime.Domain.AttachConfig;

/// <summary>
/// One attach-client + mux-host config. Shared type. No TOML package in this layer.
/// Compression JSON stays at ~/.hypa/config.json. This object is the TOML attach file.
/// </summary>
public sealed record AttachClientConfig
{
    public bool? Onboarding { get; init; }

    /// <summary>Missing or true shows first-run. False skips.</summary>
    public bool ShouldShowOnboarding => Onboarding is not false;
    public AttachThemeConfig Theme { get; init; } = AttachThemeConfig.Default;
    public AttachTerminalConfig Terminal { get; init; } = AttachTerminalConfig.Default;
    public AttachKeysConfig Keys { get; init; } = AttachKeysConfig.Default;
    public AttachUiConfig Ui { get; init; } = AttachUiConfig.Default;
    public AttachSessionConfig Session { get; init; } = AttachSessionConfig.Default;
    public AttachAdvancedConfig Advanced { get; init; } = AttachAdvancedConfig.Default;
    public AttachExperimentalConfig Experimental { get; init; } = AttachExperimentalConfig.Default;
    public AttachWorktreesConfig Worktrees { get; init; } = AttachWorktreesConfig.Default;
    public AttachRemoteConfig Remote { get; init; } = AttachRemoteConfig.Default;
    public AttachUpdateConfig Update { get; init; } = AttachUpdateConfig.Default;

    public static AttachClientConfig Default { get; } = new();
}

/// <summary>
/// Remote agent-detection manifest check. Default is on. Set false to disable.
/// Channel and version_check stay out of product.
/// </summary>
public sealed record AttachUpdateConfig
{
    public bool ManifestCheck { get; init; } = true;

    public static AttachUpdateConfig Default { get; } = new();
}

public sealed record AttachSessionConfig
{
    // / <summary>Hypa-native. Default is <c>default</c>.</summary>
    public string Name { get; init; } = "default";

    public bool ResumeAgentsOnRestore { get; init; } = true;

    public static AttachSessionConfig Default { get; } = new();
}

public sealed record AttachAdvancedConfig
{
    public const long DefaultScrollbackLimitBytes = 10_000_000;

    public long ScrollbackLimitBytes { get; init; } = DefaultScrollbackLimitBytes;

    public static AttachAdvancedConfig Default { get; } = new();
}

public sealed record AttachWorktreesConfig
{
    public string Directory { get; init; } = "~/.config/hypa/worktrees";

    public static AttachWorktreesConfig Default { get; } = new();
}

public sealed record AttachRemoteConfig
{
    public bool ManageSshConfig { get; init; } = true;

    public static AttachRemoteConfig Default { get; } = new();
}

public sealed record AttachExperimentalConfig
{
    public bool AllowNested { get; init; }
    public bool KittyGraphics { get; init; }
    /// <summary>
    /// Persist pane screen history to session-history.json. Default false.
    /// The file can hold secrets.
    /// </summary>
    public bool PaneHistory { get; init; }
    public bool RevealHiddenCursorForCjkIme { get; init; }
    public IReadOnlyList<string> CjkImeAgents { get; init; } = [];
    public string CjkImeCursorShape { get; init; } = "steady_block";
    public bool SwitchAsciiInputSourceInPrefix { get; init; }

    public static AttachExperimentalConfig Default { get; } = new();
}
