using Hypa.AgentRuntime.Application;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Builds the child environment map for hosted helper path and experimental Unix PTY.
/// Hosted mode never dumps the full parent environment.
/// </summary>
public static class ChildEnvironmentBuilder
{
    public static IReadOnlyDictionary<string, string> Build(
        ChildEnvironmentMode mode,
        IReadOnlyDictionary<string, string>? explicitEnv = null,
        IReadOnlyDictionary<string, string>? parentEnv = null)
    {
        parentEnv ??= CaptureParentEnvironment();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (mode == ChildEnvironmentMode.LocalUnmanaged)
        {
            foreach (var kv in parentEnv)
            {
                if (ChildEnvironmentPolicy.IsSecretKey(kv.Key))
                    continue;
                result[kv.Key] = kv.Value;
            }
        }
        else
        {
            // Hosted: only base + ContextAbi keys from parent (deny-by-default).
            foreach (var key in ChildEnvironmentPolicy.BaseKeys)
            {
                if (parentEnv.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                    result[key] = value;
            }

            foreach (var key in ChildEnvironmentPolicy.ContextAbiKeys)
            {
                if (parentEnv.TryGetValue(key, out var value) && value is not null)
                    result[key] = value;
            }
        }

        // Nested hypa must not inherit the outer pane id. Real panes set their
        // own via explicit env. Popups strip those keys before this overlay.
        result.Remove(PaneIdEnvironment.HypaPaneId);
        result.Remove(PaneIdEnvironment.HerdrPaneId);
        result.Remove(PaneIdEnvironment.HypaTabId);
        result.Remove(PaneIdEnvironment.HypaWorkspaceId);

        // The pane VT draws the child, not the host TTY.
        result["TERM"] = ChildEnvironmentPolicy.DefaultTerm;
        result["COLORTERM"] = ChildEnvironmentPolicy.DefaultColorTerm;

        if (explicitEnv is not null)
        {
            foreach (var kv in explicitEnv)
                result[kv.Key] = kv.Value;
        }

        ApplyDefaults(result, parentEnv);
        return result;
    }

    /// <summary>
    /// Hosted builder used by hypa-pty-host Spawn frames and experimental Unix PTY.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildHosted(
        IReadOnlyDictionary<string, string>? explicitEnv = null,
        IReadOnlyDictionary<string, string>? parentEnv = null)
        => Build(ChildEnvironmentMode.Hosted, explicitEnv, parentEnv);

    public static IReadOnlyDictionary<string, string> BuildLocalUnmanaged(
        IReadOnlyDictionary<string, string>? explicitEnv = null,
        IReadOnlyDictionary<string, string>? parentEnv = null)
        => Build(ChildEnvironmentMode.LocalUnmanaged, explicitEnv, parentEnv);

    private static void ApplyDefaults(
        Dictionary<string, string> env,
        IReadOnlyDictionary<string, string> parentEnv)
    {
        if (!env.ContainsKey("LANG"))
            env["LANG"] = ChildEnvironmentPolicy.DefaultLang;

        if (!env.ContainsKey("HOME"))
        {
            if (parentEnv.TryGetValue("HOME", out var home) && !string.IsNullOrEmpty(home))
                env["HOME"] = home;
            else
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                env["HOME"] = string.IsNullOrEmpty(profile) ? "/" : profile;
            }
        }

        if (!env.ContainsKey("USER") || !env.ContainsKey("LOGNAME"))
        {
            var user = env.GetValueOrDefault("USER")
                ?? env.GetValueOrDefault("LOGNAME")
                ?? Environment.UserName;
            if (!string.IsNullOrEmpty(user))
            {
                env.TryAdd("USER", user);
                env.TryAdd("LOGNAME", user);
            }
        }

        if (!env.ContainsKey("PATH"))
        {
            if (parentEnv.TryGetValue("PATH", out var path) && !string.IsNullOrEmpty(path))
                env["PATH"] = path;
            else
                env["PATH"] = ChildEnvironmentPolicy.DefaultPath;
        }

        // Nested attach guard. Do not dump parent env.
        env["HYPA_ENV"] = "1";
    }

    private static Dictionary<string, string> CaptureParentEnvironment()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
                map[k] = v;
        }

        return map;
    }
}
