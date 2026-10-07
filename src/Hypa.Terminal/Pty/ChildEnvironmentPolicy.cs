namespace Hypa.Terminal.Pty;

/// <summary>
/// Hosted vs local environment inheritance mode for pane children.
/// Hosted is deny-by-default (design §12.4). Local unmanaged may inherit with secret strip.
/// </summary>
public enum ChildEnvironmentMode
{
    /// <summary>Deny-by-default allow-list. Never full parent dump.</summary>
    Hosted = 0,

    /// <summary>Inherit parent env after stripping secret-like keys, then apply overlays.</summary>
    LocalUnmanaged = 1,
}

/// <summary>
/// Allow-list rules for child process environments on the helper / hosted path.
/// </summary>
public static class ChildEnvironmentPolicy
{
    /// <summary>
    /// Login-session identity a terminal always provides. Agents need these:
    /// Claude Code, for one, looks up its macOS keychain entry by <c>USER</c>.
    /// </summary>
    public static readonly string[] BaseKeys =
    [
        "PATH",
        "HOME",
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "USER",
        "LOGNAME",
        "SHELL",
        "TMPDIR",
        "SSH_AUTH_SOCK",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_STATE_HOME",
        "XDG_CACHE_HOME",
        "XDG_RUNTIME_DIR",
    ];

    public static readonly string[] ContextAbiKeys =
    [
        "ATOMIC_CONTEXT_PACK_PATH",
        "ATOMIC_CONTEXT_PACK_ID",
        "ATOMIC_CONTEXT_PACK_SHA256",
        "ATOMIC_CONTEXT_ABI",
        "ATOMIC_RUN_ID",
        "ATOMIC_STEP_ID",
        "ATOMIC_AGENT_SESSION_ID",
    ];

    public const string DefaultTerm = "xterm-256color";
    public const string DefaultColorTerm = "truecolor";
    public const string DefaultLang = "C.UTF-8";
    public const string DefaultPath = "/usr/bin:/bin";

    public static bool IsSecretKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        if (string.Equals(key, "PASSWORD", StringComparison.OrdinalIgnoreCase))
            return true;

        return key.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBaseKey(string key) =>
        BaseKeys.Any(b => string.Equals(b, key, StringComparison.Ordinal));

    public static bool IsContextAbiKey(string key) =>
        ContextAbiKeys.Any(b => string.Equals(b, key, StringComparison.Ordinal));

    /// <summary>
    /// Hosted mode: key is allowed from parent only if base or ContextAbi and not secret-shaped.
    /// Explicit <paramref name="explicitEnv"/> keys are always allowed (caller-provided).
    /// </summary>
    public static bool IsAllowedHostedKey(string key, IReadOnlyDictionary<string, string>? explicitEnv)
    {
        if (explicitEnv is not null && explicitEnv.ContainsKey(key))
            return true;

        if (IsSecretKey(key))
            return false;

        return IsBaseKey(key) || IsContextAbiKey(key);
    }
}
