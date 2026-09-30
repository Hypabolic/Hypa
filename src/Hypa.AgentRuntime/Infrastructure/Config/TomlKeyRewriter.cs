using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using static Hypa.AgentRuntime.Infrastructure.Config.TomlSubsetParser;

namespace Hypa.AgentRuntime.Infrastructure.Config;

/// <summary>
/// Comment-preserving upsert for settings keys. Bind after rewrite. Fail closed.
/// </summary>
public static class TomlKeyRewriter
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "onboarding",
        "theme.name",
        "theme.auto_switch",
        "ui.status_indicators",
        "ui.show_agent_labels_on_pane_borders",
        "ui.sound.enabled",
        "ui.toast.delivery",
        "ui.startup_splash",
    };

    public static AttachConfigResult<string> Upsert(
        string text,
        IReadOnlyList<AttachConfigAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Count == 0)
            return AttachConfigResult<string>.Ok(text);

        foreach (var assignment in assignments)
        {
            if (assignment is null
                || string.IsNullOrWhiteSpace(assignment.Path)
                || !Allowed.Contains(assignment.Path)
                || string.IsNullOrWhiteSpace(assignment.TomlLiteral))
            {
                return AttachConfigResult<string>.Fail(
                    AttachConfigError.Value(
                        assignment?.Path ?? "patch",
                        "Refusing to rewrite attach config: path is not a settings key.",
                        line: null));
            }
        }

        var syntax = Parse(text);
        if (!syntax.IsOk)
            return AttachConfigResult<string>.Fail(syntax.Errors);

        if (HasBlockingInlineTable(syntax.Value, assignments))
        {
            return AttachConfigResult<string>.Fail(
                new AttachConfigError(
                    AttachConfigError.UnsafeRewrite,
                    "Refusing to rewrite attach config: inline table would change.",
                    "theme",
                    Line: null));
        }

        var rewritten = Apply(text, assignments);
        var after = TomlAttachConfigBinder.Bind(rewritten);
        if (!after.IsOk)
        {
            return AttachConfigResult<string>.Fail(
                AttachConfigError.Value(
                    "patch",
                    "Refusing to rewrite attach config: result would be invalid TOML.",
                    line: null));
        }

        return AttachConfigResult<string>.Ok(rewritten);
    }

    private static bool HasBlockingInlineTable(
        TomlDocument document,
        IReadOnlyList<AttachConfigAssignment> assignments)
    {
        foreach (var assignment in document.Assignments)
        {
            if (assignment.Value is not TomlInlineTableValue)
                continue;
            foreach (var patch in assignments)
            {
                if (patch.Path.Equals(assignment.Path, StringComparison.Ordinal)
                    || patch.Path.StartsWith(assignment.Path + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Apply(string text, IReadOnlyList<AttachConfigAssignment> assignments)
    {
        var pending = new Dictionary<string, List<AttachConfigAssignment>>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            var table = TableOf(assignment.Path);
            if (!pending.TryGetValue(table, out var list))
            {
                list = [];
                pending[table] = list;
            }

            list.Add(assignment);
        }

        var unmatched = new HashSet<string>(
            assignments.Select(a => a.Path),
            StringComparer.Ordinal);
        var lines = SplitKeep(text);
        var output = new List<string>(lines.Count + assignments.Count + 4);
        var current = "";
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        foreach (var line in lines)
        {
            if (TryHeader(line.AsSpan(), out _, out var name))
            {
                FlushTable(output, current, pending, unmatched, nl);
                current = name;
                output.Add(line);
                continue;
            }

            if (TryReplace(line, current, pending, unmatched, out var replaced))
            {
                output.Add(replaced);
                continue;
            }

            output.Add(line);
        }

        FlushTable(output, current, pending, unmatched, nl);
        AppendMissingTables(output, pending, unmatched, nl);
        return string.Concat(output);
    }

    private static void FlushTable(
        List<string> output,
        string table,
        Dictionary<string, List<AttachConfigAssignment>> pending,
        HashSet<string> unmatched,
        string nl)
    {
        if (!pending.TryGetValue(table, out var list))
            return;

        foreach (var assignment in list)
        {
            if (!unmatched.Remove(assignment.Path))
                continue;
            EnsureTrailingNewline(output, nl);
            output.Add(KeyOf(assignment.Path) + " = " + assignment.TomlLiteral + nl);
        }
    }

    private static void AppendMissingTables(
        List<string> output,
        Dictionary<string, List<AttachConfigAssignment>> pending,
        HashSet<string> unmatched,
        string nl)
    {
        foreach (var (table, list) in pending)
        {
            var remaining = list.Where(a => unmatched.Contains(a.Path)).ToList();
            if (remaining.Count == 0)
                continue;

            EnsureTrailingNewline(output, nl);
            if (output.Count > 0 && !EndsWithBlank(output, nl))
                output.Add(nl);
            if (table.Length > 0)
                output.Add("[" + table + "]" + nl);
            foreach (var assignment in remaining)
            {
                unmatched.Remove(assignment.Path);
                output.Add(KeyOf(assignment.Path) + " = " + assignment.TomlLiteral + nl);
            }
        }
    }

    private static bool TryReplace(
        string line,
        string table,
        Dictionary<string, List<AttachConfigAssignment>> pending,
        HashSet<string> unmatched,
        out string replaced)
    {
        replaced = line;
        if (!TryAssignmentKey(line.AsSpan(), out var key, out var equals, out var comment))
            return false;

        if (!TryMatch(table, key, pending, unmatched, out var assignment))
            return false;

        unmatched.Remove(assignment.Path);
        var prefix = line[..(equals + 1)];
        if (prefix.Length == 0 || prefix[^1] != ' ')
            prefix += " ";
        replaced = prefix + assignment.TomlLiteral + comment;
        if (line.EndsWith('\n') && !replaced.EndsWith('\n'))
            replaced += line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return true;
    }

    private static bool TryMatch(
        string table,
        string key,
        Dictionary<string, List<AttachConfigAssignment>> pending,
        HashSet<string> unmatched,
        out AttachConfigAssignment assignment)
    {
        assignment = null!;
        foreach (var (owner, list) in pending)
        {
            foreach (var item in list)
            {
                if (!unmatched.Contains(item.Path))
                    continue;
                if (Matches(table, key, owner, item.Path))
                {
                    assignment = item;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Matches(string table, string key, string owner, string path)
    {
        if (table.Equals(owner, StringComparison.Ordinal)
            && key.Equals(KeyOf(path), StringComparison.Ordinal))
        {
            return true;
        }

        if (key.Equals(path, StringComparison.Ordinal) && table.Length == 0)
            return true;

        var dotted = table.Length == 0 ? key : table + "." + key;
        return dotted.Equals(path, StringComparison.Ordinal);
    }

    private static string TableOf(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 ? "" : path[..dot];
    }

    private static string KeyOf(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 ? path : path[(dot + 1)..];
    }

    private static void EnsureTrailingNewline(List<string> output, string nl)
    {
        if (output.Count == 0)
            return;
        var last = output[^1];
        if (last.Length == 0 || last[^1] != '\n')
            output.Add(nl);
    }

    private static bool EndsWithBlank(List<string> output, string nl)
    {
        if (output.Count == 0)
            return true;
        var last = output[^1];
        return last == "\n" || last == "\r\n" || last == nl;
    }

    private static bool TryAssignmentKey(
        ReadOnlySpan<char> line,
        out string key,
        out int equals,
        out string comment)
    {
        key = "";
        equals = -1;
        comment = "";
        var span = line;
        var start = 0;
        while (start < span.Length && (span[start] is ' ' or '\t'))
            start++;
        if (start >= span.Length || span[start] is '#' or '[' or '\n' or '\r')
            return false;

        var eq = IndexOfUnquoted(span, '=');
        if (eq <= start)
            return false;

        var name = span[start..eq].Trim();
        if (name.Length == 0)
            return false;
        key = name.ToString();
        equals = eq;
        comment = TrailingComment(span, eq + 1);
        return true;
    }

    private static string TrailingComment(ReadOnlySpan<char> line, int valueStart)
    {
        var i = valueStart;
        while (i < line.Length && (line[i] is ' ' or '\t'))
            i++;
        if (i >= line.Length)
            return "";

        var end = i;
        if (line[i] is '"' or '\'')
        {
            var quote = line[i];
            end = i + 1;
            while (end < line.Length)
            {
                var c = line[end];
                if (c == '\\' && quote == '"' && end + 1 < line.Length)
                {
                    end += 2;
                    continue;
                }

                if (c == quote)
                {
                    end++;
                    break;
                }

                if (c is '\n' or '\r')
                    break;
                end++;
            }
        }
        else
        {
            while (end < line.Length && line[end] is not '#' and not '\n' and not '\r')
                end++;
        }

        if (end >= line.Length)
            return "";
        return line[end..].ToString();
    }

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
