using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
// / Nested command-hook edits.
/// <c>ensure_command_hook</c> / <c>remove_command_hook</c>.
/// </summary>
public static class IntegrationHookJson
{
    public static JsonObject ParseObject(string content, string description)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new JsonObject();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("failed to parse " + description + ": " + ex.Message);
        }

        if (node is not JsonObject obj)
            throw new InvalidOperationException(description + " must be a JSON object");
        return obj;
    }

    public static JsonObject EnsureHooksObject(JsonObject root, string hooksDescription)
    {
        if (root["hooks"] is JsonObject existing)
            return existing;
        if (root["hooks"] is not null)
            throw new InvalidOperationException(hooksDescription + " must be a JSON object");
        var hooks = new JsonObject();
        root["hooks"] = hooks;
        return hooks;
    }

    public static JsonObject? HooksObjectIfPresent(JsonObject root, string hooksDescription)
    {
        if (root["hooks"] is null)
            return null;
        if (root["hooks"] is JsonObject existing)
            return existing;
        throw new InvalidOperationException(hooksDescription + " must be a JSON object");
    }

    public static void EnsureCommandHook(
        JsonObject hooks,
        string eventName,
        string command,
        int timeout,
        string? matcher)
    {
        var entries = EnsureArray(hooks, eventName);
        foreach (var entry in entries)
        {
            if (entry is not JsonObject obj)
                continue;
            if (obj["hooks"] is not JsonArray hookEntries)
                continue;
            foreach (var hook in hookEntries)
            {
                if (hook is JsonObject hookObj
                    && AsString(hookObj["type"]) == "command"
                    && AsString(hookObj["command"]) == command)
                {
                    return;
                }
            }
        }

        var hookObject = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = timeout,
        };
        var nested = new JsonArray();
        nested.Add((JsonNode)hookObject);
        var wrapper = new JsonObject
        {
            ["hooks"] = nested,
        };
        if (matcher is not null)
            wrapper["matcher"] = matcher;
        entries.Add((JsonNode)wrapper);
    }

    public static bool RemoveCommandHook(JsonObject hooks, string eventName, string command)
    {
        if (hooks[eventName] is null)
            return false;
        if (hooks[eventName] is not JsonArray entries)
            throw new InvalidOperationException("hook entries for " + eventName + " must be an array");

        var removed = false;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is not JsonObject entry)
                continue;
            if (entry["hooks"] is not JsonArray hookEntries)
                continue;
            for (var h = hookEntries.Count - 1; h >= 0; h--)
            {
                if (hookEntries[h] is JsonObject hookObj
                    && AsString(hookObj["type"]) == "command"
                    && AsString(hookObj["command"]) == command)
                {
                    hookEntries.RemoveAt(h);
                    removed = true;
                }
            }

            if (hookEntries.Count == 0)
                entries.RemoveAt(i);
        }

        if (entries.Count == 0)
            hooks.Remove(eventName);
        return removed;
    }

    public static string ToPrettyJson(JsonNode node) => FormatNode(node, 0);

    public static bool RemoveOwnedHookCommands(JsonObject hooks, string hookPath)
    {
        var removed = false;
        foreach (var eventName in OwnedHookEvents)
        {
            foreach (var action in OwnedHookActions)
            {
                var command = IntegrationHookCommand.ForUnix(hookPath, action);
                removed |= RemoveCommandHook(hooks, eventName, command);
            }
        }

        return removed;
    }

    // Unix uses the <c>bash</c> field.</summary>
    public static void EnsureDirectCommandHook(
        JsonObject hooks,
        string eventName,
        string command,
        int timeoutSec,
        string? matcher)
    {
        var entries = EnsureArray(hooks, eventName);
        foreach (var entry in entries)
        {
            if (entry is not JsonObject obj)
                continue;
            if (AsString(obj["type"]) != "command")
                continue;
            if (!IsMatchingDirectCommand(obj, command))
                continue;
            obj.Remove("command");
            obj.Remove("bash");
            obj.Remove("powershell");
            obj["bash"] = command;
            obj["timeoutSec"] = timeoutSec;
            if (matcher is null)
                obj.Remove("matcher");
            else
                obj["matcher"] = matcher;
            return;
        }

        var created = new JsonObject
        {
            ["type"] = "command",
            ["bash"] = command,
            ["timeoutSec"] = timeoutSec,
        };
        if (matcher is not null)
            created["matcher"] = matcher;
        entries.Add((JsonNode)created);
    }

    public static bool RemoveDirectCommandHook(JsonObject hooks, string eventName, string command)
    {
        if (hooks[eventName] is null)
            return false;
        if (hooks[eventName] is not JsonArray entries)
            throw new InvalidOperationException("hook entries for " + eventName + " must be an array");

        var removed = false;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is JsonObject obj
                && AsString(obj["type"]) == "command"
                && IsMatchingDirectCommand(obj, command))
            {
                entries.RemoveAt(i);
                removed = true;
            }
        }

        if (entries.Count == 0)
            hooks.Remove(eventName);
        return removed;
    }

    public static bool RemoveDirectHookCommands(
        JsonObject hooks,
        string eventName,
        string hookPath,
        string? action)
    {
        return RemoveDirectCommandHook(hooks, eventName, IntegrationHookCommand.ForUnix(hookPath, action));
    }

    public static bool RemoveNestedHookCommands(
        JsonObject hooks,
        string eventName,
        string hookPath,
        string? action)
    {
        return RemoveCommandHook(hooks, eventName, IntegrationHookCommand.ForUnix(hookPath, action));
    }

    public static void EnsureSimpleCommandHook(JsonObject hooks, string eventName, string command)
    {
        var entries = EnsureArray(hooks, eventName);
        foreach (var entry in entries)
        {
            if (entry is JsonObject obj && AsString(obj["command"]) == command)
                return;
        }

        entries.Add((JsonNode)new JsonObject { ["command"] = command });
    }

    public static bool RemoveSimpleCommandHook(JsonObject hooks, string eventName, string command)
    {
        if (hooks[eventName] is null)
            return false;
        if (hooks[eventName] is not JsonArray entries)
            throw new InvalidOperationException("hook entries for " + eventName + " must be an array");

        var removed = false;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is JsonObject obj && AsString(obj["command"]) == command)
            {
                entries.RemoveAt(i);
                removed = true;
            }
        }

        if (entries.Count == 0)
            hooks.Remove(eventName);
        return removed;
    }

    public static void EnsureFlatCommandHook(
        JsonObject hooks,
        string eventName,
        string command,
        int timeoutMs,
        string description)
    {
        var entries = EnsureArray(hooks, eventName);
        foreach (var entry in entries)
        {
            if (entry is JsonObject obj
                && AsString(obj["type"]) == "command"
                && AsString(obj["command"]) == command)
            {
                return;
            }
        }

        entries.Add((JsonNode)new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = timeoutMs,
            ["description"] = description,
        });
    }

    public static bool RemoveFlatCommandHook(JsonObject hooks, string eventName, string command)
    {
        if (hooks[eventName] is null)
            return false;
        if (hooks[eventName] is not JsonArray entries)
            throw new InvalidOperationException("hook entries for " + eventName + " must be an array");

        var removed = false;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is JsonObject obj
                && AsString(obj["type"]) == "command"
                && AsString(obj["command"]) == command)
            {
                entries.RemoveAt(i);
                removed = true;
            }
        }

        if (entries.Count == 0)
            hooks.Remove(eventName);
        return removed;
    }

    private static bool IsMatchingDirectCommand(JsonObject entry, string command) =>
        AsString(entry["command"]) == command
        || AsString(entry["bash"]) == command
        || AsString(entry["powershell"]) == command;

    private static readonly string[] OwnedHookEvents =
    [
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PostToolUseFailure",
        "SubagentStop",
        "SubagentStart",
        "PermissionRequest",
        "Stop",
        "SessionEnd",
    ];

    private static readonly string[] OwnedHookActions =
        ["session", "idle", "working", "blocked", "release"];

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonArray EnsureArray(JsonObject hooks, string eventName)
    {
        if (hooks[eventName] is JsonArray existing)
            return existing;
        if (hooks[eventName] is not null)
            throw new InvalidOperationException("hook entries for " + eventName + " must be an array");
        var created = new JsonArray();
        hooks[eventName] = created;
        return created;
    }

    private static string FormatNode(JsonNode? node, int depth) =>
        node switch
        {
            JsonObject obj => FormatObject(obj, depth),
            JsonArray arr => FormatArray(arr, depth),
            JsonValue value => value.ToJsonString(),
            null => "null",
            _ => node.ToJsonString(),
        };

    private static string FormatObject(JsonObject obj, int depth)
    {
        if (obj.Count == 0)
            return "{}";
        var inner = depth + 1;
        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;
        foreach (var kv in obj)
        {
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append('\n')
                .Append(Indent(inner))
                .Append(QuoteJsonString(kv.Key))
                .Append(": ")
                .Append(FormatNode(kv.Value, inner));
        }

        sb.Append('\n').Append(Indent(depth)).Append('}');
        return sb.ToString();
    }

    private static string FormatArray(JsonArray arr, int depth)
    {
        if (arr.Count == 0)
            return "[]";
        var inner = depth + 1;
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < arr.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append('\n')
                .Append(Indent(inner))
                .Append(FormatNode(arr[i], inner));
        }

        sb.Append('\n').Append(Indent(depth)).Append(']');
        return sb.ToString();
    }

    private static string Indent(int depth) => new(' ', depth * 2);

    private static string QuoteJsonString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
