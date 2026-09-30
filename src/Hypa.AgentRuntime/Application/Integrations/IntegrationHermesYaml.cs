namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
/// Hermes <c>config.yaml</c> enabled-plugin edit.
/// </summary>
public static class IntegrationHermesYaml
{
    public const string PluginName = OfficialIntegrationLayout.HermesPluginDirName;

    public static string EnsureEnabled(string content) => Update(content, enabled: true);

    public static string RemoveEnabled(string content) => Update(content, enabled: false);

    public static string Update(string content, bool enabled)
    {
        var trailingNewline = content.EndsWith('\n');
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (content.EndsWith('\n') && lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var pluginsIndex = TopLevelKeyIndex(lines, "plugins");
        if (pluginsIndex is null)
        {
            if (!enabled)
                return content;
            var result = content.TrimEnd('\n');
            if (result.Length > 0)
                result += "\n";
            result += "plugins:\n  enabled:\n    - " + PluginName + "\n";
            return result;
        }

        var pluginsEnd = NextTopLevelKeyIndex(lines, pluginsIndex.Value + 1) ?? lines.Count;
        var pluginsInlineItems = YamlKeyValueAtIndent(lines[pluginsIndex.Value], 0, "plugins")
            is string inline
            ? YamlFlowSequenceItems(inline)
            : null;
        var enabledIndexOffset = IndexOf(
            lines,
            pluginsIndex.Value + 1,
            pluginsEnd,
            line => YamlKeyAtIndent(line, 2) == "enabled");
        int? enabledIndex = enabledIndexOffset is int eoff
            ? pluginsIndex.Value + 1 + eoff
            : null;
        var flatListOffset = IndexOf(
            lines,
            pluginsIndex.Value + 1,
            pluginsEnd,
            line => YamlListItemValueAtIndent(line, 2) is not null);
        int? flatListStart = flatListOffset is int foff
            ? pluginsIndex.Value + 1 + foff
            : null;

        if (enabledIndex is int enabledAt)
        {
            var line = lines[enabledAt].Trim();
            if (line is "enabled: []" or "enabled: [] # hypa" or "enabled: [] # herdr")
            {
                if (enabled)
                {
                    lines[enabledAt] = "  enabled:";
                    lines.Insert(enabledAt + 1, "    - " + PluginName);
                }

                return Join(lines, trailingNewline);
            }

            var listStart = enabledAt + 1;
            var listEndOffset = IndexOf(
                lines,
                listStart,
                pluginsEnd,
                candidate => YamlIndent(candidate) is int indent
                    && indent <= 2
                    && YamlKeyName(candidate) is not null);
            var listEnd = listEndOffset is int loff ? listStart + loff : pluginsEnd;
            var existingOffset = IndexOf(
                lines,
                listStart,
                listEnd,
                candidate => YamlListItemMatches(candidate, PluginName));
            int? existingItem = existingOffset is int xoff ? listStart + xoff : null;
            switch (enabled, existingItem)
            {
                case (true, not null):
                case (false, null):
                    return content;
                case (true, null):
                    lines.Insert(listStart, "    - " + PluginName);
                    break;
                case (false, int index):
                    lines.RemoveAt(index);
                    break;
            }

            return Join(lines, trailingNewline);
        }

        if (pluginsInlineItems is List<string> items)
        {
            var existingItemIndex = items.FindIndex(item => item == PluginName);
            switch (enabled, existingItemIndex >= 0)
            {
                case (true, true):
                case (false, false):
                    return content;
                case (true, false):
                    items.Insert(0, PluginName);
                    break;
                case (false, true):
                    items.RemoveAt(existingItemIndex);
                    break;
            }

            lines.RemoveRange(pluginsIndex.Value, pluginsEnd - pluginsIndex.Value);
            lines.InsertRange(pluginsIndex.Value, FlatPluginLines(items));
            return Join(lines, trailingNewline);
        }

        if (flatListStart is int flatStart)
        {
            var existingOffset = IndexOf(
                lines,
                pluginsIndex.Value + 1,
                pluginsEnd,
                line => YamlListItemMatchesAtIndent(line, 2, PluginName));
            int? existingItem = existingOffset is int xoff
                ? pluginsIndex.Value + 1 + xoff
                : null;
            switch (enabled, existingItem)
            {
                case (true, not null):
                case (false, null):
                    return content;
                case (true, null):
                    lines.Insert(flatStart, "  - " + PluginName);
                    break;
                case (false, int index):
                    lines.RemoveAt(index);
                    break;
            }

            return Join(lines, trailingNewline);
        }

        if (enabled)
        {
            lines.Insert(pluginsIndex.Value + 1, "  enabled:");
            lines.Insert(pluginsIndex.Value + 2, "    - " + PluginName);
            return Join(lines, trailingNewline);
        }

        return content;
    }

    private static IReadOnlyList<string> FlatPluginLines(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return ["plugins: []"];
        var lines = new List<string> { "plugins:" };
        foreach (var item in items)
            lines.Add("  - " + item);
        return lines;
    }

    private static int? TopLevelKeyIndex(IReadOnlyList<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (YamlKeyAtIndent(lines[i], 0) == key)
                return i;
        }

        return null;
    }

    private static int? NextTopLevelKeyIndex(IReadOnlyList<string> lines, int start)
    {
        for (var i = start; i < lines.Count; i++)
        {
            if (YamlIndent(lines[i]) == 0 && YamlKeyName(lines[i]) is not null)
                return i;
        }

        return null;
    }

    private static int? IndexOf(
        IReadOnlyList<string> lines,
        int start,
        int end,
        Func<string, bool> predicate)
    {
        for (var i = start; i < end; i++)
        {
            if (predicate(lines[i]))
                return i - start;
        }

        return null;
    }

    private static string? YamlKeyAtIndent(string line, int indent)
    {
        if (YamlIndent(line) != indent)
            return null;
        return YamlKeyName(line);
    }

    private static string? YamlKeyValueAtIndent(string line, int indent, string key)
    {
        if (YamlIndent(line) != indent)
            return null;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('-'))
            return null;
        var split = trimmed.Split(':', 2);
        if (split.Length != 2 || split[0].Trim() != key)
            return null;
        return split[1].Trim();
    }

    private static string? YamlKeyName(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('-'))
            return null;
        var colon = trimmed.IndexOf(':');
        if (colon <= 0)
            return null;
        var key = trimmed[..colon].Trim();
        return key.Length == 0 ? null : key;
    }

    private static int? YamlIndent(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;
        return line.Length - trimmed.Length;
    }

    private static string? YamlListItemValue(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("- ", StringComparison.Ordinal) ? trimmed[2..].Trim() : null;
    }

    private static bool YamlListItemMatches(string line, string value) =>
        YamlListItemValue(line) is string item && YamlScalarValue(item) == value;

    private static string? YamlListItemValueAtIndent(string line, int indent)
    {
        if (YamlIndent(line) != indent)
            return null;
        return YamlListItemValue(line);
    }

    private static bool YamlListItemMatchesAtIndent(string line, int indent, string value) =>
        YamlListItemValueAtIndent(line, indent) is string item && YamlScalarValue(item) == value;

    private static List<string>? YamlFlowSequenceItems(string value)
    {
        var inner = StripYamlInlineComment(value).Trim();
        if (!inner.StartsWith('[') || !inner.EndsWith(']'))
            return null;
        inner = inner[1..^1].Trim();
        if (inner.Length == 0)
            return [];

        var items = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        var escaped = false;
        foreach (var ch in inner)
        {
            if (quote is char quoteChar)
            {
                current.Append(ch);
                if (quoteChar == '"' && ch == '\\' && !escaped)
                {
                    escaped = true;
                    continue;
                }

                if (ch == quoteChar && !escaped)
                    quote = null;
                escaped = false;
                continue;
            }

            switch (ch)
            {
                case '"' or '\'':
                    quote = ch;
                    current.Append(ch);
                    break;
                case ',':
                    items.Add(YamlScalarValue(current.ToString()));
                    current.Clear();
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (quote is not null)
            return null;
        items.Add(YamlScalarValue(current.ToString()));
        return items;
    }

    private static string YamlScalarValue(string value)
    {
        value = StripYamlInlineComment(value).Trim();
        if (value.Length >= 2)
        {
            var quoted = (value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'');
            if (quoted)
                return value[1..^1];
        }

        return value;
    }

    private static string StripYamlInlineComment(string value)
    {
        char? quote = null;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (quote is char quoteChar)
            {
                if (quoteChar == '"' && ch == '\\' && !escaped)
                {
                    escaped = true;
                    continue;
                }

                if (ch == quoteChar && !escaped)
                    quote = null;
                escaped = false;
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '#' && (index == 0 || char.IsWhiteSpace(value[index - 1])))
                return value[..index].TrimEnd();
        }

        return value;
    }

    private static string Join(IReadOnlyList<string> lines, bool trailingNewline)
    {
        var result = string.Join('\n', lines);
        if (trailingNewline || result.Length == 0)
            result += "\n";
        return result;
    }
}
