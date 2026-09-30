using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Envelopes;

/// <summary>
/// NDJSON request envelope: <c>{ "id", "method", "params" }</c>.
/// </summary>
public sealed record RpcRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

    /// <summary>
    /// Optional plugin grant token. Plugin processes inject
    /// <c>HYPA_PLUGIN_GRANT_TOKEN</c>. User CLI omits this field.
    /// </summary>
    [JsonPropertyName("grant_token")]
    public string? GrantToken { get; init; }
}
