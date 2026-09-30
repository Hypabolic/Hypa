namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
/// TOML edits for Codex features.hooks and the Kimi hook block.
/// <c>build_codex_config_with_hooks</c> / <c>build_kimi_config_with_hooks</c>.
/// </summary>
public static class IntegrationTomlEdit
{
    public static string BuildCodexConfigWithHooks(string content)
    {
        var lines = SplitKeep(content, out var trailingNewline);
        var inFeatures = false;
        int? featuresHeader = null;
        int? hooksIndex = null;
        var deprecated = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var header = TomlTableHeader(lines[i]);
            if (header is not null)
            {
                inFeatures = header == "[features]";
                if (inFeatures && featuresHeader is null)
                    featuresHeader = i;
                continue;
            }

            if (!inFeatures)
                continue;
            if (IsTomlKey(lines[i], "codex_hooks"))
                deprecated.Add(i);
            else if (IsTomlKey(lines[i], "hooks"))
                hooksIndex = i;
        }

        if (hooksIndex is int existing)
            lines[existing] = "hooks = true";
        for (var i = deprecated.Count - 1; i >= 0; i--)
            lines.RemoveAt(deprecated[i]);

        if (hooksIndex is null)
        {
            if (featuresHeader is int header)
            {
                lines.Insert(header + 1, "hooks = true");
                return Join(lines, trailingNewline);
            }

            var result = content.TrimEnd('\n');
            if (result.Length > 0)
                result += "\n\n";
            result += "[features]\nhooks = true\n";
            return result;
        }

        return Join(lines, trailingNewline);
    }

    public static string BuildKimiConfigWithHooks(string content, string hookPath)
    {
        var result = RemoveKimiConfigBlock(content).TrimEnd('\n');
        if (result.Length > 0)
            result += "\n\n";
        result += OfficialIntegrationLayout.KimiBlockBegin + "\n";
        foreach (var (eventName, matcher, action) in KimiHookEvents)
        {
            result += KimiHookTable(eventName, matcher, hookPath, action);
        }

        result += OfficialIntegrationLayout.KimiBlockEnd + "\n";
        return result;
    }

    public static string RemoveKimiConfigBlock(string content)
    {
        var trailingNewline = content.EndsWith('\n');
        var lines = new List<string>();
        var inBlock = false;
        var removed = false;
        foreach (var line in content.Split('\n'))
        {
            if (line.Trim() == OfficialIntegrationLayout.KimiBlockBegin)
            {
                inBlock = true;
                removed = true;
                continue;
            }

            if (inBlock)
            {
                if (line.Trim() == OfficialIntegrationLayout.KimiBlockEnd)
                    inBlock = false;
                continue;
            }

            lines.Add(line);
        }

        if (!removed)
            return content;

        var result = Join(lines, trailingNewline);
        while (result.EndsWith("\n\n", StringComparison.Ordinal))
            result = result[..^1];
        return result == "\n" ? "" : result;
    }

    private static readonly (string Event, string? Matcher, string Action)[] KimiHookEvents =
    [
        ("SessionStart", null, "session"),
        ("UserPromptSubmit", null, "working"),
        ("PreToolUse", OfficialIntegrationLayout.OtherToolMatcher, "working"),
        ("PreToolUse", OfficialIntegrationLayout.AskUserQuestionMatcher, "blocked"),
        ("PostToolUse", OfficialIntegrationLayout.AskUserQuestionMatcher, "working"),
        ("PostToolUseFailure", OfficialIntegrationLayout.AskUserQuestionMatcher, "working"),
        ("SubagentStart", null, "working"),
        ("PreCompact", null, "working"),
        ("PermissionRequest", null, "blocked"),
        ("PermissionResult", null, "working"),
        ("Stop", null, "idle"),
        ("Interrupt", null, "idle"),
    ];

    private static string KimiHookTable(string eventName, string? matcher, string hookPath, string action)
    {
        var command = IntegrationHookCommand.ForUnix(hookPath, action);
        var matcherLine = matcher is null
            ? ""
            : "matcher = " + TomlBasicString(matcher) + "\n";
        return "[[hooks]]\nevent = " + TomlBasicString(eventName) + "\n"
            + matcherLine
            + "command = " + TomlBasicString(command) + "\ntimeout = 10\n\n";
    }

    private static string TomlBasicString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// may have a trailing comment after <c>]</c>.
    /// </summary>
    private static string? TomlTableHeader(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#') || !trimmed.StartsWith('['))
            return null;

        int headerEnd;
        if (trimmed.StartsWith("[[", StringComparison.Ordinal))
        {
            var close = trimmed.IndexOf("]]", StringComparison.Ordinal);
            if (close < 0)
                return null;
            headerEnd = close + 2;
        }
        else
        {
            var close = trimmed.IndexOf(']');
            if (close < 0)
                return null;
            headerEnd = close + 1;
        }

        var header = trimmed[..headerEnd];
        var rest = trimmed[headerEnd..].TrimStart();
        if (rest.Length > 0 && !rest.StartsWith('#'))
            return null;
        return header;
    }

    /// <summary>
    /// requires <c>=</c> after optional whitespace.
    /// </summary>
    private static bool IsTomlKey(string line, string key)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('#') || !trimmed.StartsWith(key, StringComparison.Ordinal))
            return false;
        return trimmed[key.Length..].TrimStart().StartsWith('=');
    }

    private static List<string> SplitKeep(string content, out bool trailingNewline)
    {
        trailingNewline = content.EndsWith('\n');
        if (content.Length == 0)
            return [];
        var parts = content.Split('\n');
        var list = new List<string>(parts.Length);
        var last = parts.Length;
        if (trailingNewline && last > 0 && parts[last - 1].Length == 0)
            last--;
        for (var i = 0; i < last; i++)
            list.Add(parts[i]);
        return list;
    }

    private static string Join(List<string> lines, bool trailingNewline)
    {
        var result = string.Join('\n', lines);
        if (trailingNewline || result.Length == 0)
            result += "\n";
        return result;
    }
}
