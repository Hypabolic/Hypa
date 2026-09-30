using Hypa.AgentRuntime.Domain.AttachConfig;
using static Hypa.AgentRuntime.Infrastructure.Config.TomlSubsetParser;

namespace Hypa.AgentRuntime.Infrastructure.Config;

/// <summary>
/// Comment-preserving removal of [keys], [keys.indexed], and [[keys.command]].
/// Parses TOML syntax only, then strips key tables, then binds the rewrite.
/// Invalid keys.* values must not block recovery. After-bind stays fail-closed.
/// </summary>
public static class KeySectionRewriter
{
    public static AttachConfigResult<KeySectionRewrite> RemoveKeySections(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var syntax = Parse(text);
        if (!syntax.IsOk)
            return AttachConfigResult<KeySectionRewrite>.Fail(syntax.Errors);

        var rewritten = Strip(text);
        var after = TomlAttachConfigBinder.Bind(rewritten);
        if (!after.IsOk)
        {
            return AttachConfigResult<KeySectionRewrite>.Fail(
                AttachConfigError.Value(
                    "keys",
                    "Refusing to rewrite attach config: result would be invalid TOML.",
                    line: null));
        }

        var afterSyntax = Parse(rewritten);
        if (!afterSyntax.IsOk || ThemeOrUiChanged(syntax.Value, afterSyntax.Value))
        {
            return AttachConfigResult<KeySectionRewrite>.Fail(
                new AttachConfigError(
                    AttachConfigError.UnsafeRewrite,
                    "Refusing to rewrite attach config: theme or UI would change.",
                    "keys",
                    Line: null));
        }

        var hadKeys = HasKeyTables(text);
        return AttachConfigResult<KeySectionRewrite>.Ok(new KeySectionRewrite(rewritten, hadKeys));
    }

    public static bool HasKeyTables(string text)
    {
        foreach (var raw in SplitKeep(text))
        {
            if (TryHeader(raw.AsSpan(), out _, out var name)
                && IsKeyTable(name))
            {
                return true;
            }

            if (IsRootKeysAssignment(raw))
                return true;
        }

        return false;
    }

    private static string Strip(string text)
    {
        var lines = SplitKeep(text);
        var kept = new List<string>(lines.Count);
        var skippingTable = false;
        var assignDepth = 0;
        foreach (var line in lines)
        {
            if (assignDepth > 0)
            {
                AddUnquotedDepth(line, ref assignDepth);
                continue;
            }

            if (TryHeader(line.AsSpan(), out _, out var name))
            {
                skippingTable = IsKeyTable(name);
                if (skippingTable)
                    continue;
            }
            else if (!skippingTable && IsRootKeysAssignment(line))
            {
                assignDepth = 0;
                AddUnquotedDepth(line, ref assignDepth);
                continue;
            }

            if (skippingTable)
                continue;
            kept.Add(line);
        }

        return string.Concat(kept);
    }

    private static bool ThemeOrUiChanged(TomlDocument before, TomlDocument after)
    {
        var beforeMap = ProtectedThemeOrUi(before);
        var afterMap = ProtectedThemeOrUi(after);
        if (beforeMap.Count != afterMap.Count)
            return true;

        foreach (var (path, value) in beforeMap)
        {
            if (!afterMap.TryGetValue(path, out var rewritten))
                return true;
            if (!TomlValueEquals(value, rewritten))
                return true;
        }

        return false;
    }

    private static Dictionary<string, TomlValue> ProtectedThemeOrUi(TomlDocument document)
    {
        var map = new Dictionary<string, TomlValue>(StringComparer.Ordinal);
        foreach (var assignment in Flatten(document.Assignments))
        {
            if (!IsProtectedThemeOrUi(assignment.Path))
                continue;
            map[assignment.Path] = assignment.Value;
        }

        return map;
    }

    private static bool IsProtectedThemeOrUi(string path)
    {
        if (IsNestedKeysPath(path))
            return false;
        return path.Equals("theme", StringComparison.Ordinal)
            || path.StartsWith("theme.", StringComparison.Ordinal)
            || path.Equals("ui", StringComparison.Ordinal)
            || path.StartsWith("ui.", StringComparison.Ordinal);
    }

    private static bool IsNestedKeysPath(string path) =>
        path.Equals("theme.keys", StringComparison.Ordinal)
        || path.StartsWith("theme.keys.", StringComparison.Ordinal)
        || path.Equals("ui.keys", StringComparison.Ordinal)
        || path.StartsWith("ui.keys.", StringComparison.Ordinal);

    private static bool TomlValueEquals(TomlValue left, TomlValue right)
    {
        switch (left)
        {
            case TomlStringValue a when right is TomlStringValue b:
                return string.Equals(a.Value, b.Value, StringComparison.Ordinal);
            case TomlBoolValue a when right is TomlBoolValue b:
                return a.Value == b.Value;
            case TomlIntValue a when right is TomlIntValue b:
                return a.Value == b.Value;
            case TomlArrayValue a when right is TomlArrayValue b:
                if (a.Items.Count != b.Items.Count)
                    return false;
                for (var i = 0; i < a.Items.Count; i++)
                {
                    if (!TomlValueEquals(a.Items[i], b.Items[i]))
                        return false;
                }

                return true;
            case TomlInlineTableValue a when right is TomlInlineTableValue b:
                if (a.Fields.Count != b.Fields.Count)
                    return false;
                var rightFields = new Dictionary<string, TomlValue>(StringComparer.Ordinal);
                foreach (var field in b.Fields)
                    rightFields[field.Path] = field.Value;
                foreach (var field in a.Fields)
                {
                    if (!rightFields.TryGetValue(field.Path, out var other)
                        || !TomlValueEquals(field.Value, other))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }

    private static List<TomlAssignment> Flatten(IReadOnlyList<TomlAssignment> assignments)
    {
        var list = new List<TomlAssignment>();
        foreach (var assignment in assignments)
        {
            if (assignment.Value is TomlInlineTableValue inline)
            {
                foreach (var nested in Flatten(inline.Fields))
                {
                    var path = string.IsNullOrEmpty(assignment.Path)
                        ? nested.Path
                        : assignment.Path + "." + nested.Path;
                    list.Add(nested with { Path = path });
                }

                continue;
            }

            list.Add(assignment);
        }

        return list;
    }

    private static bool IsKeyTable(string name) =>
        name.Equals("keys", StringComparison.Ordinal)
        || name.StartsWith("keys.", StringComparison.Ordinal);

    private static bool IsRootKeysAssignment(string line)
    {
        var span = StripLineComment(line.AsSpan().TrimStart());
        if (span.StartsWith("keys.", StringComparison.Ordinal))
            return ContainsUnquoted(span, '=');

        if (!span.StartsWith("keys", StringComparison.Ordinal))
            return false;

        var rest = span[4..];
        if (rest.Length == 0 || rest[0] is not (' ' or '\t' or '='))
            return false;

        rest = rest.TrimStart();
        return rest.Length > 0 && rest[0] == '=';
    }

    private static void AddUnquotedDepth(string line, ref int depth)
    {
        var span = line.AsSpan();
        var inString = false;
        var quote = '\0';
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (inString)
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                    continue;
                }

                if (c == quote)
                    inString = false;
                continue;
            }

            if (c is '"' or '\'')
            {
                inString = true;
                quote = c;
                continue;
            }

            if (c == '#')
                break;
            if (c is '[' or '{')
                depth++;
            else if (c is ']' or '}')
                depth = depth > 0 ? depth - 1 : 0;
        }
    }

    private static ReadOnlySpan<char> StripLineComment(ReadOnlySpan<char> span)
    {
        var hash = IndexOfUnquoted(span, '#');
        return hash >= 0 ? span[..hash] : span;
    }

    private static bool ContainsUnquoted(ReadOnlySpan<char> span, char ch) =>
        IndexOfUnquoted(span, ch) >= 0;

    private static bool TryHeader(ReadOnlySpan<char> line, out bool isArray, out string name)
    {
        isArray = false;
        name = "";
        var span = line.TrimStart();
        var hash = IndexOfUnquoted(span, '#');
        if (hash >= 0)
            span = span[..hash];
        span = span.Trim();
        if (span.Length < 3 || span[0] != '[')
            return false;

        isArray = span.Length >= 4 && span[1] == '[';
        var start = isArray ? 2 : 1;
        var end = span.LastIndexOf(']');
        if (end <= start)
            return false;
        if (isArray && (end == 0 || span[end - 1] != ']'))
            return false;
        if (isArray)
            end--;
        name = span[start..end].Trim().ToString();
        return name.Length > 0;
    }

    private static int IndexOfUnquoted(ReadOnlySpan<char> span, char ch)
    {
        var inString = false;
        var quote = '\0';
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (inString)
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                    continue;
                }

                if (c == quote)
                    inString = false;
                continue;
            }

            if (c is '"' or '\'')
            {
                inString = true;
                quote = c;
                continue;
            }

            if (c == ch)
                return i;
        }

        return -1;
    }

    private static List<string> SplitKeep(string text)
    {
        var list = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            list.Add(text[start..(i + 1)]);
            start = i + 1;
        }

        if (start < text.Length)
            list.Add(text[start..]);
        return list;
    }
}

public sealed record KeySectionRewrite(string Text, bool RemovedKeys);
