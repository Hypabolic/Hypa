using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Envelopes;

/// <summary>
/// NDJSON event envelope: <c>{ "event", "params" }</c>.
/// P0 runtime events use <c>event</c> = <c>runtime.event</c>.
/// </summary>
public sealed record RpcEvent
{
    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}
