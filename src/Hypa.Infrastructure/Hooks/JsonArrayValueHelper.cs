using System.Text.Json.Nodes;

namespace Hypa.Infrastructure.Hooks;

/// <summary>
/// Membership identity for top-level JSON array value ops (e.g. Pi packages[]).
/// Matches a scalar string element, or an object whose string "source" equals the target.
/// Non-string siblings are never treated as errors — they simply do not match.
/// </summary>
internal static class JsonArrayValueHelper
{
    internal static bool ItemMatches(JsonNode? item, string value)
    {
        if (item is null)
            return false;

        if (item is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var stringValue) &&
            stringValue == value)
            return true;

        if (item is JsonObject obj &&
            obj.TryGetPropertyValue("source", out var sourceNode) &&
            sourceNode is JsonValue sourceValue &&
            sourceValue.TryGetValue<string>(out var source) &&
            source == value)
            return true;

        return false;
    }
}
