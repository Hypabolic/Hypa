using System.Text.Json.Serialization;

namespace Hypa.Infrastructure.Mcp.Import;

internal sealed record ClaudeSettingsJson(
    [property: JsonPropertyName("mcpServers")]
    Dictionary<string, ClaudeMcpServerEntry?>? McpServers);

internal sealed record ClaudeMcpServerEntry(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("command")] string? Command,
    [property: JsonPropertyName("args")] string[]? Args,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env);

[JsonSerializable(typeof(ClaudeSettingsJson))]
[JsonSerializable(typeof(ClaudeMcpServerEntry))]
[JsonSerializable(typeof(Dictionary<string, ClaudeMcpServerEntry?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
internal sealed partial class ClaudeSettingsJsonContext : JsonSerializerContext;
