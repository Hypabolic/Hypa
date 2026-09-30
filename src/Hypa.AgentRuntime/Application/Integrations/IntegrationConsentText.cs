using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
/// Consent text for one integration install.
/// Paths come from <see cref="OfficialIntegrationLayout"/>.
/// </summary>
public static class IntegrationConsentText
{
    public const string HookPurpose = "a hook so Hypa can see the agent state";
    public const string PluginPurpose = "a plugin so Hypa can see the agent state";
    public const string SkillPurpose = "a skill so the agent knows how to use Hypa panes";
    public const string RestartSentence = "Restart the agent to load the skill.";

    public static IReadOnlyList<string> Lines(IIntegrationEnvironment env, OfficialIntegrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(env);
        var owned = OfficialIntegrationLayout.OwnedFile(env, target);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(env, target);
        var ownedKind = IsHook(owned) ? "hook" : "plugin";
        var ownedPurpose = ownedKind == "hook" ? HookPurpose : PluginPurpose;
        var intro = skill is null
            ? ProductName(target) + " will get " + ownedPurpose + "."
            : ProductName(target) + " will get " + ownedPurpose + " and " + SkillPurpose + ".";
        var lines = new List<string> { intro };
        foreach (var file in OfficialIntegrationLayout.WrittenFiles(env, target))
            lines.Add("  " + KindLabel(file).PadRight(6) + " " + DisplayPath(env, file));
        foreach (var removal in OfficialIntegrationLayout.LegacyRemovals(env, target))
        {
            lines.Add(
                "Hypa removes " + DisplayPath(env, removal.Path) + " " + removal.Condition + ".");
        }

        lines.Add("Remove with: hypa integration uninstall " + target.WireName());
        return lines;
    }

    public static string WithNextStep(OfficialIntegrationTarget target, string reason) =>
        AppendNextStep(target, reason, failure: null);

    public static string WithNextStep(OfficialIntegrationTarget target, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return AppendNextStep(target, failure.Message, failure);
    }

    private static string AppendNextStep(OfficialIntegrationTarget target, string reason, Exception? failure)
    {
        var text = reason.Trim();
        if (text.Contains("press Enter again", StringComparison.Ordinal))
            return text;
        var step = NextStep(target, text, failure);
        return step is null ? text : text + " " + step;
    }

    private static string? NextStep(OfficialIntegrationTarget target, string text, Exception? failure)
    {
        if (text.Contains("directory not found", StringComparison.OrdinalIgnoreCase))
            return "Start " + ProductName(target) + " once, then press Enter again.";
        if (IsAccessDenied(text, failure))
            return "Change the directory permissions, then press Enter again.";
        if (failure is IOException || text.Contains("I/O", StringComparison.OrdinalIgnoreCase))
            return "Check the directory, then press Enter again.";
        if (failure is InvalidOperationException)
            return "Fix the config file, then press Enter again.";
        return null;
    }

    private static bool IsAccessDenied(string text, Exception? failure) =>
        failure is UnauthorizedAccessException
        || text.Contains("permission", StringComparison.OrdinalIgnoreCase)
        || (text.Contains("access", StringComparison.OrdinalIgnoreCase)
            && text.Contains("denied", StringComparison.OrdinalIgnoreCase));

    public static string ProductName(OfficialIntegrationTarget target) =>
        target switch
        {
            OfficialIntegrationTarget.Claude => "Claude Code",
            _ => target.Label(),
        };

    public static string DisplayPath(IIntegrationEnvironment env, string path)
    {
        ArgumentNullException.ThrowIfNull(env);
        var home = env.UserHome;
        if (!string.IsNullOrEmpty(home)
            && path.StartsWith(home, StringComparison.Ordinal)
            && (path.Length == home.Length
                || path[home.Length] is '/' or '\\'))
        {
            var rest = path[home.Length..].TrimStart('/', '\\').Replace('\\', '/');
            return rest.Length == 0 ? "~" : "~/" + rest;
        }

        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Wraps lines to <paramref name="width"/> columns.
    /// A token wider than the width stays whole across rows. No character is dropped.
    /// </summary>
    public static IReadOnlyList<string> Wrap(IReadOnlyList<string> lines, int width)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (width < 1)
            width = 1;
        var wrapped = new List<string>();
        foreach (var line in lines)
            WrapLine(line ?? "", width, wrapped);
        return wrapped;
    }

    private static void WrapLine(string line, int width, List<string> wrapped)
    {
        if (line.Length == 0)
        {
            wrapped.Add("");
            return;
        }

        var index = 0;
        while (index < line.Length)
        {
            var rest = line.Length - index;
            if (rest <= width)
            {
                wrapped.Add(line[index..]);
                return;
            }

            var window = line.AsSpan(index, width);
            var breakAt = window.LastIndexOf(' ');
            if (breakAt <= 0)
            {
                wrapped.Add(line.Substring(index, width));
                index += width;
                continue;
            }

            wrapped.Add(line.Substring(index, breakAt));
            index += breakAt + 1;
        }
    }

    private static bool IsHook(string path) =>
        path.EndsWith(".sh", StringComparison.Ordinal)
        || path.Contains("/hooks/", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}hooks{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string KindLabel(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals(OfficialIntegrationLayout.RuntimeSkillFileName, StringComparison.Ordinal))
            return "skill";
        if (name.EndsWith(".sh", StringComparison.Ordinal))
            return "hook";
        if (name.Equals(OfficialIntegrationLayout.SettingsFileName, StringComparison.Ordinal)
            || name.Equals(OfficialIntegrationLayout.HooksFileName, StringComparison.Ordinal)
            || name.Equals(OfficialIntegrationLayout.ConfigTomlFileName, StringComparison.Ordinal)
            || name.Equals(OfficialIntegrationLayout.ConfigYamlFileName, StringComparison.Ordinal)
            || name.Equals(OfficialIntegrationLayout.ConfigJsonFileName, StringComparison.Ordinal)
            || name.Equals(OfficialIntegrationLayout.OpenCodeTuiConfigFileName, StringComparison.Ordinal)
            || name.Equals(OfficialIntegrationLayout.GrokHookConfigFile, StringComparison.Ordinal))
        {
            return "config";
        }

        return "plugin";
    }
}
