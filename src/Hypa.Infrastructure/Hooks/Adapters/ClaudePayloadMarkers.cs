using System.Text.Json;

namespace Hypa.Infrastructure.Hooks.Adapters;

/// <summary>
/// Fields that appear on real Claude Code hook envelopes but not on Copilot CLI
/// PascalCase PreToolUse payloads that reuse hook_event_name/tool_name/tool_input.
/// </summary>
internal static class ClaudePayloadMarkers
{
    public static bool HasClaudeMarker(JsonElement json) =>
        IsNonEmptyStringProperty(json, "transcript_path") ||
        IsNonEmptyStringProperty(json, "permission_mode") ||
        IsNonEmptyStringProperty(json, "tool_use_id");

    private static bool IsNonEmptyStringProperty(JsonElement json, string name) =>
        json.TryGetProperty(name, out var el) &&
        el.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(el.GetString());
}
