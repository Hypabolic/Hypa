using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Envelopes;

/// <summary>
/// NDJSON response envelope: <c>{ "id", "result" }</c> or <c>{ "id", "error" }</c>.
/// </summary>
public sealed record RpcResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public RpcError? Error { get; init; }
}
