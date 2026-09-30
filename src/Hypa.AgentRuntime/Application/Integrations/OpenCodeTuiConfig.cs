using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
// / OpenCode <c>tui.jsonc</c> plugin list.
/// Writes JSON. Comments are skipped on read.
/// </summary>
public static class OpenCodeTuiConfig
{
    public static void Validate(string? content, string path)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;
        var root = Parse(content, path);
        if (root["plugin"] is null)
            return;
        if (root["plugin"] is not JsonArray)
            throw new InvalidOperationException(
                "OpenCode TUI config plugin list at " + path + " must be an array");
    }

    public static string AddPlugin(string? content, string path, string spec)
    {
        var root = string.IsNullOrWhiteSpace(content)
            ? new JsonObject()
            : Parse(content, path);
        if (root["plugin"] is null)
        {
            var created = new JsonArray();
            created.Add((JsonNode?)JsonValue.Create(spec));
            root["plugin"] = created;
            return IntegrationHookJson.ToPrettyJson(root) + "\n";
        }

        if (root["plugin"] is not JsonArray plugins)
        {
            throw new InvalidOperationException(
                "OpenCode TUI config plugin list at " + path + " must be an array");
        }

        foreach (var entry in plugins)
        {
            if (Matches(entry, spec))
                return IntegrationHookJson.ToPrettyJson(root) + "\n";
        }

        plugins.Add((JsonNode?)JsonValue.Create(spec));
        return IntegrationHookJson.ToPrettyJson(root) + "\n";
    }

    public static string? RemovePlugin(string? content, string path, string spec)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;
        JsonObject root;
        try
        {
            root = Parse(content, path);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (root["plugin"] is not JsonArray plugins)
            return null;

        var removed = false;
        for (var i = plugins.Count - 1; i >= 0; i--)
        {
            if (!Matches(plugins[i], spec))
                continue;
            plugins.RemoveAt(i);
            removed = true;
        }

        if (!removed)
            return null;
        if (plugins.Count == 0)
            root.Remove("plugin");
        return IntegrationHookJson.ToPrettyJson(root) + "\n";
    }

    public static bool IsConfigured(string? content, string spec)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        try
        {
            var root = Parse(content, "tui.jsonc");
            if (root["plugin"] is not JsonArray plugins)
                return false;
            foreach (var entry in plugins)
            {
                if (Matches(entry, spec))
                    return true;
            }

            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Matches(JsonNode? entry, string spec)
    {
        if (entry is JsonValue value && value.TryGetValue<string>(out var text))
            return text == spec;
        if (entry is JsonArray arr && arr.Count > 0
            && arr[0] is JsonValue first && first.TryGetValue<string>(out var head))
        {
            return head == spec;
        }

        return false;
    }

    private static JsonObject Parse(string content, string path)
    {
        try
        {
            var node = JsonNode.Parse(
                content,
                nodeOptions: null,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            return node as JsonObject
                ?? throw new InvalidOperationException(
                    "OpenCode TUI config at " + path + " must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to parse OpenCode TUI config at " + path + ": " + ex.Message);
        }
    }
}
