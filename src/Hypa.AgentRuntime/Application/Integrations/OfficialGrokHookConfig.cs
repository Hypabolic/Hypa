using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
// / Grok owned hook config.
/// <c>grok_hook_config</c>.
/// </summary>
internal static class OfficialGrokHookConfig
{
    public static JsonObject Create(string hookPath)
    {
        var nested = new JsonArray();
        nested.Add((JsonNode)new JsonObject
        {
            ["type"] = "command",
            ["command"] = IntegrationHookCommand.ForUnixSh(hookPath, "session"),
            ["timeout"] = 10,
        });
        var session = new JsonArray();
        session.Add((JsonNode)new JsonObject { ["hooks"] = nested });
        var hooks = new JsonObject { ["SessionStart"] = session };
        return new JsonObject { ["hooks"] = hooks };
    }

    public static string Format(string hookPath) =>
        IntegrationHookJson.ToPrettyJson(Create(hookPath)) + "\n";

    public static bool MatchesInstalled(string? content, string hookPath)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        try
        {
            var actual = JsonNode.Parse(content);
            return StructuralEquals(actual, Create(hookPath));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool StructuralEquals(JsonNode? left, JsonNode? right)
    {
        if (left is null && right is null)
            return true;
        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            if (leftObject.Count != rightObject.Count)
                return false;
            foreach (var kv in leftObject)
            {
                if (!rightObject.TryGetPropertyValue(kv.Key, out var other))
                    return false;
                if (!StructuralEquals(kv.Value, other))
                    return false;
            }

            return true;
        }

        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            if (leftArray.Count != rightArray.Count)
                return false;
            for (var i = 0; i < leftArray.Count; i++)
            {
                if (!StructuralEquals(leftArray[i], rightArray[i]))
                    return false;
            }

            return true;
        }

        if (left is JsonValue leftValue && right is JsonValue rightValue)
            return leftValue.ToJsonString() == rightValue.ToJsonString();
        return false;
    }
}
